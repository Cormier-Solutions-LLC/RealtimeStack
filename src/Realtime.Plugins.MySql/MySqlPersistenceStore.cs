using System.Data.Common;
using Realtime.Plugins.Persistence;

namespace Realtime.Plugins.MySql;

/// <summary>MySql storage using a fresh host-configured ADO.NET connection for each operation.</summary>
public sealed class MySqlPersistenceStore(Func<DbConnection> connectionFactory,
    string tableName = "realtime_records", TimeProvider? timeProvider = null, Action<DbCommand>? configureCommand = null)
    : RelationalPersistenceStore(connectionFactory, RelationalDialect.MySql, tableName, timeProvider, configureCommand);
