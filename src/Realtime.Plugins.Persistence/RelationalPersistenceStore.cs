using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Cormier.Realtime.Plugins.Persistence;

public enum RelationalDialect { PostgreSql, MySql, MariaDb, SqlServer, AzureSql, Oracle, Sqlite, Db2, CockroachDb, Snowflake }

/// <summary>Owns and disposes a fresh connection per operation. The caller supplies its installed ADO.NET driver.</summary>
public class RelationalPersistenceStore : IRealtimePersistenceStore
{
    private readonly Func<DbConnection> connectionFactory;
    private readonly TimeProvider clock;
    private readonly RelationalDialect dialect;
    private readonly string table;
    private readonly Action<DbCommand>? configureCommand;

    public RelationalPersistenceStore(Func<DbConnection> connectionFactory, RelationalDialect dialect,
        string tableName = "realtime_records", TimeProvider? timeProvider = null, Action<DbCommand>? configureCommand = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        if (tableName.Length > 30 || !char.IsAsciiLetter(tableName[0]) ||
            tableName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Use a simple SQL identifier of at most 30 ASCII characters.", nameof(tableName));
        if (!Enum.IsDefined(dialect)) throw new ArgumentOutOfRangeException(nameof(dialect));
        this.connectionFactory = connectionFactory;
        this.dialect = dialect;
        this.configureCommand = configureCommand;
        table = tableName;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>DDL for an explicit host-managed migration; never runs automatically.</summary>
    public string CreateTableSql
    {
        get
        {
            var text = dialect switch
            {
                RelationalDialect.SqlServer or RelationalDialect.AzureSql => "NVARCHAR(128) COLLATE Latin1_General_100_BIN2",
                RelationalDialect.Oracle => "VARCHAR2(128 CHAR)",
                RelationalDialect.MySql or RelationalDialect.MariaDb => "VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin",
                _ => "VARCHAR(128)"
            };
            var payload = dialect switch
            {
                RelationalDialect.SqlServer or RelationalDialect.AzureSql => "NVARCHAR(MAX)",
                RelationalDialect.Oracle => "CLOB",
                RelationalDialect.Db2 => "CLOB(16M)",
                RelationalDialect.MySql or RelationalDialect.MariaDb => "LONGTEXT",
                _ => "TEXT"
            };
            var number = dialect == RelationalDialect.Oracle ? "NUMBER(19)" : "BIGINT";
            // Snowflake hybrid tables enforce the primary key, unlike standard Snowflake tables.
            var hybrid = dialect == RelationalDialect.Snowflake ? "HYBRID " : "";
            return $"CREATE {hybrid}TABLE {table} (tenant_id {text} NOT NULL, owner_id {text} NOT NULL, " +
                $"stream_id {text} NOT NULL, record_kind INTEGER NOT NULL, sequence_no {number} NOT NULL, " +
                $"payload {payload} NOT NULL, created_at {number} NOT NULL, expires_at {number} NULL, " +
                "PRIMARY KEY (tenant_id, owner_id, stream_id, record_kind, sequence_no))";
        }
    }

    public async Task AppendAsync(DurableStreamKey stream, DurableRecord record, CancellationToken cancellationToken = default)
    {
        Validate(stream);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(record.Payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(record.Sequence, 1);
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var names = new[] { "tenant_id", "owner_id", "stream_id", "record_kind", "sequence_no", "payload", "created_at", "expires_at" };
        var values = new object?[] { stream.TenantId, stream.OwnerId, stream.StreamId, (int)stream.Kind, record.Sequence,
            record.Payload, record.CreatedAt.ToUnixTimeMilliseconds(), record.ExpiresAt?.ToUnixTimeMilliseconds() };
        var types = new[] { DbType.String, DbType.String, DbType.String, DbType.Int32, DbType.Int64, DbType.String, DbType.Int64, DbType.Int64 };
        var parameters = names.Select((_, i) => Add(command, values[i], types[i])).ToArray();
        command.CommandText = $"INSERT INTO {table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", parameters)})";
        configureCommand?.Invoke(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DurableRecord>> ReadAsync(DurableStreamKey stream, long afterSequence = 0, int limit = 100,
        CancellationToken cancellationToken = default) => ReadCoreAsync(stream, afterSequence, limit, false, cancellationToken);

    public async Task<DurableRecord?> ReadLatestAsync(DurableStreamKey stream, CancellationToken cancellationToken = default)
    {
        var records = await ReadCoreAsync(stream, 0, 1, true, cancellationToken).ConfigureAwait(false);
        return records.Count == 0 ? null : records[0];
    }

    private async Task<IReadOnlyList<DurableRecord>> ReadCoreAsync(DurableStreamKey stream, long afterSequence, int limit,
        bool latest, CancellationToken cancellationToken)
    {
        Validate(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var filter = Scope(command, stream);
        var after = Add(command, afterSequence, DbType.Int64);
        var now = Add(command, clock.GetUtcNow().ToUnixTimeMilliseconds(), DbType.Int64);
        var count = limit.ToString(CultureInfo.InvariantCulture);
        var top = dialect is RelationalDialect.SqlServer or RelationalDialect.AzureSql ? $"TOP ({count}) " : "";
        var tail = dialect switch
        {
            RelationalDialect.SqlServer or RelationalDialect.AzureSql => "",
            RelationalDialect.Oracle or RelationalDialect.Db2 => $" FETCH FIRST {count} ROWS ONLY",
            _ => $" LIMIT {count}"
        };
        command.CommandText = $"SELECT {top}sequence_no, payload, created_at, expires_at FROM {table} WHERE {filter} " +
            $"AND sequence_no > {after} AND (expires_at IS NULL OR expires_at > {now}) ORDER BY sequence_no " +
            (latest ? "DESC" : "ASC") + tail;
        configureCommand?.Invoke(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DurableRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new DurableRecord(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)),
                reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture))));
        return result;
    }

    public async Task AcknowledgeAsync(DurableStreamKey stream, long throughSequence, CancellationToken cancellationToken = default)
    {
        Validate(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(throughSequence);
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var filter = Scope(command, stream);
        command.CommandText = $"DELETE FROM {table} WHERE {filter} AND sequence_no <= {Add(command, throughSequence, DbType.Int64)}";
        configureCommand?.Invoke(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {table} WHERE expires_at <= {Add(command, clock.GetUtcNow().ToUnixTimeMilliseconds(), DbType.Int64)}";
        configureCommand?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private string Scope(DbCommand command, DurableStreamKey stream) =>
        $"tenant_id = {Add(command, stream.TenantId, DbType.String)} AND owner_id = {Add(command, stream.OwnerId, DbType.String)} " +
        $"AND stream_id = {Add(command, stream.StreamId, DbType.String)} AND record_kind = {Add(command, (int)stream.Kind, DbType.Int32)}";

    private string Add(DbCommand command, object? value, DbType type)
    {
        var index = command.Parameters.Count;
        var parameter = command.CreateParameter();
        parameter.ParameterName = dialect == RelationalDialect.Snowflake ? (index + 1).ToString(CultureInfo.InvariantCulture) : $"p{index}";
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return dialect switch
        {
            RelationalDialect.Db2 or RelationalDialect.Snowflake => "?",
            RelationalDialect.Oracle => $":p{index}",
            _ => $"@p{index}"
        };
    }

    private static void Validate(DurableStreamKey stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        foreach (var value in new[] { stream.TenantId, stream.OwnerId, stream.StreamId })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
                throw new ArgumentException("Tenant, owner, and stream IDs must contain 1 to 128 characters.", nameof(stream));
        if (!Enum.IsDefined(stream.Kind)) throw new ArgumentOutOfRangeException(nameof(stream));
    }
}
