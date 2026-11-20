namespace Cormier.Licensing.Products;

/// <summary>Queue statistics of a <see cref="WorkGate"/>.</summary>
/// <param name="Capacity">Tasks allowed to run at once.</param>
/// <param name="Running">Tasks running.</param>
/// <param name="Queued">Tasks waiting.</param>
/// <param name="MaxDepth">Deepest the queue got this period.</param>
/// <param name="WaitMilliseconds">Total time tasks waited this period.</param>
public sealed record WorkGateStats(int Capacity, int Running, int Queued, int MaxDepth, double WaitMilliseconds);

/// <summary>
/// A concurrency gate render and job paths run work through. When the instance is
/// over its licensed resources the capacity shrinks and work waits; it is never rejected.
/// </summary>
public sealed class WorkGate
{
    private readonly object sync = new();
    private readonly LinkedList<(TaskCompletionSource Ready, long EnqueuedAt)> waiters = new();
    private readonly TimeProvider time;
    private int running, maxDepth, limit;
    private double waited;

    /// <summary>Creates a gate with <paramref name="baseSlots"/> slots.</summary>
    public WorkGate(int baseSlots = 8, TimeProvider? time = null)
    {
        if (baseSlots < 1) throw new ArgumentOutOfRangeException(nameof(baseSlots), "A work gate needs at least one slot");
        BaseSlots = limit = baseSlots;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Slots when unthrottled.</summary>
    public int BaseSlots { get; }

    /// <summary>Tasks currently allowed to run at once.</summary>
    public int Capacity { get { lock (sync) return limit; } }

    /// <summary>Set the capacity (clamped to 1..BaseSlots).</summary>
    public void SetCapacity(int slots)
    {
        lock (sync) limit = Math.Clamp(slots, 1, BaseSlots);
        Drain();
    }

    /// <summary>Run <paramref name="task"/> when a slot is free.</summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> task, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await task(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (sync) running--;
            Drain();
        }
    }

    /// <summary>Run <paramref name="task"/> when a slot is free.</summary>
    public Task RunAsync(Func<CancellationToken, Task> task, CancellationToken cancellationToken = default) =>
        RunAsync<bool>(async token => { await task(token).ConfigureAwait(false); return true; }, cancellationToken);

    private Task EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<(TaskCompletionSource Ready, long EnqueuedAt)> node;
        lock (sync)
        {
            if (running < limit && waiters.Count == 0)
            {
                running++;
                return Task.CompletedTask;
            }
            node = waiters.AddLast((new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), time.GetTimestamp()));
            maxDepth = Math.Max(maxDepth, waiters.Count);
        }
        if (!cancellationToken.CanBeCanceled) return node.Value.Ready.Task;
        var registration = cancellationToken.Register(() =>
            {
                lock (sync)
                {
                    if (node.List is null) return;
                    waiters.Remove(node);
                }
                node.Value.Ready.TrySetCanceled(cancellationToken);
            });
        return WaitAndRelease(node.Value.Ready.Task, registration);
    }

    /// <summary>Release the cancellation registration once admitted or cancelled, so long-lived tokens retain nothing.</summary>
    private static async Task WaitAndRelease(Task ready, CancellationTokenRegistration registration)
    {
        try
        {
            await ready.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister();
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Drain()
    {
        while (true)
        {
            TaskCompletionSource ready;
            lock (sync)
            {
                if (running >= limit || waiters.First is not { } first) return;
                waiters.RemoveFirst();
                running++;
                waited += time.GetElapsedTime(first.Value.EnqueuedAt).TotalMilliseconds;
                ready = first.Value.Ready;
            }
            ready.TrySetResult();
        }
    }

    /// <summary>Current statistics.</summary>
    public WorkGateStats Stats()
    {
        lock (sync) return new(limit, running, waiters.Count, maxDepth, waited);
    }

    /// <summary>Return this period's statistics and start a new period.</summary>
    public WorkGateStats TakeStats()
    {
        lock (sync)
        {
            var stats = new WorkGateStats(limit, running, waiters.Count, maxDepth, waited);
            maxDepth = waiters.Count;
            waited = 0;
            return stats;
        }
    }
}
