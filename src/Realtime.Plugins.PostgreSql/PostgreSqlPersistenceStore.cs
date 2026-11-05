using System.Data.Common;
using Cormier.Realtime.Plugins.Persistence;

namespace Cormier.Realtime.Plugins.PostgreSql;

/// <summary>PostgreSql storage using a fresh host-configured ADO.NET connection for each operation.</summary>
public sealed class PostgreSqlPersistenceStore(Func<DbConnection> connectionFactory,
    string tableName = "realtime_records", TimeProvider? timeProvider = null, Action<DbCommand>? configureCommand = null)
    : RelationalPersistenceStore(connectionFactory, RelationalDialect.PostgreSql, tableName, timeProvider, configureCommand);
