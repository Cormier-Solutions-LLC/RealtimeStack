# Registration and first-time configuration

Install `Realtime.Config` for an integrated .NET 10 host. It references all seventeen other NuGet packages, with bounded dependencies. Existing `Realtime.AspNetCore` and `Realtime.Client` remain available for smaller consumers. The portable client still targets .NET Standard 2.0; the configuration package requires ASP.NET Core 10.

## First run

Create a fresh settings file with `RealtimeFirstRun.WriteTemplateAsync(path)`. This is an API for an installer or setup application, not a globally installed command. It writes no secrets, leaves systems disabled, and refuses to overwrite files. Merge the resulting `RealtimeSetup` section into the host's configuration, then supply network identities and credentials through host configuration, environment variables, user secrets, or the deployment's secret manager.

```json
{
  "RealtimeSetup": {
    "GatewayEnabled": true,
    "HttpFallbackEnabled": true,
    "ClientEnabled": false,
    "Persistence": {
      "Enabled": true,
      "Provider": "PostgreSql",
      "TableName": "realtime_records",
      "ConnectionStringName": "RealtimePersistence",
      "CommandTimeoutSeconds": 30,
      "ApiEnabled": true,
      "ApiBasePath": "/realtime/persistence"
    }
  }
}
```

Provide `ConnectionStrings__RealtimePersistence` and, for HTTP worker writes, `RealtimeSetup__Persistence__WorkerToken` through secret configuration. The worker token must contain at least 32 characters. Readers work without a worker token, but worker HTTP endpoints reject every write when it is absent. Never put the worker token in a browser bundle, query string, sample file, ConfigMap, or public diagnostics response.

The gateway also needs its existing Redis, origin, proxy, and authorization settings. `RealtimeSetup:ClientEnabled` registers a disconnected `RealtimeClient` using `RealtimeClient:Endpoint`; configure authentication and other client options through `ConfigureRealtimeClient` and the existing DI interfaces. Connecting remains explicit.

```csharp
using Cormier.Realtime.Config;
using Npgsql;

builder.Services.AddRealtimeConfiguration(builder.Configuration, plugins => plugins
    .UsePostgreSql((_, connectionString) => new NpgsqlConnection(connectionString))
    .UseCockroachDb((_, connectionString) => new NpgsqlConnection(connectionString)));
var app = builder.Build();
app.UseRealtimeGateway();
// Install the host's authentication, authorization and session middleware here.
app.MapRealtimeGateway();
app.MapRealtimeHttpFallback();
app.MapRealtimePersistence(builder.Configuration);
```

Factories receive the service provider and configured connection string; return a new unopened connection. Install only the driver versions the host qualifies. All ten adapters are selectable by configuration, but a selected provider without a registered factory fails before registration changes the service collection. Factories can use host-managed credentials or identity services. The shipped gateway, ASP.NET example, and full-circle host register SQLite, PostgreSQL, and CockroachDB factories. MySQL, MariaDB, SQL Server, Azure SQL, Oracle, Db2, and Snowflake require a host/custom image registering the corresponding installed driver. Setting a Helm provider value does not install a driver.

## Inspect and initialize

Resolve `RealtimeSetupManager` and call `InspectAsync`. No schema changes occur. Database/permission/schema failures produce a generic unavailable status without provider exception details. `Configured` means a feature was registered, not that its remote dependency is reachable. The gateway's existing runtime health endpoints continue to check Redis. When persistence is enabled, standalone gateway readiness additionally checks the record query; liveness does not depend on the database.

Call `InitializeAsync` only from an authorized installer/operator action. For the standalone executable, pass `--realtime-setup` with the same database configuration. This mode initializes and exits without opening the HTTP listener. Repeating setup preserves existing records. Concurrent create attempts are accepted only when the subsequent query succeeds. Existing schemas are not dropped, upgraded, or repaired; use explicit migrations for incompatible schemas. A successful probe verifies query compatibility, not every physical database constraint. Run setup with appropriate DDL privileges, then remove DDL privileges from routine application credentials where your deployment separates these roles.

Custom plugins implement `IRealtimeSetupStep`, with a stable unique ID, an inspection result, and an explicit initialization method. Register a factory through `AddSetupStep`. Initialization runs sequentially, stops on failure, and honors cancellation. The library does not guess how to provision external servers, credentials, managed services, or infrastructure.

## Helm

Persistence defaults to disabled. Set `persistence.enabled`, `provider`, `connectionSecret`, and explicit `egress` destinations/ports. The chart references an existing Secret key as `ConnectionStrings__RealtimePersistence`; it never renders the connection string. Enable `persistence.api.enabled` for session reads and optionally provide a distinct `workerTokenSecret` plus worker namespace/pod selectors for private service access. Public ingress includes only the three reader/acknowledgment routes. Workers use the internal Service address.

For external databases, `persistence.setupJob.enabled` adds a regular revision-specific Job running `--realtime-setup`. It is deliberately not a pre-install hook: its normal ConfigMap must exist first. Gateway readiness remains unavailable until the schema is queryable. The Job has bounded retries/deadline, no HTTP Service membership, and a separate egress policy limited to DNS and the configured database. A failed job needs operator diagnosis; it does not reset application data. Use `helm --wait --wait-for-jobs` with a timeout covering setup and rollout.

SQLite requires `topology=non-ha`, one replica, disabled autoscaling, and an existing PVC. Initialize that database before rollout; the chart rejects concurrent SQLite setup jobs. Point the secret connection string to a file under `persistence.storage.mountPath`. SQLite uses the Recreate strategy to avoid overlapping pods on one local database. The chart neither creates nor deletes the PVC. This intentionally permits downtime during replacement and does not provide HA. Use an external relational database for HA deployments.

For other database drivers, supply a custom image and set `persistence.customProviderImage=true`. That flag acknowledges the image requirement; it does not prove that the image contains or registers the selected driver. Qualify that image, schema and driver against the intended engine/platform before deployment.
