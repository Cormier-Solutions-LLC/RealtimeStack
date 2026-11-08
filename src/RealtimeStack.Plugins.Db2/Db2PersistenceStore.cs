using System.Data.Common;
using RealtimeStack.Plugins.Persistence;

namespace RealtimeStack.Plugins.Db2;

/// <summary>Db2 storage using a fresh host-configured ADO.NET connection for each operation.</summary>
public sealed class Db2PersistenceStore(Func<DbConnection> connectionFactory,
    string tableName = "realtimestack_records", TimeProvider? timeProvider = null, Action<DbCommand>? configureCommand = null)
    : RelationalPersistenceStore(connectionFactory, RelationalDialect.Db2, tableName, timeProvider, configureCommand);
