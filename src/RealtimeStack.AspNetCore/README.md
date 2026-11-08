# RealtimeStack ASP.NET Core integration

`RealtimeStack.AspNetCore` adds the existing realtimestack gateway to an ASP.NET Core application without copying gateway infrastructure.

The package targets `net10.0` and requires the ASP.NET Core shared framework. Add it to a .NET 10 web host from your approved NuGet source (`dotnet add package RealtimeStack.AspNetCore --version 2.0.1-beta`). It carries compatible Contracts and Redis dependencies; it does not launch a separate gateway process or provide a login/identity provider.

The following host setup assumes the required external configuration is already supplied. Memory-backed ASP.NET Core sessions are suitable only for a single-process example; choose a shared session store for multiple replicas.

```csharp
using RealtimeStack.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddRealtimeStackGateway(builder.Configuration);
if (builder.Configuration.GetValue<bool>("Diagnostics:Enabled"))
{
    builder.Services.AddRealtimeStackDiagnosticsBearer(
        builder.Configuration["Diagnostics:AuthorizationPolicy"]!,
        builder.Configuration["Diagnostics:OperatorToken"]!);
}
if (!string.IsNullOrWhiteSpace(builder.Configuration["Metrics:AuthorizationPolicy"]))
{
    builder.Services.AddRealtimeStackMetricsBearer(
        builder.Configuration["Metrics:AuthorizationPolicy"]!,
        builder.Configuration["Metrics:ScrapeToken"]!);
}

var app = builder.Build();
app.UseRealtimeStackGateway();
app.UseAuthentication();
app.UseAuthorization();
app.UseSession();
app.MapRealtimeStackGateway();
app.MapRealtimeStackDiagnostics();
app.Run();
```

Configure the `Gateway`, `Redis`, `Proxy`, `RealtimeStack`, `Metrics`, and `Diagnostics` sections. Network locations, routes, cookie names, origins, Redis keys, and deployment names remain configuration values.

At minimum supply a reachable `Redis:Endpoint`, a nonempty `Redis:InstancePrefix`, and the intended `RealtimeStack:AllowedOrigins`. Supply Redis user/password together from runtime secret configuration when authentication is enabled. `Proxy:TrustedNetworks` must describe only the trusted forwarding hop; the middleware forwarding limit is one. The default routes are GET `/realtimestack/ws` and POST `/realtimestack/tickets`, with cookie session resolution and the `realtimestack.v1` subprotocol. Register any application authorization policy before selecting it.

`MapRealtimeStackDiagnostics` maps the configured Prometheus/OpenMetrics endpoint and, only when explicitly enabled, the operator diagnostics routes. Diagnostics require a registered authorization policy plus configured Origin and network restrictions; Production requires the additional `Diagnostics:ProductionEnabled` acknowledgement. `AddRealtimeStackDiagnosticsBearer` is the built-in option for a strong runtime-provided operator token, while `AddRealtimeStackMetricsBearer` registers a distinct scrape credential and scheme. Applications using an identity provider can instead register their own policies. Never reuse the diagnostics and metrics policies or tokens, or place credentials in source, appsettings, Helm values, or browser storage. The live log/event streams and temporary log-level controls are bounded, redacted, audited, time-limited, and replica-coordinated through Redis. See the repository diagnostics runbook for the full policy and rollback matrix.

For standard ASP.NET Core session middleware, call `AddSession` during service registration and place `UseSession` before the mapped endpoints execute. Set `RealtimeStack:SessionSource` to `AspNetCoreSession`. The resolver reads `RealtimeStack.SessionId` from `HttpContext.Session`, falling back to `ISession.Id`, and validates that identifier through the existing `IRealtimeStackSessionStore`; expiration, revocation, tenant scope, and reconnect therefore retain the shared Redis contract.

`AddSession` alone does not create an authorized realtimestack identity. The host's login flow must create the matching gateway session record (or provide an `IRealtimeStackSessionStore` implementation) and place its identifier in the configured session key. Cookie mode likewise requires an existing valid store record. Diagnostics bearer credentials are operator credentials, not application login credentials.

The default `Cookie` session source preserves standalone gateway behavior. It reads the configurable `RealtimeStack:SessionCookieName` and validates it through the same store.

Set `RealtimeStack:AuthorizationPolicy` to attach a standard ASP.NET Core authorization policy to both mapped endpoints. Internal Origin, ticket, session, tenant, user, route, payload, timeout, queue, heartbeat, and close-code enforcement still applies.

`UseRealtimeStackGateway` installs forwarded-header, response-compression, diagnostics preflight and WebSocket middleware, so call it before authentication, authorization, session, and mapped endpoints. `MapRealtimeStackGateway` fails if called twice or if an existing equivalent route accepts the same method. Health endpoints are owned by the host: the standalone gateway maps `/health/startup`, `/health/live` and `/health/ready` in its `Program.cs`; these are not added by `MapRealtimeStackGateway`.

`MapRealtimeStackDiagnostics` maps metrics only when `Metrics:Enabled` is true (the default); metrics are unprotected unless a policy/network restriction is configured. OTLP metric export is separately enabled by `Metrics:OtlpEnabled` and configured through standard OpenTelemetry exporter settings. See the [diagnostics runbook](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/runbooks/diagnostics.md), [protocol contract](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/protocol.md) and [package release policy](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/package-release.md).

Repository verification includes hosting integration tests and `scripts/Test-AspNetCorePackage.ps1` for an isolated packaged consumer. A successful package install does not validate deployment Redis, proxy trust, identity provisioning or operator authorization.

## Upgrade to RealtimeStack 2

Update `RealtimeStack.AspNetCore` to `2.0.1-beta` together with the other RealtimeStack packages. NuGet restores the matching transitive dependencies automatically; it does not rewrite application code or configuration. Replace `Realtime.AspNetCore` (or its older `Cormier.`-prefixed equivalent) namespace/assembly references with `RealtimeStack.AspNetCore`. Rename product-prefixed types and registration calls from `Realtime...` to `RealtimeStack...`, then rebuild.

Browser consumers must use `@realtimestack/client`, `RealtimeStack.RealtimeStackClient`, and `/_content/RealtimeStack.Browser/realtimestack*.js`. Update configuration sections, routes, cookie options, metrics, scripts, and deployment selectors as described in the [complete upgrade guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/upgrading-to-realtimestack-2.md). Preserve configured Redis prefixes when existing state must remain reachable. Previously published package files are immutable; upgrading installs the new revision.

## Error-handler plugins

The hosting package includes the provider-neutral `RealtimeStack.Plugins.Logging` dependency. Register one or more optional adapters with `AddRealtimeStackBugsnagErrorHandler`, `AddRealtimeStackSerilogErrorHandler`, or `AddRealtimeStackLog4netErrorHandler`, supplying your configured client/logger. Call `app.UseRealtimeStackErrorHandling()` before the endpoints to report their unhandled exceptions and preserve the normal exception pipeline. Reporting waits up to five seconds by default; pass a positive timeout up to one minute to override it. Providers should honor cancellation; a provider that ignores it may finish after the middleware stops waiting. The request's `RealtimeStack.ErrorReportResult` or `RealtimeStack.ErrorReportingFailure` item records the reporting outcome. Request cancellations are not reported.

See [logging plugins](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/logging-plugins.md) for the generic interface, custom implementations, and provider setup. Provider SDKs are separate opt-in dependencies.
