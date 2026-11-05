using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cormier.Realtime.Client;

public enum RealtimeInboxKind { Message, Notification }
public sealed record RealtimeSavedRecord(long Sequence, string Payload, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record RealtimeTaskStatus(string TaskId, int State, string? Progress, string? Error, DateTimeOffset? FinishedAt);

/// <summary>Portable session-scoped persistence client. The host owns HttpClient, cookies, Origin, and TLS configuration.</summary>
public sealed class RealtimePersistenceClient
{
    private readonly HttpClient http;
    private readonly string basePath;
    public RealtimePersistenceClient(HttpClient httpClient, string basePath = "/realtime/persistence")
    {
        http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(basePath) || !basePath.StartsWith("/", StringComparison.Ordinal) ||
            basePath.StartsWith("//", StringComparison.Ordinal) || basePath.EndsWith("/", StringComparison.Ordinal) || basePath.IndexOfAny(['?', '#', '\\', '{', '}']) >= 0)
            throw new ArgumentException("Use a same-origin absolute route prefix.", nameof(basePath));
        this.basePath = basePath;
    }

    public async Task<IReadOnlyList<RealtimeSavedRecord>> ReadAsync(string streamId, RealtimeInboxKind kind,
        long afterSequence = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        Validate(streamId, kind, afterSequence);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var response = await PostAsync("records/read", new InboxRead(streamId, (int)kind, afterSequence, limit),
            PersistenceClientJsonContext.Default.InboxRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(body, PersistenceClientJsonContext.Default.RealtimeSavedRecordArray, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task AcknowledgeAsync(string streamId, RealtimeInboxKind kind, long throughSequence, CancellationToken cancellationToken = default)
    {
        Validate(streamId, kind, throughSequence);
        using var response = await PostAsync("records/ack", new InboxAck(streamId, (int)kind, throughSequence),
            PersistenceClientJsonContext.Default.InboxAck, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<RealtimeTaskStatus?> GetTaskStatusAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Validate(taskId, RealtimeInboxKind.Message, 0);
        using var response = await PostAsync("tasks/read", new TaskLookup(taskId), PersistenceClientJsonContext.Default.TaskLookup,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(body, PersistenceClientJsonContext.Default.RealtimeTaskStatus, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T request, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(basePath + "/" + path, UriKind.Relative));
        message.Content = new StringContent(JsonSerializer.Serialize(request, type), Encoding.UTF8, "application/json");
        return await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(string id, RealtimeInboxKind kind, long sequence)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("IDs must contain 1 to 128 characters.", nameof(id));
        if (kind is not (RealtimeInboxKind.Message or RealtimeInboxKind.Notification)) throw new ArgumentOutOfRangeException(nameof(kind));
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
[JsonSerializable(typeof(RealtimeSavedRecord[]))]
[JsonSerializable(typeof(RealtimeTaskStatus))]
internal sealed partial class PersistenceClientJsonContext : JsonSerializerContext;
