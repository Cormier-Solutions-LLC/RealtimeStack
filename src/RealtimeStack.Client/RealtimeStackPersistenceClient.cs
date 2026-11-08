using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RealtimeStack.Client;

public enum RealtimeStackInboxKind { Message, Notification }
public sealed record RealtimeStackSavedRecord(long Sequence, string Payload, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record RealtimeStackTaskStatus(string TaskId, int State, string? Progress, string? Error, DateTimeOffset? FinishedAt);

/// <summary>Portable session-scoped persistence client. The host owns HttpClient, cookies, Origin, and TLS configuration.</summary>
public sealed class RealtimeStackPersistenceClient
{
    private readonly HttpClient http;
    private readonly string basePath;
    public RealtimeStackPersistenceClient(HttpClient httpClient, string basePath = "/realtimestack/persistence")
    {
        http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(basePath) || !basePath.StartsWith("/", StringComparison.Ordinal) ||
            basePath.StartsWith("//", StringComparison.Ordinal) || basePath.EndsWith("/", StringComparison.Ordinal) || basePath.IndexOfAny(['?', '#', '\\', '{', '}']) >= 0)
            throw new ArgumentException("Use a same-origin absolute route prefix.", nameof(basePath));
        this.basePath = basePath;
    }

    public async Task<IReadOnlyList<RealtimeStackSavedRecord>> ReadAsync(string streamId, RealtimeStackInboxKind kind,
        long afterSequence = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        Validate(streamId, kind, afterSequence);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var response = await PostAsync("records/read", new InboxRead(streamId, (int)kind, afterSequence, limit),
            PersistenceClientJsonContext.Default.InboxRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(body, PersistenceClientJsonContext.Default.RealtimeStackSavedRecordArray, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task AcknowledgeAsync(string streamId, RealtimeStackInboxKind kind, long throughSequence, CancellationToken cancellationToken = default)
    {
        Validate(streamId, kind, throughSequence);
        using var response = await PostAsync("records/ack", new InboxAck(streamId, (int)kind, throughSequence),
            PersistenceClientJsonContext.Default.InboxAck, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<RealtimeStackTaskStatus?> GetTaskStatusAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Validate(taskId, RealtimeStackInboxKind.Message, 0);
        using var response = await PostAsync("tasks/read", new TaskLookup(taskId), PersistenceClientJsonContext.Default.TaskLookup,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(body, PersistenceClientJsonContext.Default.RealtimeStackTaskStatus, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T request, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(basePath + "/" + path, UriKind.Relative));
        message.Content = new StringContent(JsonSerializer.Serialize(request, type), Encoding.UTF8, "application/json");
        return await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(string id, RealtimeStackInboxKind kind, long sequence)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("IDs must contain 1 to 128 characters.", nameof(id));
        if (kind is not (RealtimeStackInboxKind.Message or RealtimeStackInboxKind.Notification)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (sequence is < 0 or > 9007199254740991) throw new ArgumentOutOfRangeException(nameof(sequence));
    }
}

internal sealed record InboxRead(string StreamId, int Kind, long AfterSequence, int Limit);
internal sealed record InboxAck(string StreamId, int Kind, long ThroughSequence);
internal sealed record TaskLookup(string TaskId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InboxRead))]
[JsonSerializable(typeof(InboxAck))]
[JsonSerializable(typeof(TaskLookup))]
[JsonSerializable(typeof(RealtimeStackSavedRecord[]))]
[JsonSerializable(typeof(RealtimeStackTaskStatus))]
internal sealed partial class PersistenceClientJsonContext : JsonSerializerContext;
