# ASP.NET Core package example

This minimal .NET 10 host consumes `RealtimeStack.AspNetCore`, enables standard ASP.NET Core session middleware, and maps the configurable realtimestack WebSocket and ticket endpoints. `MapRealtimeStackDiagnostics` also maps the enabled metrics endpoint; the operator diagnostics API remains disabled unless explicitly configured.

The example uses loopback-only development values and expects Redis at `127.0.0.1:6379`. Before connecting, the application that establishes the user session must store its shared Redis session identifier under `RealtimeStack.SessionId`. The existing Redis session record remains authoritative for tenant, user, topics, expiration, and revocation; this minimal host does not create login or session records.

```powershell
dotnet run --project .\examples\aspnet-core -- --urls http://127.0.0.1:5080
```

Override Redis, origin, route, and session settings through normal ASP.NET Core configuration for each environment.

## Saved messages and background tasks

See [durable integration](../../docs/persistence-integrations.md) for session-scoped replay, notifications, task status, and private server-worker writes. Enable and initialize persistence on the gateway using [RealtimeStack.Config](../../docs/configuration.md). The browser examples include optional saved-item and task lookup controls. Persistence does not itself execute import jobs.
