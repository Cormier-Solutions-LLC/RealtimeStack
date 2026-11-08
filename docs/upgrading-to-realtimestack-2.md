# Upgrade to RealtimeStack 2

Version `2.0.0-beta` completes the public RealtimeStack naming change. Upgrade the gateway, server packages, clients, and generated assets together; the renamed default WebSocket subprotocol and routes require a coordinated rollout.

## Package and source updates

Keep `RealtimeStack.*` NuGet package IDs and update them to `2.0.0-beta`. If a consumer still uses the older `Cormier.Realtime.*` IDs, replace the prefix with `RealtimeStack.` and remove the old package graph. NuGet restores transitive dependencies automatically within each package's declared `[2.0.0-beta,2.1.0)` internal ranges. NuGet does not rewrite application source files, configuration, or URLs.

| Earlier identity | RealtimeStack 2 replacement |
| --- | --- |
| `Realtime.*` or `Cormier.Realtime.*` namespaces and assemblies | `RealtimeStack.*` |
| `RealtimeClient` | `RealtimeStackClient` |
| `AddRealtimeGateway` / `UseRealtimeGateway` / `MapRealtimeGateway` | `AddRealtimeStackGateway` / `UseRealtimeStackGateway` / `MapRealtimeStackGateway` |
| `AddRealtimeConfiguration` / `MapRealtimePersistence` | `AddRealtimeStackConfiguration` / `MapRealtimeStackPersistence` |
| `RealtimeOptions`, `RealtimeSetupOptions`, `RealtimeSetupManager` | Corresponding `RealtimeStackOptions`, `RealtimeStackSetupOptions`, `RealtimeStackSetupManager` |
| `@realtime/client` | `@realtimestack/client` |
| `Realtime.RealtimeClient` / `RealtimeStack.RealtimeClient` | `RealtimeStack.RealtimeStackClient` |
| `/_content/Realtime.Browser/realtime*.js` | `/_content/RealtimeStack.Browser/realtimestack*.js` |
| `Realtime.sln`, `src/Realtime.*`, `Bootstrap-Realtime.ps1` | `RealtimeStack.sln`, `src/RealtimeStack.*`, `Bootstrap-RealtimeStack.ps1` |

Update all C# `using` directives, fully qualified type names, reflection/type strings, assembly references, and renamed registration calls, then rebuild the application. Update TypeScript imports and the `RealtimeStackClient` export. Refresh JavaScript, declarations, source maps, CSP/SRI hashes, and cached HTML as one asset set. Each NuGet package includes this upgrade guidance in its packaged README.

## Configuration and deployment updates

- The configuration sections are now `RealtimeStack` and `RealtimeStackSetup`, with corresponding double-underscore environment variables. Rename old `REALTIME_` script environment variables to `REALTIMESTACK_`.
- Default endpoint paths begin `/realtimestack/`; the default WebSocket subprotocol is `realtimestack.v1`. Envelope schema version remains `1.0`. Update reverse-proxy paths, ticket audiences, origin rules, and clients together. Explicit route configuration can retain existing paths during a controlled migration.
- The ASP.NET Core session entry defaults to `RealtimeStack.SessionId`; update the host that writes the session ID, or explicitly preserve the prior `AspNetCoreSessionIdKey` during migration.
- The default session cookie is `realtimestack_session`. Set the cookie option explicitly if the application must preserve an existing cookie name during migration.
- The Helm chart, generated release/service identities, browser filenames, and metric prefix use `realtimestack`. Update selectors, alert queries, dashboards, scrape configuration, ingress routes, and image references. Publish images use `ghcr.io/cormier-solutions-llc/realtimestack-gateway`.
- Preserve the existing configured Redis instance/key prefixes when continuity of sessions, tickets, or durable streams is required. Changing a prefix selects a different key space; this release does not move or delete Redis records. Review the generated plan before applying a new release name. Existing installed Helm releases are not renamed automatically.
- Run the matching renamed bootstrap wrappers and shared Node engine; do not mix scripts, chart values, gateway binaries, or SDK assets from different major versions.

For a rolling migration, keep the previous deployment available, deploy the new gateway and matching clients under their configured identities, verify readiness and authenticated traffic, then retire the previous deployment through the normal lifecycle process. Keep backups and the previous immutable image digest for rollback.

## Published history

Previously published NuGet and npm artifacts are immutable and retain the identifiers they were built with. New major revisions contain the renamed assemblies, bundled READMEs, and corrected repository metadata. GitHub release titles and descriptions use RealtimeStack and point readers to this migration guide; historical binary assets remain available under their original filenames.
