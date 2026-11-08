using Exception = System.Exception;
using Bugsnag;
using Bugsnag.Payload;
using log4net;
using log4net.Appender;
using log4net.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RealtimeStack.AspNetCore;
using RealtimeStack.Plugins.Bugsnag;
using RealtimeStack.Plugins.Log4net;
using RealtimeStack.Plugins.Logging;
using RealtimeStack.Plugins.Serilog;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RealtimeStack.Plugins.Tests;

public sealed class LoggingPluginTests
{
    [Fact]
    public async Task DispatchContinuesAfterProviderFailureAndReturnsItsCause()
    {
        var failure = new InvalidOperationException("provider unavailable");
        var received = new List<RealtimeStackError>();
        var reporter = new RealtimeStackErrorReporter([
            new CallbackHandler((_, _) => Task.FromException(failure)),
            new CallbackHandler((entry, _) => { received.Add(entry); return Task.CompletedTask; }),
        ]);
        var entry = new RealtimeStackError(new IOException("application failure"), "job", "correlation");

        var result = await reporter.ReportAsync(entry);

        Assert.Equal(1, result.AcceptedCount);
        Assert.False(result.Succeeded);
        Assert.Same(failure, Assert.Single(result.Failures).Exception);
        Assert.Same(entry, Assert.Single(received));
    }

    [Fact]
    public async Task RequestedCancellationStopsDispatchWithoutBecomingAProviderFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var laterCalls = 0;
        var reporter = new RealtimeStackErrorReporter([
            new CallbackHandler((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }),
            new CallbackHandler((_, _) => { laterCalls++; return Task.CompletedTask; }),
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reporter.ReportAsync(
            new RealtimeStackError(new IOException("failure")), cancellation.Token));
        Assert.Equal(0, laterCalls);
    }

    [Fact]
    public async Task MissingHandlersAreVisibleAndMetadataIsSnapshotted()
    {
        var metadata = new Dictionary<string, string> { ["job"] = "original" };
        var entry = new RealtimeStackError(new IOException("failure"), metadata: metadata);
        metadata["job"] = "changed";

        var result = await new RealtimeStackErrorReporter([]).ReportAsync(entry);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Empty(result.Failures);
        Assert.Equal("original", entry.Metadata["job"]);
    }

    [Fact]
    public void BuiltInRegistrationIsAdditiveAndIdempotent()
    {
        using var serilog = new LoggerConfiguration().CreateLogger();
        var log4net = LogManager.GetLogger(typeof(LoggingPluginTests));
        var services = new ServiceCollection();
        services.AddRealtimeStackSerilogErrorHandler(serilog).AddRealtimeStackSerilogErrorHandler(serilog);
        services.AddRealtimeStackLog4netErrorHandler(log4net);
        services.AddRealtimeStackBugsnagErrorHandler(new RecordingBugsnagClient());
        services.AddRealtimeStackErrorHandler<CustomHandler>().AddRealtimeStackErrorHandler<CustomHandler>();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(4, provider.GetServices<IRealtimeStackErrorHandler>().Count());
        Assert.Same(provider.GetRequiredService<IRealtimeStackErrorReporter>(), provider.GetRequiredService<IRealtimeStackErrorReporter>());
    }

    [Theory]
    [InlineData(RealtimeStackErrorSeverity.Warning, LogEventLevel.Warning)]
    [InlineData(RealtimeStackErrorSeverity.Error, LogEventLevel.Error)]
    [InlineData(RealtimeStackErrorSeverity.Critical, LogEventLevel.Fatal)]
    public async Task SerilogPreservesExceptionsAndIsolatesPerEventContext(RealtimeStackErrorSeverity severity, LogEventLevel level)
    {
        var sink = new RecordingSerilogSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var handler = new SerilogErrorHandler(logger);
        var failure = new IOException("failure");

        await handler.HandleAsync(new RealtimeStackError(failure, "first", "correlation", new Dictionary<string, string> { ["job"] = "42" }, severity), CancellationToken.None);
        await handler.HandleAsync(new RealtimeStackError(failure, "second"), CancellationToken.None);

        Assert.Equal(level, sink.Events[0].Level);
        Assert.Same(failure, sink.Events[0].Exception);
        Assert.Equal("first", ((ScalarValue)sink.Events[0].Properties["RealtimeStackOperation"]).Value);
        Assert.Equal("42", ((ScalarValue)sink.Events[0].Properties["RealtimeStackMetadata_job"]).Value);
        Assert.False(sink.Events[1].Properties.ContainsKey("RealtimeStackMetadata_job"));
    }

