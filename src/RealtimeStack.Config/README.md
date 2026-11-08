# RealtimeStack.Config

.NET 10 registration and first-run setup for the RealtimeStack package family. Includes the gateway/Redis integration, HTTP fallback, portable .NET client, browser assets, persistence contracts, and all ten database adapters.

```csharp
using RealtimeStack.Config;
using Microsoft.Data.Sqlite;

builder.Services.AddRealtimeStackConfiguration(builder.Configuration, registration => registration
    .UseSqlite((_, connectionString) => new SqliteConnection(connectionString)));
var app = builder.Build();
// Inspect is read-only; initialization is an explicit operator action.
var setup = app.Services.GetRequiredService<RealtimeStackSetupManager>();
var status = await setup.InspectAsync();
// await setup.InitializeAsync(); // creates the missing record table; does not drop existing data
```

Install your database driver in the host, provide its factory with `UsePostgreSql`, `UseMySql`, `UseMariaDb`, `UseSqlServer`, `UseAzureSql`, `UseOracle`, `UseSqlite`, `UseDb2`, `UseCockroachDb`, or `UseSnowflake`, and select that provider through `RealtimeStackSetup:Persistence:Provider`. Connection strings are resolved from the named `ConnectionStrings` entry and never emitted in setup status. `ConfigureDatabaseCommands` supports driver-specific binding and timeout settings.

`RealtimeStackFirstRun.WriteTemplateAsync(path)` creates disabled-by-default settings and refuses to overwrite an existing file. `RealtimeStackSetupOptions.Read(configuration).Validate(configuration)` reports required configuration before registration. Enabling HTTP fallback requires the gateway. Register only once. Registration never connects or changes schemas. `Configured` status indicates registration, while `Ready` for persistence indicates a successful record query; it does not certify schema constraints or the host worker implementation.

Custom plugins implement `IRealtimeStackSetupStep` and register through `AddSetupStep`. Their IDs must be unique. Setup steps run sequentially; failures stop subsequent steps and cancellation propagates. Custom step authors own idempotency, safe status messages, rollback, and authorization of external changes.

Use `app.MapRealtimeStackPersistence(configuration)` to expose the opt-in HTTP interface after gateway middleware/session/authentication setup. Reader operations use the existing validated session and same-origin policy; worker writes require a separate secret of at least 32 characters. This package exposes no anonymous setup/admin endpoints. Protect operator-triggered initialization in your own host. See the repository's `docs/configuration.md` and `docs/persistence-integrations.md` for configuration, Helm deployment, HTTP contracts, and worker examples.

## Upgrade to RealtimeStack 2

Update `RealtimeStack.Config` to `2.0.0-beta` together with the other RealtimeStack packages. NuGet restores the matching transitive dependencies automatically; it does not rewrite application code or configuration. Replace `Realtime.Config` (or its older `Cormier.`-prefixed equivalent) namespace/assembly references with `RealtimeStack.Config`. Rename product-prefixed types and registration calls from `Realtime...` to `RealtimeStack...`, then rebuild.

Browser consumers must use `@realtimestack/client`, `RealtimeStack.RealtimeStackClient`, and `/_content/RealtimeStack.Browser/realtimestack*.js`. Update configuration sections, routes, cookie options, metrics, scripts, and deployment selectors as described in the [complete upgrade guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/upgrading-to-realtimestack-2.md). Preserve configured Redis prefixes when existing state must remain reachable. Previously published package files are immutable; upgrading installs the new revision.
