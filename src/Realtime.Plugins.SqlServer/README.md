# Realtime.Plugins.SqlServer

Durable replay, notifications, and task status using SqlServer. Depends on `Realtime.Plugins.Persistence`.

Construct `SqlServerPersistenceStore` with a factory returning a **new, unopened** ADO.NET connection for this database. Install and configure your driver's package in the host; this plugin does not bundle a driver or discover one by reflection. Manage connection strings and authentication through host configuration. Driver platform and Native AOT support remain driver-specific.

Apply `store.CreateTableSql` through an explicit database migration before use. The connection's configured database/schema owns the table. No schema mutations occur on startup. Use a case-sensitive database/schema and preserve tenant/owner/stream IDs exactly. Snowflake requires hybrid table support and privileges because ordinary Snowflake primary keys are not enforced.

See the repository's `docs/persistence.md` for stream sequencing, replay acknowledgments, authorization, task-worker integration, retention, and verification limits. These are persistence primitives; installing this package does not automatically change gateway delivery or schedule background work.
