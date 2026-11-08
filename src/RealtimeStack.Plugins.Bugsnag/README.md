# RealtimeStack.Plugins.Bugsnag

A simple Bugsnag adapter for the common RealtimeStack error-reporting interface.

```text
dotnet add package RealtimeStack.Plugins.Bugsnag --version 2.0.0-beta
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using RealtimeStack.Plugins.Logging;
using RealtimeStack.Plugins.Bugsnag;

// Supply your application's configured Bugsnag client.
services.AddRealtimeStackBugsnagErrorHandler(bugsnagClient);
var reporter = serviceProvider.GetRequiredService<IRealtimeStackErrorReporter>();
var result = await reporter.ReportAsync(
    new RealtimeStackError(exception, operation: "ProcessOrder", correlationId: correlationId),
    cancellationToken);
```

This adapter depends on `RealtimeStack.Plugins.Logging` and `Bugsnag` 4.1.1. It supports .NET Standard 2.0 and .NET 10. Registration does not create, configure, flush, or dispose your client. The application owns SDK configuration, credentials, destinations, filters, and lifetime. Add other adapters to send one reported error to multiple providers.

Warning, error, and critical severities map to the provider's warning/error/fatal levels where available. Operation, correlation ID, and application-selected scalar metadata are attached per event, without changing shared/global logging context. Exception details can contain application data; supply only metadata appropriate for the configured destination.

BugSnag maps critical reports to its error severity and preserves the requested severity in metadata. Supply an existing `Bugsnag.IClient`; configure API key, release stage, endpoint, and redaction in your host. Disable overlapping automatic exception capture when using the optional RealtimeStack middleware to avoid duplicate reports. `Notify` may enqueue delivery; successful handling confirms acceptance by the SDK, not arrival at BugSnag. No report is sent by installation or DI registration. BugSnag SDK trimming/AOT behavior is owned by that SDK; this optional package is not pulled into the standalone Native AOT gateway by default.

## Upgrade to RealtimeStack 2

This is a new package in `2.0.0-beta`. It uses `RealtimeStack.*` namespaces, types, and registration methods. Install matching major versions of the common logging package and adapters. NuGet restores their bounded transitive dependencies automatically; it does not rewrite your application's imports or configuration. Follow the [complete upgrade guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/upgrading-to-realtimestack-2.md) when migrating an existing host. Existing logging configuration remains application-owned.
