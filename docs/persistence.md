# Durable realtime persistence

`Realtime.Plugins.Persistence` is the shared package. All ten database plugins depend on it with the same bounded compatibility range as the rest of the package family. Plugin versions follow `ContractsVersion` in coordinated releases.

| Package | Host-supplied ADO.NET connection |
| --- | --- |
| `Realtime.Plugins.PostgreSql` | Npgsql connection |
| `Realtime.Plugins.MySql` | MySqlConnector connection |
| `Realtime.Plugins.MariaDb` | MySqlConnector connection |
| `Realtime.Plugins.SqlServer` | Microsoft.Data.SqlClient connection |
| `Realtime.Plugins.AzureSql` | Microsoft.Data.SqlClient connection with host-managed Azure authentication |
| `Realtime.Plugins.Oracle` | Oracle managed provider connection, Oracle 12c or newer SQL syntax |
| `Realtime.Plugins.Sqlite` | Microsoft.Data.Sqlite connection |
| `Realtime.Plugins.Db2` | IBM Db2 LUW provider connection; not qualified for Db2 for i or z/OS |
| `Realtime.Plugins.CockroachDb` | Npgsql connection using the PostgreSQL protocol |
| `Realtime.Plugins.Snowflake` | Snowflake.Data connection and hybrid table support |

Drivers are installed by the host, separately from these plugins. This avoids forcing driver versions, native binaries, authentication packages, or driver-specific trimming requirements onto every consumer. Pass a factory returning a **fresh unopened connection** on every invocation; each operation opens and disposes its own connection. Configure database/schema, authentication, command timeouts, pooling, and TLS using the selected driver's supported configuration. The optional `configureCommand` callback runs after parameters and SQL are populated, before execution. Use it to set command timeouts and provider-specific parameter metadata (for example Oracle CLOB binding for large payloads and Unicode settings for Db2). Cancellation is passed through to database operations; actual cancellation behavior is driver-dependent.

## Schema and authorization

Apply `store.CreateTableSql` through an explicit host migration before using the store. No DDL is executed automatically. The optional table name is a simple ASCII SQL identifier up to 30 characters; choose the schema through the connection's configuration. Reserve this table for this library. Use case-sensitive ID comparison; generated MySQL/MariaDB and SQL Server/Azure SQL ID columns explicitly use binary collations. Configure Oracle and Db2 sessions/database appropriately and qualify Unicode handling with your installed driver. SQL text uses bound values for all record data.

Snowflake uses `CREATE HYBRID TABLE`, because hybrid tables enforce primary keys while ordinary tables do not. See [Snowflake hybrid table constraints](https://docs.snowflake.com/en/user-guide/tables-hybrid). An account without hybrid table support cannot use this adapter's schema.

Every operation scopes records by tenant, owner, stream ID, and kind. Resolve tenant and owner from authenticated server context; a client-provided ID is not authorization. Expose replay and task queries only behind your application's authorization policy. Never expose maintenance operations directly to clients.

## Replay and notifications

```csharp
using Microsoft.Data.Sqlite;
using Cormier.Realtime.Plugins.Persistence;
using Cormier.Realtime.Plugins.Sqlite;

var store = new SqlitePersistenceStore(() => new SqliteConnection(configuredConnectionString));
var inbox = new DurableStreamKey(authenticatedTenant, authenticatedOwner, "inbox", DurableRecordKind.Message);
await store.AppendAsync(inbox, new DurableRecord(nextDurableSequence, payload, DateTimeOffset.UtcNow), workerToken);
var page = await store.ReadAsync(inbox, lastProcessedSequence, limit: 100, cancellationToken: requestToken);
// Send the page. Acknowledge only after the client confirms successful processing.
await store.AcknowledgeAsync(inbox, confirmedSequence, requestToken);
```

Persist before sending live. Use `DurableRecordKind.Notification` for a separate notification inbox. Payloads are opaque strings; the host owns the envelope format and its versioning. A stream supports one shared acknowledgment position; use distinct streams per device if each device needs independent delivery. Delivery is at least once: consumers must deduplicate using stream plus sequence. Database errors propagate; the library does not silently treat a failed write as delivered.

The host must serialize writers **within each stream** and allocate strictly increasing sequence numbers durably. Do not derive the next sequence from the currently retained rows, reset it after acknowledgments, commit sequences out of order, or advance a replay cursor past writes still in flight. Duplicate sequence numbers fail at the database primary key; classify and handle driver-specific duplicate/transient failures in the host. No automatic retries hide ambiguous write outcomes. Concurrent writes to different streams are independent.

Reads use an exclusive `afterSequence`, ordered pages of 1–1000 records, and filter expiration using the configured `TimeProvider`. `ReadLatestAsync` returns the newest unexpired record. Acknowledgment deletes records through the confirmed sequence in that exact stream. Expiration is optional; schedule `PruneExpiredAsync` from a privileged maintenance worker to reclaim space. Task status does not expire unless the host uses the lower-level record API to set retention. Plan payload size, indexes, database backups, encryption, retention, and sensitive-error redaction for your workload.

## Imports that outlive a browser

```csharp
var tasks = new DurableTasks(store);
// The server-owned worker holds a task ID, tenant/owner, and durable sequence allocator.
await tasks.SaveAsync(tenant, owner, 1, new(taskId, DurableTaskState.Queued), workerToken);
await tasks.SaveAsync(tenant, owner, 2, new(taskId, DurableTaskState.Running, "500 rows imported"), workerToken);
await tasks.SaveAsync(tenant, owner, 3,
    new(taskId, DurableTaskState.Succeeded, "1000 rows imported", FinishedAt: DateTimeOffset.UtcNow), workerToken);
// A subsequent authenticated request, even from a new browser connection:
var status = await tasks.GetAsync(authenticatedTenant, authenticatedOwner, taskId, requestToken);
```

Run imports outside the HTTP request and WebSocket lifetime. Persist the task ID where the host can rediscover it, such as an application job table or a notification inbox. Store `Failed` with a safe error description and `FinishedAt` when execution fails. Terminal snapshots require a completion time; active snapshots cannot have one. `GetAsync` returns the snapshot with the highest sequence. Hosts enforce legal state transitions and serialize worker updates.

The persistence packages store replay and task history. They do not automatically capture gateway traffic, discover task IDs, queue execution, lease workers, or recover a worker after a server restart. Optional `Realtime.Config` integration now supplies an HTTP task/inbox API and shared example UI; see [cross-language integration](persistence-integrations.md). A production durable worker must persist its work request, claim work safely, handle abandoned leases, and make business side effects retry-safe. Closing a page can be supported by a server-owned worker plus this status store; surviving server/process loss additionally requires that worker recovery design.

## Verification

The automated SQLite tests exercise real file-backed persistence across new connections, ordered replay, scoped acknowledgments, tenant/owner/kind isolation, duplicates, expiration, cancellation, and task completion/failure snapshots. All ten adapters build and are included in package inspection and clean-consumer checks. Live qualification of PostgreSQL, MySQL, MariaDB, SQL Server, Azure SQL, Oracle, Db2 LUW, CockroachDB, and Snowflake requires configured disposable databases and their host drivers; compilation and schema checks alone do not establish runtime compatibility with those engines. Native AOT support must be qualified with the host's selected driver.
