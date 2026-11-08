# RealtimeStack.Plugins.MySql

Durable replay, notifications, and task status using MySql. Depends on `RealtimeStack.Plugins.Persistence`.

Construct `MySqlPersistenceStore` with a factory returning a **new, unopened** ADO.NET connection for this database. Install and configure your driver's package in the host; this plugin does not bundle a driver or discover one by reflection. Manage connection strings and authentication through host configuration. Driver platform and Native AOT support remain driver-specific.

Apply `store.CreateTableSql` through an explicit database migration before use. The connection's configured database/schema owns the table. No schema mutations occur on startup. Use a case-sensitive database/schema and preserve tenant/owner/stream IDs exactly. Snowflake requires hybrid table support and privileges because ordinary Snowflake primary keys are not enforced.

See the repository's `docs/persistence.md` for stream sequencing, replay acknowledgments, authorization, task-worker integration, retention, and verification limits. These are persistence primitives; installing this package does not automatically change gateway delivery or schedule background work.

## Upgrade to RealtimeStack 2

Update `RealtimeStack.Plugins.MySql` to `2.0.1-beta` together with the other RealtimeStack packages. NuGet restores the matching transitive dependencies automatically; it does not rewrite application code or configuration. Replace `Realtime.Plugins.MySql` (or its older `Cormier.`-prefixed equivalent) namespace/assembly references with `RealtimeStack.Plugins.MySql`. Rename product-prefixed types and registration calls from `Realtime...` to `RealtimeStack...`, then rebuild.

Browser consumers must use `@realtimestack/client`, `RealtimeStack.RealtimeStackClient`, and `/_content/RealtimeStack.Browser/realtimestack*.js`. Update configuration sections, routes, cookie options, metrics, scripts, and deployment selectors as described in the [complete upgrade guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/upgrading-to-realtimestack-2.md). Preserve configured Redis prefixes when existing state must remain reachable. Previously published package files are immutable; upgrading installs the new revision.
