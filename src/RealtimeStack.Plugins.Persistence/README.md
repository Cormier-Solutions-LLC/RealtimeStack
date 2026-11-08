# RealtimeStack.Plugins.Persistence

Shared .NET 10 persistence contracts and relational implementation for durable message replay, notifications, and background task status. The PostgreSql, MySql, MariaDb, SqlServer, AzureSql, Oracle, Sqlite, Db2, CockroachDb, and Snowflake packages depend on this package.

Use `IRealtimeStackPersistenceStore` for scoped, ordered streams and `DurableTasks` for source-generated JSON task snapshots. Each stream includes tenant, owner, stream ID, and record kind. Database adapters accept a factory that creates a new unopened ADO.NET connection, so the host controls the driver, credentials, pooling, and platform requirements.

Apply the adapter's `CreateTableSql` through your migration process. Installing a plugin does not automatically wire replay into the gateway or execute background tasks. The host must authorize stream access, serialize writers within each stream, allocate durable increasing sequence numbers, persist before sending, and acknowledge only after client processing. Do not reset sequence numbers after acknowledgment or retention cleanup. Failed writes propagate to the host.

Run imports in a server-owned worker independent of browser request cancellation. Save queued/running and terminal task snapshots, then query `DurableTasks.GetAsync` using the authenticated tenant/owner and task ID when the user returns. Persistence stores progress and results; restart recovery, job leasing, scheduling, retries, and exactly-once business effects belong to the worker implementation.

See the repository's `docs/persistence.md` for examples and verification limits.

## Upgrade to RealtimeStack 2

Update `RealtimeStack.Plugins.Persistence` to `2.0.0-beta` together with the other RealtimeStack packages. NuGet restores the matching transitive dependencies automatically; it does not rewrite application code or configuration. Replace `Realtime.Plugins.Persistence` (or its older `Cormier.`-prefixed equivalent) namespace/assembly references with `RealtimeStack.Plugins.Persistence`. Rename product-prefixed types and registration calls from `Realtime...` to `RealtimeStack...`, then rebuild.

Browser consumers must use `@realtimestack/client`, `RealtimeStack.RealtimeStackClient`, and `/_content/RealtimeStack.Browser/realtimestack*.js`. Update configuration sections, routes, cookie options, metrics, scripts, and deployment selectors as described in the [complete upgrade guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/upgrading-to-realtimestack-2.md). Preserve configured Redis prefixes when existing state must remain reachable. Previously published package files are immutable; upgrading installs the new revision.
