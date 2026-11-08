using System.Data.Common;
using RealtimeStack.Plugins.Persistence;

namespace RealtimeStack.Plugins.SqlServer;

/// <summary>SqlServer storage using a fresh host-configured ADO.NET connection for each operation.</summary>
public sealed class SqlServerPersistenceStore(Func<DbConnection> connectionFactory,
    string tableName = "realtimestack_records", TimeProvider? timeProvider = null, Action<DbCommand>? configureCommand = null)
    : RelationalPersistenceStore(connectionFactory, RelationalDialect.SqlServer, tableName, timeProvider, configureCommand);
