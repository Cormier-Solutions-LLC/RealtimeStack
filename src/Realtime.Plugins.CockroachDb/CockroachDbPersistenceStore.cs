using System.Data.Common;
using Cormier.Realtime.Plugins.Persistence;

namespace Cormier.Realtime.Plugins.CockroachDb;

/// <summary>CockroachDb storage using a fresh host-configured ADO.NET connection for each operation.</summary>
public sealed class CockroachDbPersistenceStore(Func<DbConnection> connectionFactory,
    string tableName = "realtime_records", TimeProvider? timeProvider = null, Action<DbCommand>? configureCommand = null)
    : RelationalPersistenceStore(connectionFactory, RelationalDialect.CockroachDb, tableName, timeProvider, configureCommand);
