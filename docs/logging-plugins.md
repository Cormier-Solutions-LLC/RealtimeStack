# RealtimeStack logging plugins

`RealtimeStack.Plugins.Logging` is the common integration for generic error handlers. `RealtimeStack.Plugins.Bugsnag`, `RealtimeStack.Plugins.Serilog`, and `RealtimeStack.Plugins.Log4net` each depend on it. All four packages target .NET Standard 2.0 and .NET 10 and use coordinated version `2.0.3-beta`.

## Register a provider

```csharp
using RealtimeStack.Plugins.Logging;
using RealtimeStack.Plugins.Serilog;

// appLogger is the application's already configured Serilog.ILogger.
builder.Services.AddRealtimeStackSerilogErrorHandler(appLogger);
```

Use `AddRealtimeStackBugsnagErrorHandler(bugsnagClient)` with a configured `Bugsnag.IClient`, or `AddRealtimeStackLog4netErrorHandler(log4netLogger)` with a configured `log4net.ILog`. Install the matching adapter package for each extension. Providers are additive; duplicate registration of the same adapter type is ignored. The host owns client/logger configuration, destinations, credentials, filtering, redaction, flushing, and disposal.

## Report an error

```csharp
var reporter = serviceProvider.GetRequiredService<IRealtimeStackErrorReporter>();
try
{
    await ProcessOrderAsync(cancellationToken);
}
catch (Exception exception)
{
    var result = await reporter.ReportAsync(
        new RealtimeStackError(exception, operation: "ProcessOrder", correlationId: correlationId),
        cancellationToken);
    // Inspect AcceptedCount and Failures according to application policy.
    throw;
}
```

The dispatcher attempts each registered handler, preserving each failure in the result so one broken provider does not prevent others from accepting the report. Requested cancellation propagates; providers must honor it. An empty provider list returns `Succeeded = false`. Acceptance does not prove network delivery. Metadata is an immutable snapshot of application-selected scalar strings; the integration never automatically adds headers, cookies, request bodies, connection strings, or query strings.

## Implement any logging provider

```csharp
using RealtimeStack.Plugins.Logging;

public sealed class ApplicationErrorHandler : IRealtimeStackErrorHandler
{
    public Task HandleAsync(RealtimeStackError errorReport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Replace this with the application's destination or SDK.
        Console.Error.WriteLine(errorReport.Exception);
        return Task.CompletedTask;
    }
}

// Register alongside any built-in adapters:
// builder.Services.AddRealtimeStackErrorHandler<ApplicationErrorHandler>();
```

Handlers are singleton services and must be thread-safe. The error object carries the original exception, operation, correlation ID, metadata snapshot, and warning/error/critical severity. Serilog and log4net use warning/error/fatal levels. BugSnag uses warning/error and includes the requested critical severity in its RealtimeStack metadata section.

## ASP.NET Core integration

```csharp
using RealtimeStack.AspNetCore;
// Place the host's exception-response middleware outside this middleware.
app.UseRealtimeStackErrorHandling();
// Map application endpoints afterwards.
```

This optional middleware reports unhandled exceptions and rethrows the original exception. It skips aborted requests, uses an operation label plus trace ID, and bounds its reporting wait to five seconds by default. A custom timeout must be positive and at most one minute. Reporting outcomes are available in `HttpContext.Items` under `RealtimeStack.ErrorReportResult` or `RealtimeStack.ErrorReportingFailure`. A timeout bounds the request wait; providers that ignore cancellation may complete later.

Disable overlapping BugSnag automatic exception capture if the same exception would also be reported by the middleware. Installation and registration do not create an SDK client or transmit reports. Built-in providers do not mutate ambient/global log context. Their SDK packages are opt-in; the default Native AOT gateway depends only on the common provider-neutral Logging package. Validate optional SDK trimming/AOT behavior in any application that includes those SDKs.

## Upgrade to RealtimeStack 2

These four packages are new in `2.0.3-beta`. NuGet automatically resolves their internal dependencies in the declared compatible range. Every NuGet artifact includes a README with setup and upgrade guidance. Existing hosts should follow the [major-version upgrade guide](upgrading-to-realtimestack-2.md) for source, configuration, browser, and deployment changes.
