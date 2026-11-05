using System.Data.Common;
using Cormier.Realtime.Plugins.Persistence;

namespace Cormier.Realtime.Plugins.Snowflake;

/// <summary>Snowflake storage using a fresh host-configured ADO.NET connection for each operation.</summary>
public sealed class SnowflakePersistenceStore(Func<DbConnection> connectionFactory,
    string tableName = "realtime_records", TimeProvider? timeProvider = null, Action<DbCommand>? configureCommand = null)
    : RelationalPersistenceStore(connectionFactory, RelationalDialect.Snowflake, tableName, timeProvider, configureCommand);
