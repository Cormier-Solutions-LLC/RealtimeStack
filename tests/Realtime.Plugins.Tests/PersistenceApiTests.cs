using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cormier.Realtime.Config;
using Cormier.Realtime.Contracts;
using Cormier.Realtime.Gateway;
using Cormier.Realtime.Plugins.Persistence;
using Cormier.Realtime.Plugins.Sqlite;
using Cormier.Realtime.Redis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cormier.Realtime.Plugins.Tests;

public sealed class PersistenceApiTests
{
    private const string WorkerToken = "test-only-worker-token-not-a-production-secret";

    [Fact]
    public async Task WorkerWritesAreSeparateFromSessionScopedReadsAndAcknowledgments()
    {
        var file = Path.Join(Path.GetTempPath(), $"realtime-api-{Guid.NewGuid():N}.db");
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
                ["RealtimeSetup:Persistence:ApiEnabled"] = "true",
                ["RealtimeSetup:Persistence:WorkerToken"] = WorkerToken
            }).Build();
            var options = new RealtimeOptions { AllowedOrigins = ["http://localhost"] };
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(new RealtimeSetupOptions { GatewayEnabled = true, PersistenceEnabled = true });
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<IRealtimePersistenceStore>(store);
            builder.Services.AddSingleton(new DurableTasks(store));
            builder.Services.AddSingleton(new RealtimeAuthenticator(new Sessions(), new Tickets(), options, new GatewayMetrics()));
            await using var app = builder.Build();
            app.MapRealtimePersistence(config);
            await app.StartAsync();
            using var client = app.GetTestClient();

            var append = new { tenantId = "tenant", ownerId = "alice", streamId = "inbox", kind = 1,
                record = new { sequence = 1, payload = "finished", createdAt = DateTimeOffset.UtcNow } };
            using var denied = await client.PostAsJsonAsync("/realtime/persistence/worker/append", append);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", WorkerToken);
            using var written = await client.PostAsJsonAsync("/realtime/persistence/worker/append", append);
            Assert.Equal(HttpStatusCode.NoContent, written.StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            using var noSession = await client.PostAsJsonAsync("/realtime/persistence/records/read", new { streamId = "inbox", kind = 1 });
            Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);

            client.DefaultRequestHeaders.Add("Origin", "http://localhost");
            client.DefaultRequestHeaders.Add("Cookie", "alice");
            using var read = await client.PostAsJsonAsync("/realtime/persistence/records/read", new { streamId = "inbox", kind = 1, ownerId = "bob", tenantId = "forged" });
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var records = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Equal("finished", records.RootElement[0].GetProperty("payload").GetString());
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", "bob");
            using var other = await client.PostAsJsonAsync("/realtime/persistence/records/read", new { streamId = "inbox", kind = 1 });
            Assert.Equal("[]", await other.Content.ReadAsStringAsync());
            using var ackOther = await client.PostAsJsonAsync("/realtime/persistence/records/ack", new { streamId = "inbox", kind = 1, throughSequence = 1 });
            Assert.Equal(HttpStatusCode.NoContent, ackOther.StatusCode);
            Assert.Single(await store.ReadAsync(new("tenant", "alice", "inbox", DurableRecordKind.Notification)));

            client.DefaultRequestHeaders.Authorization = new("Bearer", WorkerToken);
            using var taskWrite = await client.PostAsJsonAsync("/realtime/persistence/worker/tasks", new
            {
                tenantId = "tenant", ownerId = "alice", sequence = 1,
                status = new { taskId = "import-1", state = 3, error = "Invalid row", finishedAt = DateTimeOffset.UtcNow }
            });
            Assert.Equal(HttpStatusCode.NoContent, taskWrite.StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            using var hiddenTask = await client.PostAsJsonAsync("/realtime/persistence/tasks/read", new { taskId = "import-1" });
            Assert.Equal(HttpStatusCode.NotFound, hiddenTask.StatusCode);
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", "alice");
            using var taskRead = await client.PostAsJsonAsync("/realtime/persistence/tasks/read", new { taskId = "import-1" });
            using var status = JsonDocument.Parse(await taskRead.Content.ReadAsStringAsync());
            Assert.Equal("Invalid row", status.RootElement.GetProperty("error").GetString());
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Add("Origin", "https://cross-origin.example");
            using var crossOrigin = await client.PostAsJsonAsync("/realtime/persistence/records/ack", new { streamId = "inbox", kind = 1, throughSequence = 1 });
            Assert.Equal(HttpStatusCode.Unauthorized, crossOrigin.StatusCode);
            Assert.Single(await store.ReadAsync(new("tenant", "alice", "inbox", DurableRecordKind.Notification)));
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Add("Origin", "http://localhost");
            var bulkStream = new DurableStreamKey("tenant", "alice", "bulk", DurableRecordKind.Notification);
            for (var sequence = 1; sequence <= 100; sequence++)
                await store.AppendAsync(bulkStream, new(sequence, new string('x', 1024), DateTimeOffset.UtcNow));
            using var firstPage = await client.PostAsJsonAsync("/realtime/persistence/records/read", new { streamId = "bulk", kind = 1, limit = 100 });
            var firstBytes = await firstPage.Content.ReadAsByteArrayAsync();
            Assert.True(firstBytes.Length <= 65536);
            using var firstRows = JsonDocument.Parse(firstBytes);
            var firstCount = firstRows.RootElement.GetArrayLength();
            Assert.InRange(firstCount, 1, 99);
            var cursor = firstRows.RootElement[firstCount - 1].GetProperty("sequence").GetInt64();
            using var nextPage = await client.PostAsJsonAsync("/realtime/persistence/records/read", new { streamId = "bulk", kind = 1, afterSequence = cursor, limit = 100 });
            using var nextRows = JsonDocument.Parse(await nextPage.Content.ReadAsStringAsync());
            Assert.Equal(100, firstCount + nextRows.RootElement.GetArrayLength());
        }
        finally { File.Delete(file); }
    }

    private sealed class Sessions : IRealtimeSessionResolver
    {
        public ValueTask<RealtimeSessionResolution?> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
        {
            var owner = context.Request.Headers.Cookie.ToString();
            return ValueTask.FromResult<RealtimeSessionResolution?>(owner is "alice" or "bob"
                ? new("session", new RealtimeIdentity("tenant", owner, [], DateTimeOffset.UtcNow.AddHours(1))) : null);
        }
        public ValueTask<RealtimeIdentity?> RevalidateAsync(string sessionId, CancellationToken cancellationToken) => ValueTask.FromResult<RealtimeIdentity?>(null);
    }
    private sealed class Tickets : IConnectionTicketStore
    {
        public ValueTask<string> IssueAsync(RealtimeIdentity identity, string audience, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<RealtimeIdentity?> ConsumeAsync(string ticket, string audience, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
