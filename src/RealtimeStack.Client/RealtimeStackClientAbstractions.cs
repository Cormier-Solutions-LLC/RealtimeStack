using System.Collections.ObjectModel;
using RealtimeStack.Contracts;

namespace RealtimeStack.Client;

public enum RealtimeStackClientState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Stopping,
    Faulted,
}

public enum RealtimeStackClientLogLevel
{
    Trace,
    Information,
    Warning,
    Error,
}

public interface IRealtimeStackClientLogger
{
    void Log(RealtimeStackClientLogLevel level, int eventId, string message);
}

public sealed class NullRealtimeStackClientLogger : IRealtimeStackClientLogger
{
    public static NullRealtimeStackClientLogger Instance { get; } = new();

    private NullRealtimeStackClientLogger()
    {
    }

    public void Log(RealtimeStackClientLogLevel level, int eventId, string message)
    {
    }
}

public interface IRealtimeStackClientClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemRealtimeStackClientClock : IRealtimeStackClientClock
{
    public static SystemRealtimeStackClientClock Instance { get; } = new();

    private SystemRealtimeStackClientClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public interface IRealtimeStackRetryPolicy
{
    TimeSpan GetDelay(int attempt, RealtimeStackTransportClose? close);
}

public interface IRealtimeStackRandom
{
    double NextDouble();
}

public sealed class SystemRealtimeStackRandom : IRealtimeStackRandom
{
    private static readonly Random Random = new();
    private static readonly object Sync = new();

    public static SystemRealtimeStackRandom Instance { get; } = new();

    private SystemRealtimeStackRandom()
    {
    }

    public double NextDouble()
    {
        lock (Sync)
        {
            return Random.NextDouble();
        }
    }
}

public interface IRealtimeStackAuthenticationProvider
{
    Task<RealtimeStackAuthenticationMaterial> GetAuthenticationAsync(CancellationToken cancellationToken);
}

public sealed class AnonymousRealtimeStackAuthenticationProvider : IRealtimeStackAuthenticationProvider
{
    public static AnonymousRealtimeStackAuthenticationProvider Instance { get; } = new();

    private AnonymousRealtimeStackAuthenticationProvider()
    {
    }

    public Task<RealtimeStackAuthenticationMaterial> GetAuthenticationAsync(CancellationToken cancellationToken) =>
        Task.FromResult(RealtimeStackAuthenticationMaterial.Anonymous);
}

public sealed class RealtimeStackAuthenticationMaterial
{
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public static RealtimeStackAuthenticationMaterial Anonymous { get; } = new();

    public RealtimeStackAuthenticationMaterial(
        string? connectionTicket = null,
        string? cookieHeader = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ConnectionTicket = connectionTicket;
        CookieHeader = cookieHeader;
        Headers = headers is null
            ? EmptyHeaders
            : new ReadOnlyDictionary<string, string>(headers.ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    public string? ConnectionTicket { get; }

    public string? CookieHeader { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public override string ToString() => "RealtimeStackAuthenticationMaterial { [REDACTED] }";
}

public sealed record RealtimeStackTransportClose(
    int? Code,
    string Reason,
    bool Clean,
    ReconnectAdvice? Reconnect = null);

public sealed record RealtimeStackTransportReceiveResult(byte[]? Payload, RealtimeStackTransportClose? Close)
{
    public static RealtimeStackTransportReceiveResult Message(byte[] payload) => new(payload, null);

    public static RealtimeStackTransportReceiveResult Closed(
        int? code,
        string reason,
        bool clean,
        ReconnectAdvice? reconnect = null) =>
        new(null, new RealtimeStackTransportClose(code, reason, clean, reconnect));
}

public interface IRealtimeStackTransport : IDisposable
{
    Task SendAsync(byte[] payload, CancellationToken cancellationToken);

    Task<RealtimeStackTransportReceiveResult> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync(int closeCode, string reason, CancellationToken cancellationToken);
}

public interface IRealtimeStackTransportFactory
{
    Task<IRealtimeStackTransport> ConnectAsync(
        Uri endpoint,
        RealtimeStackAuthenticationMaterial authentication,
        string subProtocol,
        int maximumFrameBytes,
        int maximumMessageBytes,
        CancellationToken cancellationToken);
}

public sealed class RealtimeStackClientStateChangedEventArgs(
    RealtimeStackClientState previous,
    RealtimeStackClientState current) : EventArgs
{
    public RealtimeStackClientState Previous { get; } = previous;

    public RealtimeStackClientState Current { get; } = current;
}

public class RealtimeStackClientException : Exception
{
    public RealtimeStackClientException(string message)
        : base(message)
    {
    }

}

public sealed class RealtimeStackProtocolException : RealtimeStackClientException
{
    public RealtimeStackProtocolException(string message, int closeCode = RealtimeStackCloseCodes.ProtocolError)
        : base(message)
    {
        CloseCode = closeCode;
    }

    public int CloseCode { get; }
}
