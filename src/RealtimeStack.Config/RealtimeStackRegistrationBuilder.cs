using System.Data.Common;
using RealtimeStack.Client;
using RealtimeStack.Plugins.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace RealtimeStack.Config;

public sealed class RealtimeStackRegistrationBuilder
{
    private readonly Dictionary<RelationalDialect, Func<IServiceProvider, string, DbConnection>> factories = [];
    internal List<Func<IServiceProvider, IRealtimeStackSetupStep>> Steps { get; } = [];
    internal Action<DbCommand>? ConfigureCommand { get; private set; }
    internal Action<RealtimeStackClientOptions>? ConfigureClient { get; private set; }
    internal RealtimeStackRegistrationBuilder() { }

    public RealtimeStackRegistrationBuilder UseConnectionFactory(RelationalDialect provider, Func<IServiceProvider, string, DbConnection> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        if (!factories.TryAdd(provider, factory)) throw new InvalidOperationException("A connection factory is already registered for this provider.");
        return this;
    }

    public RealtimeStackRegistrationBuilder UsePostgreSql(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.PostgreSql, factory);
    public RealtimeStackRegistrationBuilder UseMySql(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.MySql, factory);
    public RealtimeStackRegistrationBuilder UseMariaDb(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.MariaDb, factory);
    public RealtimeStackRegistrationBuilder UseSqlServer(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.SqlServer, factory);
    public RealtimeStackRegistrationBuilder UseAzureSql(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.AzureSql, factory);
    public RealtimeStackRegistrationBuilder UseOracle(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.Oracle, factory);
    public RealtimeStackRegistrationBuilder UseSqlite(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.Sqlite, factory);
    public RealtimeStackRegistrationBuilder UseDb2(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.Db2, factory);
    public RealtimeStackRegistrationBuilder UseCockroachDb(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.CockroachDb, factory);
    public RealtimeStackRegistrationBuilder UseSnowflake(Func<IServiceProvider, string, DbConnection> factory) => UseConnectionFactory(RelationalDialect.Snowflake, factory);

    public RealtimeStackRegistrationBuilder ConfigureDatabaseCommands(Action<DbCommand> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ConfigureCommand += configure;
        return this;
    }

    public RealtimeStackRegistrationBuilder ConfigureRealtimeStackClient(Action<RealtimeStackClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ConfigureClient += configure;
        return this;
    }

    public RealtimeStackRegistrationBuilder AddSetupStep(Func<IServiceProvider, IRealtimeStackSetupStep> factory)
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
