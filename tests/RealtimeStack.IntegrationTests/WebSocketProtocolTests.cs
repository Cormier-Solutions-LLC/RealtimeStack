using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RealtimeStack.Contracts;
using RealtimeStack.Gateway;
using RealtimeStack.Redis;

namespace RealtimeStack.IntegrationTests;

public sealed class WebSocketProtocolTests
{
    [Fact]
    public async Task HttpFallbackPollReturnsCorrelatedAcknowledgment()
    {
        await using var factory = new RealtimeStackFactory();
        using var client = factory.CreateClient();
        using var connect = await client.PostAsync(
            "/realtimestack/http/connect?ticket=valid-ticket",
            content: null,
            CancellationToken.None);
        connect.EnsureSuccessStatusCode();
        using var connection = await JsonDocument.ParseAsync(await connect.Content.ReadAsStreamAsync());
        var id = connection.RootElement.GetProperty("connectionId").GetString()!;
        var token = connection.RootElement.GetProperty("connectionToken").GetString()!;
        using var poll = new HttpRequestMessage(HttpMethod.Post, $"/realtimestack/http/connections/{id}/poll");
        poll.Headers.Add("X-RealtimeStack-Connection", token);
        var pollResponse = client.SendAsync(poll);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"/realtimestack/http/connections/{id}/messages");
        message.Headers.Add("X-RealtimeStack-Connection", token);
        message.Content = JsonContent.Create(Envelope(ProtocolMessageTypes.Ping, "http-correlation-1"),
            RealtimeStackJsonSerializerContext.Default.MessageEnvelope);
        using var accepted = await client.SendAsync(message);
        using var received = await pollResponse;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await received.Content.ReadFromJsonAsync(
            RealtimeStackJsonSerializerContext.Default.ServerMessageEnvelope,
            timeout.Token);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(ProtocolMessageTypes.Acknowledge, response?.Type);
        Assert.Equal("http-correlation-1", response?.CorrelationId);
        using var close = new HttpRequestMessage(HttpMethod.Delete, $"/realtimestack/http/connections/{id}");
        close.Headers.Add("X-RealtimeStack-Connection", token);
        using var closed = await client.SendAsync(close);
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
    }

    [Fact]
    public async Task HttpFallbackConnectionTokenIsRequired()
    {
        await using var factory = new RealtimeStackFactory();
        using var client = factory.CreateClient();
        using var connect = await client.PostAsync(
            "/realtimestack/http/connect?ticket=valid-ticket",
            content: null,
            CancellationToken.None);
        connect.EnsureSuccessStatusCode();
        using var connection = await JsonDocument.ParseAsync(await connect.Content.ReadAsStreamAsync());
        var id = connection.RootElement.GetProperty("connectionId").GetString()!;
        var token = connection.RootElement.GetProperty("connectionToken").GetString()!;

        using var response = await client.PostAsync(
            $"/realtimestack/http/connections/{id}/poll",
            content: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var close = new HttpRequestMessage(HttpMethod.Delete, $"/realtimestack/http/connections/{id}");
        close.Headers.Add("X-RealtimeStack-Connection", token);
        using var closed = await client.SendAsync(close);
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
    }

    [Fact]
    public async Task HttpFallbackRejectsOverlappingReceiversAndCommandsAfterClose()
    {
        await using var factory = new RealtimeStackFactory();
        using var client = factory.CreateClient();
        using var connect = await client.PostAsync(
            "/realtimestack/http/connect?ticket=valid-ticket",
            content: null,
            CancellationToken.None);
        connect.EnsureSuccessStatusCode();
        using var connection = await JsonDocument.ParseAsync(await connect.Content.ReadAsStreamAsync());
        var id = connection.RootElement.GetProperty("connectionId").GetString()!;
        var token = connection.RootElement.GetProperty("connectionToken").GetString()!;

        using var firstPoll = new HttpRequestMessage(HttpMethod.Post, $"/realtimestack/http/connections/{id}/poll");
        firstPoll.Headers.Add("X-RealtimeStack-Connection", token);
        var activePoll = client.SendAsync(firstPoll);
        await Task.Delay(100);

        using var secondPoll = new HttpRequestMessage(HttpMethod.Post, $"/realtimestack/http/connections/{id}/poll");
        secondPoll.Headers.Add("X-RealtimeStack-Connection", token);
        using var conflict = await client.SendAsync(secondPoll);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var close = new HttpRequestMessage(HttpMethod.Delete, $"/realtimestack/http/connections/{id}");
        close.Headers.Add("X-RealtimeStack-Connection", token);
        using var closed = await client.SendAsync(close);
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
        using var completedPoll = await activePoll;
        Assert.Equal(HttpStatusCode.Gone, completedPoll.StatusCode);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"/realtimestack/http/connections/{id}/messages");
        message.Headers.Add("X-RealtimeStack-Connection", token);
        message.Content = JsonContent.Create(Envelope(ProtocolMessageTypes.Ping, "after-close"),
            RealtimeStackJsonSerializerContext.Default.MessageEnvelope);
        using var rejected = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
    }

    [Fact]
    public async Task HttpFallbackDoesNotDispatchUploadThatFinishesAfterClose()
    {
        await using var factory = new RealtimeStackFactory();
        using var client = factory.CreateClient();
        using var connect = await client.PostAsync(
            "/realtimestack/http/connect?ticket=valid-ticket",
            content: null,
            CancellationToken.None);
        connect.EnsureSuccessStatusCode();
        using var connection = await JsonDocument.ParseAsync(await connect.Content.ReadAsStreamAsync());
        var id = connection.RootElement.GetProperty("connectionId").GetString()!;
        var token = connection.RootElement.GetProperty("connectionToken").GetString()!;
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            Envelope(ProtocolMessageTypes.Publish, "close-race"),
            RealtimeStackJsonSerializerContext.Default.MessageEnvelope);
        var releaseUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/realtimestack/http/connections/{id}/messages");
        message.Headers.Add("X-RealtimeStack-Connection", token);
        message.Content = new PausedUploadContent(payload, uploadPaused, releaseUpload.Task);
        var pendingMessage = client.SendAsync(message);
        await uploadPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            using var close = new HttpRequestMessage(HttpMethod.Delete, $"/realtimestack/http/connections/{id}");
            close.Headers.Add("X-RealtimeStack-Connection", token);
            using var closed = await client.SendAsync(close);
            Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
        }
        finally
        {
            releaseUpload.TrySetResult();
        }

        using var rejected = await pendingMessage;
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        Assert.Empty(factory.Bus.Published);
    }

    [Fact]
    public async Task HttpFallbackPendingSendDoesNotBlockRegistryClose()
    {
        await using var factory = new RealtimeStackFactory();
        using var client = factory.CreateClient();
        using var connect = await client.PostAsync(
            "/realtimestack/http/connect?ticket=valid-ticket",
            content: null,
            CancellationToken.None);
        connect.EnsureSuccessStatusCode();
        using var connection = await JsonDocument.ParseAsync(await connect.Content.ReadAsStreamAsync());
        var id = connection.RootElement.GetProperty("connectionId").GetString()!;
        var token = connection.RootElement.GetProperty("connectionToken").GetString()!;
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/realtimestack/http/connections/{id}/messages");
        message.Headers.Add("X-RealtimeStack-Connection", token);
        message.Content = JsonContent.Create(Envelope(ProtocolMessageTypes.Ping, "pending-close"),
            RealtimeStackJsonSerializerContext.Default.MessageEnvelope);
        using var accepted = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await Task.Delay(100);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await factory.Services.GetRequiredService<RealtimeStackConnectionRegistry>().CloseAllAsync(timeout.Token);
    }

    [Fact]
    public async Task AuthorizedPublishReturnsCorrelatedAcknowledgment()
    {
        await using var factory = new RealtimeStackFactory();
        Assert.Equal(1024, factory.Services.GetRequiredService<RealtimeStackOptions>().MaximumFrameBytes);
        using var socket = await ConnectAsync(factory);
        var command = Envelope(ProtocolMessageTypes.Publish, "correlation-1");

        await SendAsync(socket, command);
        var response = await ReceiveEnvelopeAsync(socket);

        Assert.Equal(ProtocolMessageTypes.Acknowledge, response.Type);
        Assert.Equal(command.CorrelationId, response.CorrelationId);
        Assert.Single(factory.Bus.Published);
        Assert.Equal("tenant-1", factory.Bus.Published.Single().TenantId);
    }

    [Fact]
    public async Task MalformedUnsupportedAndDuplicateFramesReturnStructuredErrors()
    {
        await using var factory = new RealtimeStackFactory();
        using var socket = await ConnectAsync(factory);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{not-json"), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal(ProtocolErrorCodes.InvalidEnvelope, (await ReceiveEnvelopeAsync(socket)).Error?.Code);

        await SendAsync(socket, Envelope("unknown", "correlation-2"));
        Assert.Equal(ProtocolErrorCodes.UnsupportedType, (await ReceiveEnvelopeAsync(socket)).Error?.Code);

        var valid = Envelope(ProtocolMessageTypes.Ping, "correlation-3");
        await SendAsync(socket, valid);
        Assert.Equal(ProtocolMessageTypes.Acknowledge, (await ReceiveEnvelopeAsync(socket)).Type);
        await SendAsync(socket, valid);
        Assert.Equal(ProtocolErrorCodes.DuplicateCorrelation, (await ReceiveEnvelopeAsync(socket)).Error?.Code);
    }

    [Fact]
    public async Task FragmentedAndOversizedFramesAreClosedWithExplicitStatus()
    {
        await using var factory = new RealtimeStackFactory();
        Assert.Equal(1024, factory.Services.GetRequiredService<RealtimeStackOptions>().MaximumFrameBytes);
        using (var fragmented = await ConnectAsync(factory))
        {
            await fragmented.SendAsync(
                Encoding.UTF8.GetBytes("{}"),
                WebSocketMessageType.Text,
                endOfMessage: false,
                CancellationToken.None);
            await WaitForCloseAsync(fragmented);
            Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, fragmented.CloseStatus);
        }

        await using var oversizedFactory = new RealtimeStackFactory();
        using var oversized = await ConnectAsync(oversizedFactory);
        await oversized.SendAsync(
            new byte[1025],
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
        await WaitForCloseAsync(oversized);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, oversized.CloseStatus);
    }

    [Fact]
    public async Task MissingCookieAndCrossOriginCookieAreRejected()
    {
        await using var factory = new RealtimeStackFactory();

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(factory, includeCookie: false, useTicket: false));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(factory, origin: "https://evil.example", useTicket: false));
    }

    [Fact]
    public async Task ExistingTicketCannotMintReplacementTicket()
    {
        await using var factory = new RealtimeStackFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/realtimestack/tickets?ticket=valid-ticket",
            content: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConfiguredEndpointPathsReplaceStandaloneDefaults()
    {
        await using var factory = new RealtimeStackFactory(
            endpointPath: "/custom/socket",
            ticketEndpointPath: "/custom/tickets");
        using var socket = await ConnectAsync(factory, endpointPath: "/custom/socket");
        using var client = factory.CreateClient();
        using var ticketRequest = new HttpRequestMessage(HttpMethod.Post, "/custom/tickets");
        ticketRequest.Headers.Add("Origin", "http://localhost");
        ticketRequest.Headers.Add("Cookie", "realtimestack_session=valid-session-123456");
        using var ticketResponse = await client.SendAsync(ticketRequest, CancellationToken.None);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync(
            RealtimeStackJsonSerializerContext.Default.ConnectionTicketResponse,
            CancellationToken.None);

        using var oldSocketEndpoint = await client.GetAsync("/realtimestack/ws", CancellationToken.None);
        using var oldTicketEndpoint = await client.PostAsync("/realtimestack/tickets", null, CancellationToken.None);

        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.Equal(HttpStatusCode.OK, ticketResponse.StatusCode);
        Assert.Equal("unused-ticket", ticket?.Ticket);
        Assert.Equal(HttpStatusCode.NotFound, oldSocketEndpoint.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, oldTicketEndpoint.StatusCode);
    }

    [Fact]
    public async Task TrustedForwardedHeadersSupportTlsTerminatingIngress()
    {
        await using var factory = new RealtimeStackFactory();
        using var socket = await ConnectAsync(
            factory,
            origin: "https://gateway.example",
            useTicket: false,
            forwardedHost: "gateway.example");

        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task DrainStartingDuringAuthenticationRejectsUpgrade()
    {
        await using var factory = new RealtimeStackFactory(authenticationDelay: TimeSpan.FromMilliseconds(250));
        var connection = ConnectAsync(factory);
        await Task.Delay(50);
        factory.Services.GetRequiredService<RealtimeStackConnectionRegistry>().BeginDrain();
        factory.Services.GetRequiredService<GatewayState>().BeginDrain();

        await Assert.ThrowsAnyAsync<Exception>(() => connection);
    }

    [Fact]
    public async Task IdleConnectionClosesWithHeartbeatTimeout()
    {
        await using var factory = new RealtimeStackFactory();
        using var socket = await ConnectAsync(factory);

        await WaitForCloseAsync(socket);

        Assert.Equal(RealtimeStackCloseStatus.HeartbeatTimeout, socket.CloseStatus);
        var registry = factory.Services.GetRequiredService<RealtimeStackConnectionRegistry>();
        for (var attempt = 0; attempt < 50 && registry.Count != 0; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ExpiredIdentityCannotPublishAndIsClosed()
    {
        await using var factory = new RealtimeStackFactory(TimeSpan.FromMilliseconds(250));
        using var socket = await ConnectAsync(factory);
        await Task.Delay(300);

        await SendAsync(socket, Envelope(ProtocolMessageTypes.Publish, "expired-command"));
        await WaitForCloseAsync(socket);

        Assert.Equal(RealtimeStackCloseStatus.AuthenticationExpired, socket.CloseStatus);
        Assert.Empty(factory.Bus.Published);
    }

    [Fact]
    public async Task RevokedSessionCannotPublishAndIsClosed()
    {
        await using var factory = new RealtimeStackFactory();
        using var socket = await ConnectAsync(
            factory,
            origin: "https://gateway.example",
            useTicket: false,
            forwardedHost: "gateway.example");
        factory.SessionStore.Revoked = true;

        await SendAsync(socket, Envelope(ProtocolMessageTypes.Publish, "revoked-command"));
        await WaitForCloseAsync(socket);

        Assert.Equal(RealtimeStackCloseStatus.AuthenticationExpired, socket.CloseStatus);
        Assert.Empty(factory.Bus.Published);
    }

    [Fact]
    public async Task RevokedSessionCannotReceiveSubscribedDeliveryAndIsClosed()
    {
        await using var factory = new RealtimeStackFactory();
        using var socket = await ConnectAsync(
            factory,
            origin: "https://gateway.example",
            useTicket: false,
            forwardedHost: "gateway.example");
        await SendAsync(socket, Envelope(ProtocolMessageTypes.Subscribe, "subscribe-before-revocation"));
        Assert.Equal(ProtocolMessageTypes.Acknowledge, (await ReceiveEnvelopeAsync(socket)).Type);
        factory.SessionStore.Revoked = true;

        await factory.Services.GetRequiredService<RealtimeStackConnectionRegistry>().DeliverAsync(
            new RealtimeStackBusMessage(
                "revoked-delivery",
                "tenant-1",
                null,
                "orders",
                "revoked-delivery-correlation",
                DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { value = 42 }),
                "integration-test"),
            CancellationToken.None);
        await WaitForCloseAsync(socket);

        Assert.Equal(RealtimeStackCloseStatus.AuthenticationExpired, socket.CloseStatus);
    }

    [Fact]
    public async Task UnrelatedTenantDeliveryDoesNotRevalidateSession()
    {
        await using var factory = new RealtimeStackFactory();
        using var socket = await ConnectAsync(
            factory,
            origin: "https://gateway.example",
            useTicket: false,
            forwardedHost: "gateway.example");
        await SendAsync(socket, Envelope(ProtocolMessageTypes.Subscribe, "subscribe-before-other-tenant"));
        Assert.Equal(ProtocolMessageTypes.Acknowledge, (await ReceiveEnvelopeAsync(socket)).Type);
        var validationCount = factory.SessionStore.ValidationCount;

        await factory.Services.GetRequiredService<RealtimeStackConnectionRegistry>().DeliverAsync(
            new RealtimeStackBusMessage(
                "other-tenant-delivery",
                "tenant-2",
                null,
                "orders",
                "other-tenant-correlation",
                DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { value = 42 }),
                "integration-test"),
            CancellationToken.None);

        Assert.Equal(validationCount, factory.SessionStore.ValidationCount);
    }

    [Fact]
    public async Task MalformedTrafficDoesNotRefreshIdleActivity()
    {
        await using var factory = new RealtimeStackFactory();
        using var socket = await ConnectAsync(factory);
        using var stopSending = new CancellationTokenSource();
        var sender = Task.Run(async () =>
        {
            try
            {
                while (!stopSending.IsCancellationRequested)
                {
                    await socket.SendAsync(
                        Encoding.UTF8.GetBytes("{bad-json"),
                        WebSocketMessageType.Text,
                        true,
                        stopSending.Token);
                    await Task.Delay(250, stopSending.Token);
                }
            }
            catch (Exception exception) when (stopSending.IsCancellationRequested || socket.State != WebSocketState.Open)
            {
                System.Diagnostics.Debug.WriteLine($"Malformed-traffic sender stopped: {exception.Message}");
            }
        });

        await WaitForCloseAsync(socket);
        await stopSending.CancelAsync();
        await sender;

        Assert.Equal(RealtimeStackCloseStatus.HeartbeatTimeout, socket.CloseStatus);
    }

    [Fact]
    public async Task RestartGuidanceAndAbruptDisconnectCleanUpLocalRegistry()
    {
        await using var factory = new RealtimeStackFactory();
        var registry = factory.Services.GetRequiredService<RealtimeStackConnectionRegistry>();
        using (var drainingSocket = await ConnectAsync(factory))
        {
            await WaitForRegistryCountAsync(registry, 1);
            Assert.Equal(1, registry.Count);
            await registry.NotifyServiceRestartAsync();
            var restart = await ReceiveEnvelopeAsync(drainingSocket);
            Assert.Equal(ProtocolMessageTypes.ServiceRestart, restart.Type);
            Assert.True(restart.Reconnect?.Reauthenticate);
            await registry.CloseAllAsync(CancellationToken.None);
            await WaitForCloseAsync(drainingSocket);
            Assert.Equal(RealtimeStackCloseStatus.ServiceRestart, drainingSocket.CloseStatus);
            await drainingSocket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "restart_received",
                CancellationToken.None);
            for (var attempt = 0; attempt < 50 && registry.Count != 0; attempt++)
            {
                await Task.Delay(20);
            }
        }

        using var abruptSocket = await ConnectAsync(factory);
        await WaitForRegistryCountAsync(registry, 1);
        Assert.Equal(1, registry.Count);
        abruptSocket.Abort();
        for (var attempt = 0; attempt < 50 && registry.Count != 0; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(0, registry.Count);
    }

    private static async Task WaitForRegistryCountAsync(RealtimeStackConnectionRegistry registry, int expected)
    {
        for (var attempt = 0; attempt < 50 && registry.Count != expected; attempt++)
        {
            await Task.Delay(20);
        }
    }

    private static async Task<WebSocket> ConnectAsync(
        RealtimeStackFactory factory,
        bool includeCookie = true,
        string origin = "http://localhost",
        bool useTicket = true,
        string? forwardedHost = null,
        string endpointPath = "/realtimestack/ws")
    {
        var client = factory.Server.CreateWebSocketClient();
        client.SubProtocols.Add(RealtimeStackWebSocketHandler.SubProtocol);
        client.ConfigureRequest = request =>
        {
            request.Headers.Origin = origin;
            if (forwardedHost is not null)
            {
                request.Headers.Append("X-Forwarded-For", "127.0.0.2");
                request.Headers.Append("X-Forwarded-Proto", "https");
                request.Headers.Append("X-Forwarded-Host", forwardedHost);
            }
            if (includeCookie)
            {
                request.Headers.Append("Cookie", "realtimestack_session=valid-session-123456");
            }
        };
        var path = useTicket ? $"{endpointPath}?ticket=valid-ticket" : endpointPath;
        return await client.ConnectAsync(new Uri($"ws://localhost{path}"), CancellationToken.None);
    }

    private static MessageEnvelope Envelope(string type, string correlationId) => new(
        ProtocolVersions.Current,
        type,
        correlationId,
        DateTimeOffset.UtcNow,
        "topics/orders",
        JsonSerializer.SerializeToElement(new { value = 42 }));

    private static async Task SendAsync(WebSocket socket, MessageEnvelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, RealtimeStackJsonSerializerContext.Default.MessageEnvelope);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<ServerMessageEnvelope> ReceiveEnvelopeAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonSerializer.Deserialize(
            buffer.AsSpan(0, result.Count),
            RealtimeStackJsonSerializerContext.Default.ServerMessageEnvelope)!;
    }

    private static async Task WaitForCloseAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    private sealed class RealtimeStackFactory(
        TimeSpan? identityLifetime = null,
        TimeSpan? authenticationDelay = null,
        string endpointPath = "/realtimestack/ws",
        string ticketEndpointPath = "/realtimestack/tickets") : WebApplicationFactory<Program>
    {
        public FakeMessageBus Bus { get; } = new();

        public FakeSessionStore SessionStore { get; } = new();

        private TimeSpan IdentityLifetime { get; } = identityLifetime ?? TimeSpan.FromMinutes(5);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["RealtimeStack:AllowedOrigins:0"] = "http://localhost",
                    ["RealtimeStack:AllowedOrigins:1"] = "https://gateway.example",
                    ["RealtimeStack:EndpointPath"] = endpointPath,
                    ["RealtimeStack:TicketEndpointPath"] = ticketEndpointPath,
                    ["RealtimeStack:MaximumFrameBytes"] = "1024",
                    ["RealtimeStack:MaximumMessageBytes"] = "1024",
                    ["Proxy:TrustedNetworks:0"] = "127.0.0.0/8",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOptions<RealtimeStackOptions>>();
                services.RemoveAll<IRealtimeStackSessionStore>();
                services.RemoveAll<IConnectionTicketStore>();
                services.RemoveAll<IRealtimeStackMessageBus>();
                services.RemoveAll<IDurableRealtimeStackStore>();
                services.AddSingleton<IOptions<RealtimeStackOptions>>(Options.Create(new RealtimeStackOptions
                {
                    AllowedOrigins = ["http://localhost", "https://gateway.example"],
                    EndpointPath = endpointPath,
                    TicketEndpointPath = ticketEndpointPath,
                    MaximumFrameBytes = 1024,
                    MaximumMessageBytes = 1024,
                    HeartbeatSeconds = 1,
                    IdleTimeoutSeconds = 2,
                }));
                services.AddSingleton<IRealtimeStackSessionStore>(SessionStore);
                services.AddSingleton<IConnectionTicketStore>(new FakeTicketStore(
                    IdentityLifetime,
                    authenticationDelay ?? TimeSpan.Zero));
                services.AddSingleton<IRealtimeStackMessageBus>(Bus);
                services.AddSingleton<IDurableRealtimeStackStore, FakeDurableStore>();
            });
        }
    }

    public sealed class FakeSessionStore : IRealtimeStackSessionStore
    {
        private int _validationCount;

        public bool Revoked { get; set; }

        public int ValidationCount => Volatile.Read(ref _validationCount);

        public ValueTask<RealtimeStackIdentity?> ValidateAsync(string sessionId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _validationCount);
            return ValueTask.FromResult<RealtimeStackIdentity?>(sessionId == "valid-session-123456" && !Revoked
                ? new RealtimeStackIdentity("tenant-1", "user-1", ["orders"], DateTimeOffset.UtcNow.AddMinutes(5))
                : null);
        }
    }

    private sealed class FakeTicketStore(
        TimeSpan identityLifetime,
        TimeSpan authenticationDelay) : IConnectionTicketStore
    {
        public ValueTask<string> IssueAsync(RealtimeStackIdentity identity, string audience, TimeSpan lifetime, CancellationToken cancellationToken) =>
            ValueTask.FromResult("unused-ticket");

        public async ValueTask<RealtimeStackIdentity?> ConsumeAsync(
            string ticket,
            string audience,
            CancellationToken cancellationToken)
        {
            if (authenticationDelay > TimeSpan.Zero)
            {
                await Task.Delay(authenticationDelay, cancellationToken);
            }

            return ticket == "valid-ticket"
                ? new RealtimeStackIdentity("tenant-1", "user-1", ["orders"], DateTimeOffset.UtcNow.Add(identityLifetime))
                : null;
        }
    }

    private sealed class PausedUploadContent(
        byte[] payload,
        TaskCompletionSource uploadPaused,
        Task releaseUpload) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(payload.AsMemory(0, 1));
            await stream.FlushAsync();
            uploadPaused.TrySetResult();
            await releaseUpload;
            await stream.WriteAsync(payload.AsMemory(1));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }
    }

    private sealed class FakeMessageBus : IRealtimeStackMessageBus
    {
        public ConcurrentQueue<RealtimeStackBusMessage> Published { get; } = new();

        public ValueTask PublishAsync(RealtimeStackBusMessage message, CancellationToken cancellationToken)
        {
            Published.Enqueue(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(Func<RealtimeStackBusMessage, ValueTask> handler, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable>(new EmptySubscription());

        private sealed class EmptySubscription : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDurableStore : IDurableRealtimeStackStore
    {
        public ValueTask<string> AppendAsync(DurableStreamMessage message, CancellationToken cancellationToken) =>
            ValueTask.FromResult("1-0");

        public ValueTask<IReadOnlyList<DurableDelivery>> ReadAsync(string eventClass, string group, string consumer, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<DurableDelivery>>([]);

        public ValueTask<IReadOnlyList<DurableDelivery>> RecoverPendingAsync(string eventClass, string group, string consumer, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<DurableDelivery>>([]);

        public ValueTask<bool> TryMarkCompletedAsync(string eventClass, string messageId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask AcknowledgeAsync(string eventClass, string group, string entryId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
