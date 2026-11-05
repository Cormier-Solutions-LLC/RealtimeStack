using System.Data.Common;
using Cormier.Realtime.Plugins.Persistence;

namespace Cormier.Realtime.Plugins.Sqlite;

/// <summary>Sqlite storage using a fresh host-configured ADO.NET connection for each operation.</summary>
public sealed class SqlitePersistenceStore(Func<DbConnection> connectionFactory,
    string tableName = "realtime_records", TimeProvider? timeProvider = null, Action<DbCommand>? configureCommand = null)
    : RelationalPersistenceStore(connectionFactory, RelationalDialect.Sqlite, tableName, timeProvider, configureCommand);
