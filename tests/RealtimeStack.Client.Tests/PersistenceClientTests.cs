using System.Net;
using System.Text;
using System.Text.Json;
using RealtimeStack.Client;

namespace RealtimeStack.Client.Tests;

public sealed class PersistenceClientTests
{
    [Fact]
    public async Task PortableClientUsesScopedPostAndDistinguishesMissingTasks()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new RealtimeStackPersistenceClient(http);
        var records = await client.ReadAsync("inbox", RealtimeStackInboxKind.Notification, 4);
        Assert.Equal(5, Assert.Single(records).Sequence);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1, body.RootElement.GetProperty("kind").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("tenantId", out _));
        Assert.Equal("/realtimestack/persistence/records/read", handler.Path);
        await client.AcknowledgeAsync("inbox", RealtimeStackInboxKind.Notification, 5);
        Assert.Null(await client.GetTaskStatusAsync("missing"));
        handler.Status = HttpStatusCode.Unauthorized;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTaskStatusAsync("missing"));
    }

    [Fact]
    public async Task PortableClientRejectsUnsafeInputsWithoutCallingTransport()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        Assert.Throws<ArgumentException>(() => new RealtimeStackPersistenceClient(http, "//example.test"));
        var client = new RealtimeStackPersistenceClient(http);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ReadAsync("inbox", RealtimeStackInboxKind.Message, long.MaxValue));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ReadAsync("inbox", RealtimeStackInboxKind.Message, limit: 1001));
        Assert.Null(handler.Path);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Equal(HttpMethod.Post, request.Method);
            var status = Status == HttpStatusCode.OK && Path.EndsWith("tasks/read", StringComparison.Ordinal) ? HttpStatusCode.NotFound : Status;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(Path.EndsWith("records/read", StringComparison.Ordinal)
                    ? "[{\"sequence\":5,\"payload\":\"saved\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"expiresAt\":null}]" : "", Encoding.UTF8, "application/json")
            };
        }
    }
}
