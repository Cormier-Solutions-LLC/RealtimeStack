# RealtimeStack.Plugins.Log4net

A simple Log4net adapter for the common RealtimeStack error-reporting interface.

```text
dotnet add package RealtimeStack.Plugins.Log4net --version 2.0.1-beta
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using RealtimeStack.Plugins.Logging;
using RealtimeStack.Plugins.Log4net;

// Supply your application's configured Log4net logger.
services.AddRealtimeStackLog4netErrorHandler(log4netLogger);
var reporter = serviceProvider.GetRequiredService<IRealtimeStackErrorReporter>();
var result = await reporter.ReportAsync(
    new RealtimeStackError(exception, operation: "ProcessOrder", correlationId: correlationId),
    cancellationToken);
```

This adapter depends on `RealtimeStack.Plugins.Logging` and `log4net` 3.5.0. It supports .NET Standard 2.0 and .NET 10. Registration does not create, configure, flush, or dispose your logger. The application owns SDK configuration, credentials, destinations, filters, and lifetime. Add other adapters to send one reported error to multiple providers.

Warning, error, and critical severities map to the provider's warning/error/fatal levels where available. Operation, correlation ID, and application-selected scalar metadata are attached per event, without changing shared/global logging context. Exception details can contain application data; supply only metadata appropriate for the configured destination.

Provider-level filtering still applies. This adapter does not add console/file/network sinks or configure appenders. Optional SDK dependencies are isolated from the default standalone gateway; validate their trimming/AOT support if your application explicitly includes them.

## Upgrade to RealtimeStack 2

This is a new package in `2.0.1-beta`. It uses `RealtimeStack.*` namespaces, types, and registration methods. Install matching major versions of the common logging package and adapters. NuGet restores their bounded transitive dependencies automatically; it does not rewrite your application's imports or configuration. Follow the [complete upgrade guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/upgrading-to-realtimestack-2.md) when migrating an existing host. Existing logging configuration remains application-owned.
