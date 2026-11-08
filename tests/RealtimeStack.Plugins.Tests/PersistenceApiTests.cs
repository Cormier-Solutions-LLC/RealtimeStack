using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RealtimeStack.Config;
using RealtimeStack.Contracts;
using RealtimeStack.Gateway;
using RealtimeStack.Plugins.Persistence;
using RealtimeStack.Plugins.Sqlite;
using RealtimeStack.Redis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace RealtimeStack.Plugins.Tests;

public sealed class PersistenceApiTests
{
    private const string WorkerToken = "test-only-worker-token-not-a-production-secret";

    [Fact]
    public async Task WorkerWritesAreSeparateFromSessionScopedReadsAndAcknowledgments()
    {
        var file = Path.Join(Path.GetTempPath(), $"realtimestack-api-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqlitePersistenceStore(() => new SqliteConnection($"Data Source={file};Pooling=False"));
            await using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = store.CreateTableSql;
                await command.ExecuteNonQueryAsync();
            }
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RealtimeStackSetup:Persistence:ApiEnabled"] = "true",
                ["RealtimeStackSetup:Persistence:WorkerToken"] = WorkerToken
            }).Build();
            var options = new RealtimeStackOptions { AllowedOrigins = ["http://localhost"] };
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(new RealtimeStackSetupOptions { GatewayEnabled = true, PersistenceEnabled = true });
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<IRealtimeStackPersistenceStore>(store);
            builder.Services.AddSingleton(new DurableTasks(store));
            builder.Services.AddSingleton(new RealtimeStackAuthenticator(new Sessions(), new Tickets(), options, new GatewayMetrics()));
            await using var app = builder.Build();
            app.MapRealtimeStackPersistence(config);
            await app.StartAsync();
            using var client = app.GetTestClient();

            var append = new { tenantId = "tenant", ownerId = "alice", streamId = "inbox", kind = 1,
                record = new { sequence = 1, payload = "finished", createdAt = DateTimeOffset.UtcNow } };
            using var denied = await client.PostAsJsonAsync("/realtimestack/persistence/worker/append", append);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", WorkerToken);
            using var written = await client.PostAsJsonAsync("/realtimestack/persistence/worker/append", append);
            Assert.Equal(HttpStatusCode.NoContent, written.StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            using var noSession = await client.PostAsJsonAsync("/realtimestack/persistence/records/read", new { streamId = "inbox", kind = 1 });
            Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);

            client.DefaultRequestHeaders.Add("Origin", "http://localhost");
            client.DefaultRequestHeaders.Add("Cookie", "alice");
            using var read = await client.PostAsJsonAsync("/realtimestack/persistence/records/read", new { streamId = "inbox", kind = 1, ownerId = "bob", tenantId = "forged" });
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var records = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Equal("finished", records.RootElement[0].GetProperty("payload").GetString());
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", "bob");
            using var other = await client.PostAsJsonAsync("/realtimestack/persistence/records/read", new { streamId = "inbox", kind = 1 });
            Assert.Equal("[]", await other.Content.ReadAsStringAsync());
            using var ackOther = await client.PostAsJsonAsync("/realtimestack/persistence/records/ack", new { streamId = "inbox", kind = 1, throughSequence = 1 });
            Assert.Equal(HttpStatusCode.NoContent, ackOther.StatusCode);
            Assert.Single(await store.ReadAsync(new("tenant", "alice", "inbox", DurableRecordKind.Notification)));

            client.DefaultRequestHeaders.Authorization = new("Bearer", WorkerToken);
            using var taskWrite = await client.PostAsJsonAsync("/realtimestack/persistence/worker/tasks", new
            {
                tenantId = "tenant", ownerId = "alice", sequence = 1,
                status = new { taskId = "import-1", state = 3, error = "Invalid row", finishedAt = DateTimeOffset.UtcNow }
            });
            Assert.Equal(HttpStatusCode.NoContent, taskWrite.StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            using var hiddenTask = await client.PostAsJsonAsync("/realtimestack/persistence/tasks/read", new { taskId = "import-1" });
            Assert.Equal(HttpStatusCode.NotFound, hiddenTask.StatusCode);
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", "alice");
            using var taskRead = await client.PostAsJsonAsync("/realtimestack/persistence/tasks/read", new { taskId = "import-1" });
            using var status = JsonDocument.Parse(await taskRead.Content.ReadAsStringAsync());
            Assert.Equal("Invalid row", status.RootElement.GetProperty("error").GetString());
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Add("Origin", "https://cross-origin.example");
            using var crossOrigin = await client.PostAsJsonAsync("/realtimestack/persistence/records/ack", new { streamId = "inbox", kind = 1, throughSequence = 1 });
            Assert.Equal(HttpStatusCode.Unauthorized, crossOrigin.StatusCode);
            Assert.Single(await store.ReadAsync(new("tenant", "alice", "inbox", DurableRecordKind.Notification)));
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Add("Origin", "http://localhost");
            var bulkStream = new DurableStreamKey("tenant", "alice", "bulk", DurableRecordKind.Notification);
            for (var sequence = 1; sequence <= 100; sequence++)
                await store.AppendAsync(bulkStream, new(sequence, new string('x', 1024), DateTimeOffset.UtcNow));
            using var firstPage = await client.PostAsJsonAsync("/realtimestack/persistence/records/read", new { streamId = "bulk", kind = 1, limit = 100 });
            var firstBytes = await firstPage.Content.ReadAsByteArrayAsync();
            Assert.True(firstBytes.Length <= 65536);
            using var firstRows = JsonDocument.Parse(firstBytes);
            var firstCount = firstRows.RootElement.GetArrayLength();
            Assert.InRange(firstCount, 1, 99);
            var cursor = firstRows.RootElement[firstCount - 1].GetProperty("sequence").GetInt64();
            using var nextPage = await client.PostAsJsonAsync("/realtimestack/persistence/records/read", new { streamId = "bulk", kind = 1, afterSequence = cursor, limit = 100 });
            using var nextRows = JsonDocument.Parse(await nextPage.Content.ReadAsStringAsync());
            Assert.Equal(100, firstCount + nextRows.RootElement.GetArrayLength());
        }
        finally { File.Delete(file); }
    }

    private sealed class Sessions : IRealtimeStackSessionResolver
    {
        public ValueTask<RealtimeStackSessionResolution?> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
        {
            var owner = context.Request.Headers.Cookie.ToString();
            return ValueTask.FromResult<RealtimeStackSessionResolution?>(owner is "alice" or "bob"
                ? new("session", new RealtimeStackIdentity("tenant", owner, [], DateTimeOffset.UtcNow.AddHours(1))) : null);
        }
        public ValueTask<RealtimeStackIdentity?> RevalidateAsync(string sessionId, CancellationToken cancellationToken) => ValueTask.FromResult<RealtimeStackIdentity?>(null);
    }
    private sealed class Tickets : IConnectionTicketStore
    {
        public ValueTask<string> IssueAsync(RealtimeStackIdentity identity, string audience, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<RealtimeStackIdentity?> ConsumeAsync(string ticket, string audience, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
