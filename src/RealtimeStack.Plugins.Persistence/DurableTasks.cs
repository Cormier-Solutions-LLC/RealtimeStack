using System.Text.Json;
using System.Text.Json.Serialization;

namespace RealtimeStack.Plugins.Persistence;

/// <summary>Persists task snapshots independently of a browser connection. Does not execute or schedule tasks.</summary>
public sealed class DurableTasks(IRealtimeStackPersistenceStore store, TimeProvider? timeProvider = null)
{
    private readonly IRealtimeStackPersistenceStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task SaveAsync(string tenantId, string ownerId, long sequence, DurableTaskStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!Enum.IsDefined(status.State)) throw new ArgumentOutOfRangeException(nameof(status));
        var terminal = status.State is DurableTaskState.Succeeded or DurableTaskState.Failed or DurableTaskState.Cancelled;
        if (terminal != status.FinishedAt.HasValue)
            throw new ArgumentException("Terminal states require a completion time; active states cannot have one.", nameof(status));
        if (status.State == DurableTaskState.Failed && string.IsNullOrWhiteSpace(status.Error))
            throw new ArgumentException("Failed tasks require an error description.", nameof(status));
        var stream = new DurableStreamKey(tenantId, ownerId, status.TaskId, DurableRecordKind.TaskStatus);
        return store.AppendAsync(stream, new DurableRecord(sequence,
            JsonSerializer.Serialize(status, PersistenceJsonContext.Default.DurableTaskStatus), clock.GetUtcNow()), cancellationToken);
    }

    public async Task<DurableTaskStatus?> GetAsync(string tenantId, string ownerId, string taskId,
        CancellationToken cancellationToken = default)
    {
        var record = await store.ReadLatestAsync(new DurableStreamKey(tenantId, ownerId, taskId, DurableRecordKind.TaskStatus),
            cancellationToken).ConfigureAwait(false);
        return record is null ? null : JsonSerializer.Deserialize(record.Payload, PersistenceJsonContext.Default.DurableTaskStatus);
    }
}

[JsonSerializable(typeof(DurableTaskStatus))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext;
