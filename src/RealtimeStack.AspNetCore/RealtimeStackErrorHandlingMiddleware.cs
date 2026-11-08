using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RealtimeStack.Plugins.Logging;

namespace RealtimeStack.AspNetCore;

/// <summary>Reports unhandled request errors and preserves the original exception pipeline.</summary>
public sealed class RealtimeStackErrorHandlingMiddleware
{
    private readonly RequestDelegate next;
    private readonly TimeSpan reportingTimeout;

    public RealtimeStackErrorHandlingMiddleware(RequestDelegate next, TimeSpan reportingTimeout)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (reportingTimeout <= TimeSpan.Zero || reportingTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(reportingTimeout));
        }

        this.next = next;
        this.reportingTimeout = reportingTimeout;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The original request exception is always rethrown; reporting failures are preserved separately on the request.")]
    public async Task InvokeAsync(HttpContext context, IRealtimeStackErrorReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reporter);
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            try
            {
                context.Items["RealtimeStack.ErrorReportResult"] = await reporter.ReportAsync(
                    new RealtimeStackError(exception, operation: "HTTP request", correlationId: context.TraceIdentifier),
                    context.RequestAborted).WaitAsync(reportingTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception reportingException)
            {
                context.Items["RealtimeStack.ErrorReportingFailure"] = reportingException;
            }

            throw;
        }
    }
}

public static class RealtimeStackErrorHandlingApplicationBuilderExtensions
{
    public static IApplicationBuilder UseRealtimeStackErrorHandling(
        this IApplicationBuilder app,
        TimeSpan? reportingTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<RealtimeStackErrorHandlingMiddleware>(reportingTimeout ?? TimeSpan.FromSeconds(5));
    }
}
