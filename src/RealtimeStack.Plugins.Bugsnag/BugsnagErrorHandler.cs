using Bugsnag;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RealtimeStack.Plugins.Logging;

namespace RealtimeStack.Plugins.Bugsnag;

public sealed class BugsnagErrorHandler : IRealtimeStackErrorHandler
{
    private readonly IClient client;

    public BugsnagErrorHandler(IClient client) => this.client = client ?? throw new ArgumentNullException(nameof(client));

    public Task HandleAsync(RealtimeStackError errorReport, CancellationToken cancellationToken)
    {
        if (errorReport is null) { throw new ArgumentNullException(nameof(errorReport)); }
        cancellationToken.ThrowIfCancellationRequested();
        var severity = errorReport.Severity == RealtimeStackErrorSeverity.Warning ? Severity.Warning : Severity.Error;
        client.Notify(errorReport.Exception, severity, report =>
        {
            if (errorReport.Operation is not null) { report.Event.Context = errorReport.Operation; }
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in errorReport.Metadata) { metadata.Add(item.Key, item.Value); }
            if (errorReport.CorrelationId is not null) { metadata["correlationId"] = errorReport.CorrelationId; }
            metadata["severity"] = errorReport.Severity.ToString();
            report.Event.Metadata["RealtimeStack"] = metadata;
        });
        return Task.CompletedTask;
    }
}

public static class BugsnagErrorHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddRealtimeStackBugsnagErrorHandler(this IServiceCollection services, IClient client)
    {
        services.AddRealtimeStackLogging();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRealtimeStackErrorHandler>(new BugsnagErrorHandler(client)));
        return services;
    }
}
