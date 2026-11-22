using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using RealtimeStack.Contracts;
using StackExchange.Redis;

namespace RealtimeStack.Gateway;

public sealed class RealtimeStackConnectionRegistry
{
    private static readonly ReconnectAdvice RestartAdvice = new(500, 30_000, 0.2, true);
    private readonly ConcurrentDictionary<string, RealtimeStackConnection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string TenantId, string UserId), int> _connectionsByIdentity = new();
    private readonly object _admissionLock = new();
    private readonly GatewayMetrics _metrics;
    private readonly RealtimeStackAuthenticator? _authenticator;
    private readonly RealtimeStackOptions _options;
    private int _draining;
    private int _count;

    public RealtimeStackConnectionRegistry(GatewayMetrics metrics, RealtimeStackAuthenticator? authenticator = null)
        : this(metrics, authenticator, new RealtimeStackOptions())
    {
    }

    public RealtimeStackConnectionRegistry(
        GatewayMetrics metrics,
        RealtimeStackAuthenticator? authenticator,
        RealtimeStackOptions options)
    {
        _metrics = metrics;
        _authenticator = authenticator;
        _options = options;
    }

    public int Count => Volatile.Read(ref _count);

    public bool IsDraining => Volatile.Read(ref _draining) == 1;

    public (int Connections, int AuthenticatedSessions, int Subscriptions, int QueuedMessages) GetAggregateSnapshot()
    {
        var connections = _connections.Values.ToArray();
        return (
            connections.Length,
            connections.Select(connection => connection.SessionId ?? connection.Id).Distinct(StringComparer.Ordinal).Count(),
            connections.Sum(connection => connection.SubscriptionCount),
            connections.Sum(connection => connection.QueuedMessageCount));
    }

    public ConnectionDiagnostic[] GetDiagnostics(int offset, int limit) => _connections.Values
        .OrderBy(connection => connection.CreatedAt)
        .Skip(offset)
        .Take(limit)
        .Select(connection => new ConnectionDiagnostic(
            OpaqueReference(connection.Id),
            connection.CreatedAt,
            connection.LastActivity,
            connection.SubscriptionCount,
            connection.QueuedMessageCount))
        .ToArray();

    public void BeginDrain()
    {
        lock (_admissionLock)
        {
            Volatile.Write(ref _draining, 1);
        }
    }

    public bool Add(RealtimeStackConnection connection)
    {
        lock (_admissionLock)
        {
            var identityCount = _connectionsByIdentity.GetValueOrDefault(connection.AdmissionIdentity);
            if (IsDraining || _count >= _options.MaximumConnections ||
                identityCount >= _options.MaximumConnectionsPerIdentity ||
                !_connections.TryAdd(connection.Id, connection))
            {
                return false;
            }

            _connectionsByIdentity[connection.AdmissionIdentity] = identityCount + 1;
            Volatile.Write(ref _count, _count + 1);
            _metrics.RecordConnectionOpened();
            return true;
        }
    }

    public bool Remove(string connectionId, string reason)
    {
        lock (_admissionLock)
        {
            if (!_connections.TryRemove(connectionId, out var connection))
            {
                return false;
            }

            var key = connection.AdmissionIdentity;
            var remaining = _connectionsByIdentity[key] - 1;
            if (remaining == 0)
            {
                _connectionsByIdentity.Remove(key);
            }
            else
            {
                _connectionsByIdentity[key] = remaining;
            }

            Volatile.Write(ref _count, _count - 1);
            _metrics.RecordConnectionClosed(reason, DateTimeOffset.UtcNow - connection.CreatedAt);
            return true;
        }
    }

    public async ValueTask DeliverAsync(RealtimeStackBusMessage message, CancellationToken cancellationToken)
    {
        List<Task>? closeTasks = null;
        var envelope = new ServerMessageEnvelope(
            ProtocolVersions.Current,
            ProtocolMessageTypes.Event,
            message.CorrelationId,
            message.Timestamp,
            message.UserId is null
                ? $"topics/{message.Topic}"
                : $"users/{message.UserId}/topics/{message.Topic}",
            message.Payload);

        foreach (var connection in _connections.Values)
        {
            var identity = connection.Identity;
            if (!IsPotentialRecipient(connection, identity, message))
            {
                continue;
            }

            if (connection.SessionId is not null && _authenticator is not null)
            {
                try
                {
                    var refreshed = await _authenticator.RevalidateSessionAsync(
                        connection.SessionId,
                        cancellationToken);
                    if (refreshed is null ||
                        !string.Equals(refreshed.TenantId, identity.TenantId, StringComparison.Ordinal) ||
                        !string.Equals(refreshed.UserId, identity.UserId, StringComparison.Ordinal) ||
                        identity.RegisteredIdentity is not null && refreshed.RegisteredIdentity != identity.RegisteredIdentity)
                    {
                        AddBoundedClose(
                            ref closeTasks,
                            connection,
                            identity.RegisteredIdentity is null ? RealtimeStackCloseStatus.AuthenticationExpired : WebSocketCloseStatus.PolicyViolation,
                            "authentication_expired",
                            cancellationToken);
                        continue;
                    }

                    connection.UpdateIdentity(refreshed);
                    identity = refreshed;
                }
                catch (RedisException)
                {
                    AddBoundedClose(
                        ref closeTasks,
                        connection,
                        WebSocketCloseStatus.InternalServerError,
                        "authentication_unavailable",
                        cancellationToken);
                    continue;
                }
            }

            if (DateTimeOffset.UtcNow >= identity.ExpiresAt)
            {
                AddBoundedClose(
                    ref closeTasks,
                    connection,
                    RealtimeStackCloseStatus.AuthenticationExpired,
                    "authentication_expired",
                    cancellationToken);
                continue;
            }

            if (!IsPotentialRecipient(connection, identity, message))
            {
                continue;
            }

            if (!connection.TryEnqueue(envelope) &&
                connection.HasExceededSlowConsumerLimit &&
                connection.TryMarkSlowConsumerDisconnect())
            {
                _metrics.RecordSlowConsumerDisconnect();
                AddBoundedClose(
                    ref closeTasks,
                    connection,
                    RealtimeStackCloseStatus.SlowConsumer,
                    "slow_consumer",
                    cancellationToken);
            }
        }

        if (closeTasks is not null)
        {
            await Task.WhenAll(closeTasks);
        }
    }

    private static bool IsPotentialRecipient(
        RealtimeStackConnection connection,
        RealtimeStackIdentity identity,
        RealtimeStackBusMessage message) =>
        string.Equals(identity.TenantId, message.TenantId, StringComparison.Ordinal) &&
        (message.UserId is null ||
            string.Equals(identity.UserId, message.UserId, StringComparison.Ordinal)) &&
        identity.AllowedTopics.Any(allowed =>
            allowed == "*" || string.Equals(allowed, message.Topic, StringComparison.Ordinal)) &&
        connection.IsSubscribed(message.Topic, message.UserId);

    private static void AddBoundedClose(
        ref List<Task>? closeTasks,
        RealtimeStackConnection connection,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        closeTasks ??= [];
        closeTasks.Add(connection.RequestCloseAsync(status, description, cancellationToken).AsTask());
    }

    public ValueTask NotifyServiceRestartAsync()
    {
        var message = new ServerMessageEnvelope(
            ProtocolVersions.Current,
            ProtocolMessageTypes.ServiceRestart,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            "system/restart",
            Reconnect: RestartAdvice);
        foreach (var connection in _connections.Values)
        {
            connection.TryEnqueue(message);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask CloseAllAsync(CancellationToken cancellationToken)
    {
        foreach (var connection in _connections.Values)
        {
            await connection.RequestCloseAsync(
                RealtimeStackCloseStatus.ServiceRestart,
                "service_restart",
                cancellationToken);
        }
    }

    private static string OpaqueReference(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
