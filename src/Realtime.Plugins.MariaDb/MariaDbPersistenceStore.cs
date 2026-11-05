using System.Data.Common;
using Realtime.Plugins.Persistence;

namespace Realtime.Plugins.MariaDb;

/// <summary>MariaDb storage using a fresh host-configured ADO.NET connection for each operation.</summary>
public sealed class MariaDbPersistenceStore(Func<DbConnection> connectionFactory,
    string tableName = "realtime_records", TimeProvider? timeProvider = null, Action<DbCommand>? configureCommand = null)
    : RelationalPersistenceStore(connectionFactory, RelationalDialect.MariaDb, tableName, timeProvider, configureCommand);
