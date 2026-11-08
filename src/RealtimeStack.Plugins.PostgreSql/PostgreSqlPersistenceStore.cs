using System.Data.Common;
using RealtimeStack.Plugins.Persistence;

namespace RealtimeStack.Plugins.PostgreSql;

/// <summary>PostgreSql storage using a fresh host-configured ADO.NET connection for each operation.</summary>
public sealed class PostgreSqlPersistenceStore(Func<DbConnection> connectionFactory,
    string tableName = "realtimestack_records", TimeProvider? timeProvider = null, Action<DbCommand>? configureCommand = null)
    : RelationalPersistenceStore(connectionFactory, RelationalDialect.PostgreSql, tableName, timeProvider, configureCommand);
