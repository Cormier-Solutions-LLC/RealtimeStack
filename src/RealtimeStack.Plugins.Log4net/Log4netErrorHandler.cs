using log4net;
using log4net.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RealtimeStack.Plugins.Logging;

namespace RealtimeStack.Plugins.Log4net;

public sealed class Log4netErrorHandler : IRealtimeStackErrorHandler
{
    private readonly ILog logger;

    public Log4netErrorHandler(ILog logger) => this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task HandleAsync(RealtimeStackError errorReport, CancellationToken cancellationToken)
    {
        if (errorReport is null) { throw new ArgumentNullException(nameof(errorReport)); }
        cancellationToken.ThrowIfCancellationRequested();
        var level = errorReport.Severity switch
        {
            RealtimeStackErrorSeverity.Warning => Level.Warn,
            RealtimeStackErrorSeverity.Critical => Level.Fatal,
            _ => Level.Error,
        };
        if (logger.Logger.IsEnabledFor(level))
        {
            var entry = new LoggingEvent(typeof(Log4netErrorHandler), logger.Logger.Repository,
                logger.Logger.Name, level, "Error reported by RealtimeStack", errorReport.Exception);
            entry.Properties["RealtimeStackOperation"] = errorReport.Operation;
            entry.Properties["RealtimeStackCorrelationId"] = errorReport.CorrelationId;
            foreach (var item in errorReport.Metadata)
            {
                entry.Properties["RealtimeStackMetadata_" + item.Key] = item.Value;
            }

            logger.Logger.Log(entry);
        }

        return Task.CompletedTask;
    }
}

public static class Log4netErrorHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddRealtimeStackLog4netErrorHandler(this IServiceCollection services, ILog logger)
    {
        services.AddRealtimeStackLogging();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRealtimeStackErrorHandler>(new Log4netErrorHandler(logger)));
        return services;
    }
}
