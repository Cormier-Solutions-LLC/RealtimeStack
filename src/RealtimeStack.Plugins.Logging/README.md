# RealtimeStack.Plugins.Logging

Shared error-handler interfaces, dispatch, and DI registration for any RealtimeStack logging plugin.

```text
dotnet add package RealtimeStack.Plugins.Logging --version 2.0.3-beta
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using RealtimeStack.Plugins.Logging;

services.AddRealtimeStackLogging();
// Register any built-in adapter, or your own IRealtimeStackErrorHandler:
services.AddRealtimeStackErrorHandler<MyErrorHandler>();
var reporter = serviceProvider.GetRequiredService<IRealtimeStackErrorReporter>();
var result = await reporter.ReportAsync(
    new RealtimeStackError(exception, operation: "ProcessOrder", correlationId: correlationId),
    cancellationToken);
// Inspect result.AcceptedCount and result.Failures; rethrow/handle the original exception as required.
```

Implement `Task HandleAsync(RealtimeStackError error, CancellationToken cancellationToken)` in a custom handler. Registration is additive across handler types and idempotent for the same type. Handlers are singletons and must be thread-safe; their constructors can resolve application-owned dependencies from DI.

The dispatcher snapshots the provider list, passes the same immutable error to every provider in registration order, and returns individual provider failures without preventing later handlers from running. Requested cancellation propagates. Zero handlers produces `AcceptedCount = 0` and `Succeeded = false`. Metadata is copied when the error is created. Completion means a provider accepted the error; it does not prove remote delivery. Reporting does not swallow or change the original application exception. Providers must honor cancellation; callers can apply their own reporting deadline.

For ASP.NET Core, register adapters and call `app.UseRealtimeStackErrorHandling()` before the endpoints whose exceptions should be reported. This optional middleware reports an unhandled exception once and rethrows it to the normal host exception pipeline.

## Upgrade to RealtimeStack 2

This is a new package in `2.0.3-beta`. It uses `RealtimeStack.*` namespaces, types, and registration methods. Install matching major versions of the common logging package and adapters. NuGet restores their bounded transitive dependencies automatically; it does not rewrite your application's imports or configuration. Follow the [complete upgrade guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/upgrading-to-realtimestack-2.md) when migrating an existing host. Existing logging configuration remains application-owned.
