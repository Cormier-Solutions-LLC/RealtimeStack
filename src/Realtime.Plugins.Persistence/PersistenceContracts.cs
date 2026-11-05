namespace Realtime.Plugins.Persistence;

public enum DurableRecordKind { Message, Notification, TaskStatus }

/// <summary>A stream belongs to one authenticated tenant and owner. Use a task ID as the stream for task history.</summary>
public sealed record DurableStreamKey(string TenantId, string OwnerId, string StreamId, DurableRecordKind Kind);

/// <summary>Sequence numbers must increase within a stream. The database rejects duplicate numbers.</summary>
public sealed record DurableRecord(long Sequence, string Payload, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt = null);

public interface IRealtimePersistenceStore
{
    Task AppendAsync(DurableStreamKey stream, DurableRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DurableRecord>> ReadAsync(DurableStreamKey stream, long afterSequence = 0, int limit = 100,
        CancellationToken cancellationToken = default);
    Task<DurableRecord?> ReadLatestAsync(DurableStreamKey stream, CancellationToken cancellationToken = default);
    /// <summary>Call only after a client confirms successful processing. Delivery is at least once.</summary>
    Task AcknowledgeAsync(DurableStreamKey stream, long throughSequence, CancellationToken cancellationToken = default);
    /// <summary>Explicit maintenance operation across all streams. Expired records are excluded from reads immediately.</summary>
    Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default);
}

public enum DurableTaskState { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>Store a complete snapshot on each task transition, including errors and completion time.</summary>
public sealed record DurableTaskStatus(string TaskId, DurableTaskState State, string? Progress = null,
    string? Error = null, DateTimeOffset? FinishedAt = null);
