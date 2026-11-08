using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RealtimeStack.Plugins.Logging;
using Serilog.Events;

namespace RealtimeStack.Plugins.Serilog;

public sealed class SerilogErrorHandler : IRealtimeStackErrorHandler
{
    private readonly global::Serilog.ILogger logger;

    public SerilogErrorHandler(global::Serilog.ILogger logger) =>
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task HandleAsync(RealtimeStackError errorReport, CancellationToken cancellationToken)
    {
        if (errorReport is null) { throw new ArgumentNullException(nameof(errorReport)); }
        cancellationToken.ThrowIfCancellationRequested();
        var contextual = logger
            .ForContext("RealtimeStackOperation", errorReport.Operation)
            .ForContext("RealtimeStackCorrelationId", errorReport.CorrelationId);
        foreach (var item in errorReport.Metadata)
        {
            contextual = contextual.ForContext("RealtimeStackMetadata_" + item.Key, item.Value);
        }

        var level = errorReport.Severity switch
        {
            RealtimeStackErrorSeverity.Warning => LogEventLevel.Warning,
            RealtimeStackErrorSeverity.Critical => LogEventLevel.Fatal,
            _ => LogEventLevel.Error,
        };
        contextual.Write(level, errorReport.Exception, "Error reported by RealtimeStack");
        return Task.CompletedTask;
    }
}

public static class SerilogErrorHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddRealtimeStackSerilogErrorHandler(
        this IServiceCollection services,
        global::Serilog.ILogger logger)
    {
        services.AddRealtimeStackLogging();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRealtimeStackErrorHandler>(new SerilogErrorHandler(logger)));
        return services;
    }
}
