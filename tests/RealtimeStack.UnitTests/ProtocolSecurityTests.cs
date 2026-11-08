using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RealtimeStack.Contracts;
using RealtimeStack.Gateway;
using RealtimeStack.Redis;

namespace RealtimeStack.UnitTests;

public sealed class ProtocolSecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonElement Payload = JsonSerializer.SerializeToElement(new { value = 42 });

    [Fact]
    public void ProtocolValidatorAcceptsCurrentVersion()
    {
        var result = ProtocolValidator.Validate(
            Envelope(correlationId: "correlation:tenant/orders/1"),
            Now);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("0.9", ProtocolErrorCodes.UnsupportedVersion)]
    [InlineData("1.0", ProtocolErrorCodes.UnsupportedType, "unknown")]
    public void ProtocolValidatorReturnsStructuredCompatibilityErrors(
        string version,
        string expectedCode,
        string type = ProtocolMessageTypes.Publish)
    {
        var result = ProtocolValidator.Validate(Envelope(version, type), Now);

        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Fact]
    public void PublishRequiresPayload()
    {
        var envelope = new MessageEnvelope(
            ProtocolVersions.Current,
            ProtocolMessageTypes.Publish,
            "correlation-1",
            Now,
            "topics/orders",
            default);

        var result = ProtocolValidator.Validate(envelope, Now);

        Assert.False(result.IsValid);
        Assert.Equal(ProtocolErrorCodes.InvalidEnvelope, result.ErrorCode);
    }

    [Fact]
    public void RouteAuthorizationUsesServerTenantAndCurrentUser()
    {
        var identity = Identity();

        Assert.True(RealtimeStackRouteAuthorizer.TryAuthorize(identity, "topics/orders", out var tenantRoute));
        Assert.Equal("orders", tenantRoute.Topic);
        Assert.True(RealtimeStackRouteAuthorizer.TryAuthorize(identity, "users/user-1/topics/orders", out _));
        Assert.False(RealtimeStackRouteAuthorizer.TryAuthorize(identity, "users/user-2/topics/orders", out _));
        Assert.False(RealtimeStackRouteAuthorizer.TryAuthorize(identity, "topics/admin", out _));
        Assert.False(RealtimeStackRouteAuthorizer.TryAuthorize(identity, "tenants/tenant-2/topics/orders", out _));
    }

    [Fact]
    public void DurableConfigurationRequiresEnabledStreamsAndSafeClassNames()
    {
        var disabled = new RealtimeStackOptionsValidator(Options.Create(new RedisOptions { StreamsEnabled = false }));
        var enabled = new RealtimeStackOptionsValidator(Options.Create(new RedisOptions { StreamsEnabled = true }));

        Assert.True(disabled.Validate(null, new RealtimeStackOptions()).Succeeded);
        Assert.False(disabled.Validate(null, new RealtimeStackOptions { DurableEventClasses = ["audit"] }).Succeeded);
        Assert.False(enabled.Validate(null, new RealtimeStackOptions { DurableEventClasses = ["order.created"] }).Succeeded);
        Assert.True(enabled.Validate(null, new RealtimeStackOptions { DurableEventClasses = ["order-created"] }).Succeeded);
        Assert.False(enabled.Validate(null, new RealtimeStackOptions { MaximumSubscriptions = 0 }).Succeeded);
        Assert.False(enabled.Validate(null, new RealtimeStackOptions { MaximumTrackedCorrelations = 0 }).Succeeded);
        Assert.False(enabled.Validate(null, new RealtimeStackOptions { SlowConsumerStrikeLimit = 0 }).Succeeded);
    }

    [Fact]
    public void SourceInstanceIsBoundedForLongMachineNames()
    {
        var source = RealtimeStackDispatcher.BuildSourceInstance(
            new string('s', 128),
            new string('m', 253));

        Assert.Equal(256, source.Length);
        Assert.StartsWith(new string('s', 128) + ":", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https", "gateway.example", null, "https://gateway.example", true)]
    [InlineData("https", "gateway.example", 8443, "https://gateway.example:8443", true)]
    [InlineData("https", "gateway.example", null, "https://evil.example", false)]
    [InlineData("https", "gateway.example", null, "http://gateway.example", false)]
    public void CookieOriginMustMatchRequestOrigin(
        string scheme,
        string host,
        int? port,
        string origin,
        bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = port.HasValue ? new HostString(host, port.Value) : new HostString(host);

        Assert.Equal(expected, RealtimeStackAuthenticator.IsSameOrigin(context.Request, origin));
    }

    [Fact]
    public async Task SessionReconnectIsRecordedFromExplicitClientContext()
    {
        using var metrics = new GatewayMetrics();
        var authenticator = new RealtimeStackAuthenticator(
            new FixedSessionResolver(),
            new UnusedTicketStore(),
            new RealtimeStackOptions { AllowedOrigins = ["https://gateway.example"] },
            metrics);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("gateway.example");
        context.Request.Headers.Origin = "https://gateway.example";
        context.Request.QueryString = new QueryString("?reconnect=true");

        var result = await authenticator.AuthenticateAsync(context, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("realtimestack_reconnect_authentications_total 1", metrics.RenderPrometheus(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionTicketPreservesTheOriginatingSessionIdentity()
    {
        using var metrics = new GatewayMetrics();
        var identity = Identity() with { SessionId = "session-ticket-1" };
        var authenticator = new RealtimeStackAuthenticator(
            new FixedSessionResolver(),
            new FixedTicketStore(identity),
            new RealtimeStackOptions(),
            metrics);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("gateway.example");
        context.Request.QueryString = new QueryString("?ticket=valid-ticket");

        var result = await authenticator.AuthenticateAsync(context, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("session-ticket-1", result.SessionId);
        Assert.Equal("session-ticket-1", result.Identity!.SessionId);
    }

    [Fact]
    public async Task ConnectionQueueIsBoundedAndTracksDuplicateCorrelations()
    {
        using var metrics = new GatewayMetrics();
        await using var connection = new RealtimeStackConnection(
            new OpenWebSocket(),
            Identity(),
            new RealtimeStackOptions { OutboundQueueCapacity = 1, SlowConsumerStrikeLimit = 1 },
            metrics);
        var message = RealtimeStackDispatcher.Error(null, ProtocolErrorCodes.InternalError, "temporary");

        Assert.True(connection.TryTrackCorrelation("correlation-1"));
        Assert.False(connection.TryTrackCorrelation("correlation-1"));
        Assert.True(connection.TryEnqueue(message));
        Assert.False(connection.TryEnqueue(message));
        Assert.True(connection.HasExceededSlowConsumerLimit);
        var rendered = metrics.RenderPrometheus();
        Assert.Contains("realtimestack_queue_dropped_total 1", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(connection.Identity.TenantId, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(connection.Identity.UserId, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingConnectionDoesNotReportQueueSaturation()
    {
        using var metrics = new GatewayMetrics();
        await using var connection = new RealtimeStackConnection(
            new OpenWebSocket(),
            Identity(),
            new RealtimeStackOptions { OutboundQueueCapacity = 1, SlowConsumerStrikeLimit = 1 },
            metrics);

        await connection.RequestCloseAsync(WebSocketCloseStatus.NormalClosure, "test_close", CancellationToken.None);

        Assert.False(connection.TryEnqueue(RealtimeStackDispatcher.Error(null, ProtocolErrorCodes.InternalError, "ignored")));
        Assert.Contains("realtimestack_queue_dropped_total 0", metrics.RenderPrometheus(), StringComparison.Ordinal);
        Assert.False(connection.HasExceededSlowConsumerLimit);
    }

    [Fact]
    public async Task FailedCloseFrameRecordsOnlyTheFailureOutcome()
    {
        using var metrics = new GatewayMetrics();
        await using var connection = new RealtimeStackConnection(
            new FailingCloseWebSocket(),
            Identity(),
            new RealtimeStackOptions(),
            metrics);

        await connection.RequestCloseAsync(WebSocketCloseStatus.NormalClosure, "test_close", CancellationToken.None);

        var rendered = metrics.RenderPrometheus();
        Assert.Contains("realtimestack_websocket_closes_total{code=\"1000\"} 0", rendered, StringComparison.Ordinal);
        Assert.Contains("realtimestack_websocket_closes_total{code=\"1011\"} 1", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistryDisconnectsSlowConsumerWithoutCrossTenantDelivery()
    {
        using var metrics = new GatewayMetrics();
        var socket = new OpenWebSocket();
        await using var connection = new RealtimeStackConnection(
            socket,
            Identity(),
            new RealtimeStackOptions { OutboundQueueCapacity = 1, SlowConsumerStrikeLimit = 1 },
            metrics);
        Assert.True(connection.TrySubscribe(new AuthorizedRoute("orders", null)));
        var registry = new RealtimeStackConnectionRegistry(metrics);
        Assert.True(registry.Add(connection));
        Assert.True(connection.TryEnqueue(RealtimeStackDispatcher.Error(null, ProtocolErrorCodes.InternalError, "fill")));

        await registry.DeliverAsync(
            new RealtimeStackBusMessage(
                "message-1",
                "tenant-1",
                null,
                "orders",
                "correlation-1",
                Now,
                Payload,
                "instance-a"),
            CancellationToken.None);

        await registry.DeliverAsync(
            new RealtimeStackBusMessage(
                "message-2",
                "tenant-1",
                null,
                "orders",
                "correlation-2",
                Now,
                Payload,
                "instance-a"),
            CancellationToken.None);

        Assert.Equal(RealtimeStackCloseStatus.SlowConsumer, socket.CloseStatus);
        var rendered = metrics.RenderPrometheus();
        Assert.Contains("realtimestack_websocket_closes_total{code=\"4008\"} 1", rendered, StringComparison.Ordinal);
        Assert.Contains("realtimestack_slow_consumer_disconnects_total 1", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StalledSlowConsumerDoesNotBlockOtherFanout()
    {
        using var metrics = new GatewayMetrics();
        var slowSocket = new SignalingWebSocket(blockSend: true);
        var fastSocket = new SignalingWebSocket(blockSend: false);
        var options = new RealtimeStackOptions { OutboundQueueCapacity = 1, SlowConsumerStrikeLimit = 1 };
        await using var slow = new RealtimeStackConnection(slowSocket, Identity(), options, metrics);
        await using var fast = new RealtimeStackConnection(fastSocket, Identity(), options, metrics);
        Assert.True(slow.TrySubscribe(new AuthorizedRoute("orders", null)));
        Assert.True(fast.TrySubscribe(new AuthorizedRoute("orders", null)));
        var registry = new RealtimeStackConnectionRegistry(metrics);
        Assert.True(registry.Add(slow));
        Assert.True(registry.Add(fast));
        using var senders = new CancellationTokenSource();
        Assert.True(slow.TryEnqueue(RealtimeStackDispatcher.Error(null, ProtocolErrorCodes.InternalError, "sending")));
        var slowSender = slow.RunSenderAsync(senders.Token);
        await slowSocket.SendEntered.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(slow.TryEnqueue(RealtimeStackDispatcher.Error(null, ProtocolErrorCodes.InternalError, "queued")));
        var fastSender = fast.RunSenderAsync(senders.Token);

        var delivery = registry.DeliverAsync(
            new RealtimeStackBusMessage(
                "message-2",
                "tenant-1",
                null,
                "orders",
                "correlation-2",
                Now,
                Payload,
                "instance-a"),
            CancellationToken.None).AsTask();

        await fastSocket.SendEntered.WaitAsync(TimeSpan.FromSeconds(2));
        slowSocket.ReleaseSend();
        await delivery.WaitAsync(TimeSpan.FromSeconds(2));
        await senders.CancelAsync();
        await IgnoreCancellationAsync(slowSender);
        await IgnoreCancellationAsync(fastSender);
    }

    [Fact]
    public async Task CanceledCloseCanBeRetried()
    {
        using var metrics = new GatewayMetrics();
        var socket = new OpenWebSocket();
        await using var connection = new RealtimeStackConnection(
            socket,
            Identity(),
            new RealtimeStackOptions(),
            metrics);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await connection.RequestCloseAsync(
                RealtimeStackCloseStatus.ServiceRestart,
                "cancelled_attempt",
                cancelled.Token));
        await connection.RequestCloseAsync(
            RealtimeStackCloseStatus.ServiceRestart,
            "retry",
            CancellationToken.None);

        Assert.Equal(RealtimeStackCloseStatus.ServiceRestart, socket.CloseStatus);
    }

    private static MessageEnvelope Envelope(
        string version = ProtocolVersions.Current,
        string type = ProtocolMessageTypes.Publish,
        string correlationId = "correlation-1") =>
        new(version, type, correlationId, Now, "topics/orders", Payload);

    private static RealtimeStackIdentity Identity() =>
        new("tenant-1", "user-1", ["orders"], DateTimeOffset.UtcNow.AddHours(1));

    private sealed class FixedSessionResolver : IRealtimeStackSessionResolver
    {
        public ValueTask<RealtimeStackSessionResolution?> ResolveAsync(HttpContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RealtimeStackSessionResolution?>(new("session-1", Identity()));

        public ValueTask<RealtimeStackIdentity?> RevalidateAsync(string sessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RealtimeStackIdentity?>(Identity());
    }

    private sealed class UnusedTicketStore : IConnectionTicketStore
    {
        public ValueTask<string> IssueAsync(
            RealtimeStackIdentity identity,
            string audience,
            TimeSpan lifetime,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<RealtimeStackIdentity?> ConsumeAsync(
            string ticket,
            string audience,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTicketStore(RealtimeStackIdentity identity) : IConnectionTicketStore
    {
        public ValueTask<string> IssueAsync(
            RealtimeStackIdentity identity,
            string audience,
            TimeSpan lifetime,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<RealtimeStackIdentity?> ConsumeAsync(
            string ticket,
            string audience,
            CancellationToken cancellationToken) => ValueTask.FromResult<RealtimeStackIdentity?>(identity);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Test WebSocket sender canceled as expected.");
        }
    }

    private class OpenWebSocket : WebSocket
    {
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;

        public override string? CloseStatusDescription => _closeStatusDescription;

        public override WebSocketState State => _state;

        public override string? SubProtocol => RealtimeStackWebSocketHandler.SubProtocol;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SignalingWebSocket(bool blockSend) : OpenWebSocket
    {
        private readonly TaskCompletionSource _sendEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSend = CreateRelease(blockSend);

        public Task SendEntered => _sendEntered.Task;

        public void ReleaseSend() => _releaseSend.TrySetResult();

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            _sendEntered.TrySetResult();
            await _releaseSend.Task.WaitAsync(cancellationToken);
        }

        private static TaskCompletionSource CreateRelease(bool block)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!block)
            {
                release.TrySetResult();
            }

            return release;
        }
    }

    private sealed class FailingCloseWebSocket : OpenWebSocket
    {
        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            throw new WebSocketException("Simulated close-frame failure.");
    }
}