    [Fact]
    public async Task Log4netPreservesExceptionSeverityAndStructuredContext()
    {
        var repository = LogManager.CreateRepository(Guid.NewGuid().ToString("N"));
        try
        {
            var appender = new MemoryAppender();
            BasicConfigurator.Configure(repository, appender);
            var handler = new Log4netErrorHandler(LogManager.GetLogger(repository.Name, "test"));
            var failure = new IOException("failure");

            await handler.HandleAsync(new RealtimeStackError(failure, "operation", "correlation", new Dictionary<string, string> { ["job"] = "42" }, RealtimeStackErrorSeverity.Critical), CancellationToken.None);

            var entry = Assert.Single(appender.GetEvents());
            Assert.Same(failure, entry.ExceptionObject);
            Assert.Equal(log4net.Core.Level.Fatal, entry.Level);
            Assert.Equal("operation", entry.LookupProperty("RealtimeStackOperation"));
            Assert.Equal("42", entry.LookupProperty("RealtimeStackMetadata_job"));
        }
        finally { repository.Shutdown(); }
    }

    [Theory]
    [InlineData(RealtimeStackErrorSeverity.Warning, Severity.Warning)]
    [InlineData(RealtimeStackErrorSeverity.Error, Severity.Error)]
    [InlineData(RealtimeStackErrorSeverity.Critical, Severity.Error)]
    public async Task BugsnagMapsReportsWithoutSendingNetworkTraffic(RealtimeStackErrorSeverity severity, Severity expected)
    {
        var client = new RecordingBugsnagClient();
        var failure = new IOException("failure");

        await new BugsnagErrorHandler(client).HandleAsync(new RealtimeStackError(
            failure, "operation", "correlation", new Dictionary<string, string> { ["job"] = "42" }, severity), CancellationToken.None);

        Assert.Same(failure, client.Exception);
        Assert.Equal(expected, client.Severity);
        var report = new Report(client.Configuration, failure, HandledState.ForHandledException(), [], null!);
        Assert.NotNull(client.Callback);
        client.Callback(report);
        Assert.Equal("operation", report.Event.Context);
        var metadata = Assert.IsType<Dictionary<string, string>>(report.Event.Metadata["RealtimeStack"]);
        Assert.Equal("42", metadata["job"]);
        Assert.Equal("correlation", metadata["correlationId"]);
        Assert.Equal(severity.ToString(), metadata["severity"]);
    }

    [Fact]
    public async Task MiddlewarePreservesTheOriginalExceptionWhenReportingFails()
    {
        var original = new IOException("application failure");
        var reporting = new InvalidOperationException("provider failure");
        var reporter = new RealtimeStackErrorReporter([new CallbackHandler((_, _) => Task.FromException(reporting))]);
        var context = new DefaultHttpContext();
        var middleware = new RealtimeStackErrorHandlingMiddleware(_ => Task.FromException(original), TimeSpan.FromSeconds(1));

        var thrown = await Assert.ThrowsAsync<IOException>(() => middleware.InvokeAsync(context, reporter));

        Assert.Same(original, thrown);
        var result = Assert.IsType<RealtimeStackErrorReportResult>(context.Items["RealtimeStack.ErrorReportResult"]);
        Assert.Same(reporting, Assert.Single(result.Failures).Exception);
    }

    [Fact]
    public async Task MiddlewareDoesNotWaitForeverForAnUnresponsiveProvider()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new IOException("application failure");
        var reporter = new RealtimeStackErrorReporter([new CallbackHandler((_, _) => blocked.Task)]);
        var context = new DefaultHttpContext();
        var middleware = new RealtimeStackErrorHandlingMiddleware(_ => Task.FromException(original), TimeSpan.FromMilliseconds(50));
        try
        {
            Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => middleware.InvokeAsync(context, reporter)));
            Assert.IsType<TimeoutException>(context.Items["RealtimeStack.ErrorReportingFailure"]);
        }
        finally { blocked.TrySetResult(); }
    }

    private sealed class CallbackHandler(Func<RealtimeStackError, CancellationToken, Task> callback) : IRealtimeStackErrorHandler
    {
        public Task HandleAsync(RealtimeStackError errorReport, CancellationToken cancellationToken) => callback(errorReport, cancellationToken);
    }

    public sealed class CustomHandler : IRealtimeStackErrorHandler
    {
        public Task HandleAsync(RealtimeStackError errorReport, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingSerilogSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class RecordingBugsnagClient : IClient
    {
        public Exception? Exception { get; private set; }
        public Severity Severity { get; private set; }
        public Middleware? Callback { get; private set; }
        public IConfiguration Configuration { get; } = new Configuration("01234567890123456789012345678901");
        public IBreadcrumbs Breadcrumbs => throw new NotSupportedException();
        public ISessionTracker SessionTracking => throw new NotSupportedException();
        public void Notify(Exception exception, Severity severity, Middleware callback) { Exception = exception; Severity = severity; Callback = callback; }
        public void Notify(Exception exception) => throw new NotSupportedException();
        public void Notify(Exception exception, Middleware callback) => throw new NotSupportedException();
        public void Notify(Exception exception, Severity severity) => throw new NotSupportedException();
        public void Notify(Exception exception, HandledState handledState) => throw new NotSupportedException();
        public void Notify(Exception exception, HandledState handledState, Middleware callback) => throw new NotSupportedException();
        public void Notify(Report report, Middleware callback) => throw new NotSupportedException();
        public void BeforeNotify(Middleware middleware) => throw new NotSupportedException();
    }
}
