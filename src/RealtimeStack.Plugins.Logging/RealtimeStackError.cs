using System.Collections.ObjectModel;

namespace RealtimeStack.Plugins.Logging;

public enum RealtimeStackErrorSeverity
{
    Warning,
    Error,
    Critical,
}

/// <summary>An immutable error and application-selected context shared by logging plugins.</summary>
public sealed class RealtimeStackError
{
    public RealtimeStackError(
        Exception exception,
        string? operation = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        RealtimeStackErrorSeverity severity = RealtimeStackErrorSeverity.Error)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        if (severity != RealtimeStackErrorSeverity.Warning && severity != RealtimeStackErrorSeverity.Error && severity != RealtimeStackErrorSeverity.Critical)
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Operation = operation;
        CorrelationId = correlationId;
        Severity = severity;
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is not null)
        {
            foreach (var item in metadata)
            {
                snapshot.Add(item.Key, item.Value);
            }
        }

        Metadata = new ReadOnlyDictionary<string, string>(snapshot);
    }

    public Exception Exception { get; }
    public string? Operation { get; }
    public string? CorrelationId { get; }
    public RealtimeStackErrorSeverity Severity { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
}

/// <summary>Implemented by any provider that accepts a RealtimeStack errorReport.</summary>
public interface IRealtimeStackErrorHandler
{
    Task HandleAsync(RealtimeStackError errorReport, CancellationToken cancellationToken);
}

public interface IRealtimeStackErrorReporter
{
    Task<RealtimeStackErrorReportResult> ReportAsync(
        RealtimeStackError errorReport,
        CancellationToken cancellationToken = default);
}

public sealed class RealtimeStackErrorHandlerFailure
{
    internal RealtimeStackErrorHandlerFailure(string handler, Exception exception)
    {
        Handler = handler;
        Exception = exception;
    }

    public string Handler { get; }
    public Exception Exception { get; }
}

public sealed class RealtimeStackErrorReportResult
{
    internal RealtimeStackErrorReportResult(int acceptedCount, IList<RealtimeStackErrorHandlerFailure> failures)
    {
        AcceptedCount = acceptedCount;
        Failures = new ReadOnlyCollection<RealtimeStackErrorHandlerFailure>(failures);
    }

    /// <summary>Providers that accepted the error; this does not confirm remote delivery.</summary>
    public int AcceptedCount { get; }
    public IReadOnlyList<RealtimeStackErrorHandlerFailure> Failures { get; }
    public bool Succeeded => AcceptedCount > 0 && Failures.Count == 0;
}
