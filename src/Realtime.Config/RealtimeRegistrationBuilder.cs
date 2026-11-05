using System.Data.Common;
using Realtime.Client;
using Realtime.Plugins.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Realtime.Config;

public sealed class RealtimeRegistrationBuilder
{
    private readonly Dictionary<RelationalDialect, Func<IServiceProvider, string, DbConnection>> factories = [];
    internal List<Func<IServiceProvider, IRealtimeSetupStep>> Steps { get; } = [];
    internal Action<DbCommand>? ConfigureCommand { get; private set; }
    internal Action<RealtimeClientOptions>? ConfigureClient { get; private set; }
    internal RealtimeRegistrationBuilder() { }

    public RealtimeRegistrationBuilder UseConnectionFactory(RelationalDialect provider, Func<IServiceProvider, string, DbConnection> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        if (!factories.TryAdd(provider, factory)) throw new InvalidOperationException("A connection factory is already registered for this provider.");
        return this;
    }

    public RealtimeRegistrationBuilder UsePostgreSql(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.PostgreSql, factory);
    public RealtimeRegistrationBuilder UseMySql(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.MySql, factory);
    public RealtimeRegistrationBuilder UseMariaDb(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.MariaDb, factory);
    public RealtimeRegistrationBuilder UseSqlServer(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.SqlServer, factory);
    public RealtimeRegistrationBuilder UseAzureSql(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.AzureSql, factory);
    public RealtimeRegistrationBuilder UseOracle(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.Oracle, factory);
    public RealtimeRegistrationBuilder UseSqlite(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.Sqlite, factory);
    public RealtimeRegistrationBuilder UseDb2(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.Db2, factory);
    public RealtimeRegistrationBuilder UseCockroachDb(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.CockroachDb, factory);
    public RealtimeRegistrationBuilder UseSnowflake(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.Snowflake, factory);

    public RealtimeRegistrationBuilder ConfigureDatabaseCommands(Action<DbCommand> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ConfigureCommand += configure;
        return this;
    }

    public RealtimeRegistrationBuilder ConfigureRealtimeClient(Action<RealtimeClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ConfigureClient += configure;
        return this;
    }

    public RealtimeRegistrationBuilder AddSetupStep(Func<IServiceProvider, IRealtimeSetupStep> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Steps.Add(factory);
        return this;
    }

    internal Func<IServiceProvider, string, DbConnection> GetFactory(RelationalDialect provider) =>
        factories.TryGetValue(provider, out var factory) ? factory : throw new InvalidOperationException("Register a connection factory for the selected persistence provider.");

    internal static RelationalPersistenceStore CreateStore(RelationalDialect provider, Func<DbConnection> factory,
        string table, TimeProvider clock, Action<DbCommand> command) => provider switch
    {
        RelationalDialect.PostgreSql => new Plugins.PostgreSql.PostgreSqlPersistenceStore(factory, table, clock, command),
        RelationalDialect.MySql => new Plugins.MySql.MySqlPersistenceStore(factory, table, clock, command),
        RelationalDialect.MariaDb => new Plugins.MariaDb.MariaDbPersistenceStore(factory, table, clock, command),
        RelationalDialect.SqlServer => new Plugins.SqlServer.SqlServerPersistenceStore(factory, table, clock, command),
        RelationalDialect.AzureSql => new Plugins.AzureSql.AzureSqlPersistenceStore(factory, table, clock, command),
        RelationalDialect.Oracle => new Plugins.Oracle.OraclePersistenceStore(factory, table, clock, command),
        RelationalDialect.Sqlite => new Plugins.Sqlite.SqlitePersistenceStore(factory, table, clock, command),
        RelationalDialect.Db2 => new Plugins.Db2.Db2PersistenceStore(factory, table, clock, command),
        RelationalDialect.CockroachDb => new Plugins.CockroachDb.CockroachDbPersistenceStore(factory, table, clock, command),
        RelationalDialect.Snowflake => new Plugins.Snowflake.SnowflakePersistenceStore(factory, table, clock, command),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}
