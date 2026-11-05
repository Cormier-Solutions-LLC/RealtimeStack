using Microsoft.Data.Sqlite;
using Cormier.Realtime.Plugins.Persistence;
using Cormier.Realtime.Plugins.Sqlite;

namespace Cormier.Realtime.Plugins.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string path = Path.Join(Path.GetTempPath(), $"realtime-{Guid.NewGuid():N}.db");
    private readonly FixedClock clock = new();
    private SqlitePersistenceStore Store() => new(() => new SqliteConnection($"Data Source={path};Pooling=False"), timeProvider: clock);
    private static DurableStreamKey Stream(string tenant = "tenant", string owner = "owner", DurableRecordKind kind = DurableRecordKind.Message) =>
        new(tenant, owner, "inbox", kind);

    private async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Store().CreateTableSql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ReconnectReplaysInSequenceAndAcknowledgesOnlyTheSelectedScope()
    {
        await InitializeAsync();
        await Store().AppendAsync(Stream(), new(2, "second", clock.GetUtcNow()));
        await Store().AppendAsync(Stream(), new(1, "first ' quoted 雪", clock.GetUtcNow()));
        await Store().AppendAsync(Stream(tenant: "Tenant"), new(1, "other tenant", clock.GetUtcNow()));
        await Store().AppendAsync(Stream(owner: "other"), new(1, "other owner", clock.GetUtcNow()));
        await Store().AppendAsync(Stream(kind: DurableRecordKind.Notification), new(1, "notice", clock.GetUtcNow()));
        var page = await Store().ReadAsync(Stream(), limit: 1);
        Assert.Equal("first ' quoted 雪", Assert.Single(page).Payload);
        Assert.Equal(2, Assert.Single(await Store().ReadAsync(Stream(), afterSequence: 1)).Sequence);
        // A new store and connection simulates a user returning after the original request has ended.
        Assert.Equal(2, (await Store().ReadLatestAsync(Stream()))!.Sequence);
        await Store().AcknowledgeAsync(Stream(), 1);
        Assert.Equal(2, Assert.Single(await Store().ReadAsync(Stream())).Sequence);
        Assert.Single(await Store().ReadAsync(Stream(tenant: "Tenant")));
        Assert.Single(await Store().ReadAsync(Stream(owner: "other")));
        Assert.Single(await Store().ReadAsync(Stream(kind: DurableRecordKind.Notification)));
    }

    [Fact]
    public async Task ExpirationFiltersReadsBeforeMaintenanceAndPrunesOnlyExpiredRows()
    {
        await InitializeAsync();
        await Store().AppendAsync(Stream(), new(1, "expired", clock.GetUtcNow(), clock.GetUtcNow()));
        await Store().AppendAsync(Stream(), new(2, "retained", clock.GetUtcNow()));
        Assert.Equal("retained", Assert.Single(await Store().ReadAsync(Stream())).Payload);
        Assert.Equal(1, await Store().PruneExpiredAsync());
        Assert.Equal(0, await Store().PruneExpiredAsync());
    }

    [Fact]
    public async Task DuplicateSequenceFailsWithoutReplacingExistingData()
    {
        await InitializeAsync();
        await Store().AppendAsync(Stream(), new(1, "original", clock.GetUtcNow()));
        await Assert.ThrowsAsync<SqliteException>(() => Store().AppendAsync(Stream(), new(1, "replacement", clock.GetUtcNow())));
        Assert.Equal("original", (await Store().ReadLatestAsync(Stream()))!.Payload);
    }

    [Fact]
    public async Task TaskCompletionAndFailureRemainReadableAfterReconnect()
    {
        await InitializeAsync();
        var tasks = new DurableTasks(Store(), clock);
        await tasks.SaveAsync("tenant", "owner", 1, new("import-1", DurableTaskState.Running, "10 rows"));
        await tasks.SaveAsync("tenant", "owner", 2, new("import-1", DurableTaskState.Failed, "10 rows", "Invalid input", clock.GetUtcNow()));
        var reconnected = new DurableTasks(Store(), clock);
        var status = await reconnected.GetAsync("tenant", "owner", "import-1");
        Assert.Equal(DurableTaskState.Failed, status!.State);
        Assert.Equal("Invalid input", status.Error);
        Assert.Equal(clock.GetUtcNow(), status.FinishedAt);
        Assert.Null(await reconnected.GetAsync("other", "owner", "import-1"));
        await tasks.SaveAsync("tenant", "owner", 1, new("import-2", DurableTaskState.Succeeded, FinishedAt: clock.GetUtcNow()));
        Assert.Equal(DurableTaskState.Succeeded, (await reconnected.GetAsync("tenant", "owner", "import-2"))!.State);
        await Assert.ThrowsAsync<ArgumentException>(() => tasks.SaveAsync("tenant", "owner", 3, new("import-1", DurableTaskState.Failed)));
    }

    [Fact]
    public async Task RejectsInvalidIdentifiersAndHonorsCancellation()
    {
        Assert.Throws<ArgumentException>(() => new SqlitePersistenceStore(() => new SqliteConnection(), "records; DROP TABLE x"));
        await Assert.ThrowsAsync<ArgumentException>(() => Store().ReadAsync(Stream(tenant: "")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Store().ReadAsync(Stream(), limit: 1001));
        await InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().AppendAsync(Stream(), new(1, "x", clock.GetUtcNow()), cancellation.Token));
        Assert.Empty(await Store().ReadAsync(Stream()));
    }

    [Theory]
    [InlineData(RelationalDialect.PostgreSql)]
    [InlineData(RelationalDialect.MySql)]
    [InlineData(RelationalDialect.MariaDb)]
    [InlineData(RelationalDialect.SqlServer)]
    [InlineData(RelationalDialect.AzureSql)]
    [InlineData(RelationalDialect.Oracle)]
    [InlineData(RelationalDialect.Sqlite)]
    [InlineData(RelationalDialect.Db2)]
    [InlineData(RelationalDialect.CockroachDb)]
    [InlineData(RelationalDialect.Snowflake)]
    public void SchemaRequiresScopedUniqueSequence(RelationalDialect dialect)
    {
        var store = new RelationalPersistenceStore(() => new SqliteConnection(), dialect);
        Assert.Contains("PRIMARY KEY (tenant_id, owner_id, stream_id, record_kind, sequence_no)", store.CreateTableSql);
        if (dialect == RelationalDialect.Snowflake) Assert.StartsWith("CREATE HYBRID TABLE", store.CreateTableSql);
    }

    public void Dispose() => File.Delete(path);
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
