using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RealtimeStack.Plugins.Logging;

/// <summary>Dispatches to all registered providers without allowing one failure to hide another provider.</summary>
public sealed class RealtimeStackErrorReporter : IRealtimeStackErrorReporter
{
    private readonly IRealtimeStackErrorHandler[] handlers;

    public RealtimeStackErrorReporter(IEnumerable<IRealtimeStackErrorHandler> handlers)
    {
        if (handlers is null) { throw new ArgumentNullException(nameof(handlers)); }
        this.handlers = handlers.ToArray();
        if (this.handlers.Any(handler => handler is null))
        {
            throw new ArgumentException("Error handlers must not contain null entries.", nameof(handlers));
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Provider failures are returned to the caller while other providers are still attempted.")]
    public async Task<RealtimeStackErrorReportResult> ReportAsync(
        RealtimeStackError errorReport,
        CancellationToken cancellationToken = default)
    {
        if (errorReport is null) { throw new ArgumentNullException(nameof(errorReport)); }
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = 0;
        var failures = new List<RealtimeStackErrorHandlerFailure>();
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await handler.HandleAsync(errorReport, cancellationToken).ConfigureAwait(false);
                accepted++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(new RealtimeStackErrorHandlerFailure(handler.GetType().FullName ?? handler.GetType().Name, exception));
            }
        }

        return new RealtimeStackErrorReportResult(accepted, failures);
    }
}

public static class RealtimeStackLoggingServiceCollectionExtensions
{
    public static IServiceCollection AddRealtimeStackLogging(this IServiceCollection services)
    {
        if (services is null) { throw new ArgumentNullException(nameof(services)); }
        services.TryAddSingleton<IRealtimeStackErrorReporter, RealtimeStackErrorReporter>();
        return services;
    }

    public static IServiceCollection AddRealtimeStackErrorHandler<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this IServiceCollection services)
        where THandler : class, IRealtimeStackErrorHandler
    {
        services.AddRealtimeStackLogging();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRealtimeStackErrorHandler, THandler>());
        return services;
    }
}
