namespace Cormier.Licensing.Products;

/// <summary>Result of one resource-rule step.</summary>
/// <param name="Ratio">The larger of the CPU and memory usage ratios.</param>
/// <param name="CoresRatio">CPU usage ratio.</param>
/// <param name="MemoryRatio">Memory usage ratio.</param>
/// <param name="Factor">Fraction (0–1] of gate slots work may use; below 1 is throttled.</param>
/// <param name="Throttled">Whether work is being queued.</param>
/// <param name="Bursting">Whether usage is above the license within the burst allowance.</param>
/// <param name="Overage">Whether the overage balance covers the excess.</param>
/// <param name="CapCores">Host scope: licensed cores this instance caps itself at.</param>
public sealed record PolicyStep(double Ratio, double CoresRatio, double MemoryRatio, double Factor, bool Throttled, bool Bursting, bool Overage, double? CapCores);

/// <summary>
/// Resource rules of licensing protocol v1: upgrade notice at 80%, overage draw-down,
/// 20% bursts for at most an hour, then throttling. Clock-driven and deterministic.
/// </summary>
public sealed class ResourcePolicy
{
    private const double Gib = 1024d * 1024 * 1024;
    private readonly object sync = new();
    private readonly List<UsageEvent> events = [];
    private DateTimeOffset? lastAt, burstStartedAt, withinSince, periodFrom;
    private double burstUsedMs, periodBurstMs, factor = 1;
    private bool throttled, overageActive, upgradeArmed = true, periodThrottled;
    private DateTimeOffset lastUpgradeAt = DateTimeOffset.MinValue;
    private double balanceCore, balanceMemory;
    private bool hasBalance;
    private double consumedCore, consumedMemory;
    private int samples;
    private double sumCores, peakCores;
    private double sumMemory;
    private long peakMemory;

    /// <summary>Raised for every usage event.</summary>
    public event Action<UsageEvent>? Event;

    /// <summary>Remaining overage balance as this instance sees it.</summary>
    public OverageBalance? Balance { get { lock (sync) return hasBalance ? new() { CoreSeconds = balanceCore, MemoryGibSeconds = balanceMemory } : null; } }

    /// <summary>Adopt the server's balance minus what the server has not deducted yet: this period plus <paramref name="unsent"/> reports.</summary>
    public void SyncBalance(OverageBalance server, OverageBalance? unsent = null)
    {
        lock (sync)
        {
            balanceCore = Math.Max(0, server.CoreSeconds - consumedCore - (unsent?.CoreSeconds ?? 0));
            balanceMemory = Math.Max(0, server.MemoryGibSeconds - consumedMemory - (unsent?.MemoryGibSeconds ?? 0));
            hasBalance = true;
        }
    }

    private void Emit(List<UsageEvent> raised, UsageEvent e)
    {
        events.Add(e);
        raised.Add(e);
    }

    /// <summary>
    /// Apply the rules for usage <paramref name="own"/> at wall time <paramref name="now"/>.
    /// <paramref name="monotonic"/> (elapsed time on a monotonic clock) measures durations for
    /// billing, bursts and cooldown, so system clock changes cost nothing; it defaults to wall time.
    /// </summary>
    public PolicyStep Step(DateTimeOffset now, LeasePayload? lease, ResourceAmount own, ResourceAmount? available = null, TimeSpan? monotonic = null)
    {
        var raised = new List<UsageEvent>();
        PolicyStep result;
        var t = monotonic is { } elapsed ? DateTimeOffset.UnixEpoch + elapsed : now;
        lock (sync) result = StepLocked(now, t, lease, own, available, raised);
        foreach (var e in raised) Event?.Invoke(e);
        return result;
    }

    private PolicyStep StepLocked(DateTimeOffset now, DateTimeOffset t, LeasePayload? lease, ResourceAmount own, ResourceAmount? available, List<UsageEvent> raised)
    {
        var dt = lastAt is { } previous ? Math.Max(0, (t - previous).TotalMilliseconds) : 0;
        lastAt = t;
        periodFrom ??= now;
        samples++;
        sumCores += own.Cores;
        sumMemory += own.MemoryBytes;
        peakCores = Math.Max(peakCores, own.Cores);
        peakMemory = Math.Max(peakMemory, own.MemoryBytes);
        if (lease is null)
        {
            burstUsedMs = 0;
            burstStartedAt = withinSince = null;
            overageActive = false;
            SetFactor(1, now, raised);
            return new(0, 0, 0, 1, false, false, false, null);
        }
        var policy = lease.Policy;
        var limits = lease.Limits;
        var total = limits.Scope == "total";
        var usedCores = own.Cores + (total ? lease.Cluster?.Cores ?? 0 : 0);
        var usedMemory = (double)own.MemoryBytes + (total ? lease.Cluster?.MemoryBytes ?? 0 : 0);
        var coresRatio = usedCores / limits.Cores;
        var memoryRatio = usedMemory / limits.MemoryBytes;
        var ratio = Math.Max(coresRatio, memoryRatio);
        if (!hasBalance)
        {
            balanceCore = lease.Overage.CoreSeconds;
            balanceMemory = lease.Overage.MemoryGibSeconds;
            hasBalance = true;
        }

        if (ratio >= policy.UpgradeThreshold)
        {
            if (upgradeArmed && t - lastUpgradeAt >= TimeSpan.FromDays(1))
            {
                var cores = coresRatio >= memoryRatio;
                Emit(raised, new()
                {
                    Type = "upgrade-threshold",
                    At = now,
                    Resource = cores ? "cores" : "memory",
                    Ratio = Math.Round(ratio, 3),
                    Used = cores ? usedCores : usedMemory,
                    Licensed = cores ? limits.Cores : limits.MemoryBytes,
                });
                upgradeArmed = false;
                lastUpgradeAt = t;
            }
        }
        else if (ratio < policy.UpgradeThreshold - 0.1) upgradeArmed = true;

        var cpuOver = coresRatio > 1;
        var memoryOver = memoryRatio > 1;
        var covered = ratio > 1 && (!cpuOver || balanceCore > 0) && (!memoryOver || balanceMemory > 0);
        var windowMs = policy.BurstSeconds * 1000;
        double target;
        if (covered)
        {
            if (!overageActive) Emit(raised, new() { Type = "overage-started", At = now });
            overageActive = true;
            withinSince = null;
            var coreShare = total && usedCores > 0 ? own.Cores / usedCores : 1;
            var memoryShare = total && usedMemory > 0 ? own.MemoryBytes / usedMemory : 1;
            var cpuDraw = cpuOver ? Math.Min(balanceCore, (usedCores - limits.Cores) * coreShare * dt / 1000) : 0;
            var memoryDraw = memoryOver ? Math.Min(balanceMemory, (usedMemory - limits.MemoryBytes) / Gib * memoryShare * dt / 1000) : 0;
            balanceCore -= cpuDraw;
            balanceMemory -= memoryDraw;
            consumedCore += cpuDraw;
            consumedMemory += memoryDraw;
            if ((cpuOver && balanceCore <= 0) || (memoryOver && balanceMemory <= 0))
            {
                Emit(raised, new() { Type = "overage-exhausted", At = now, Resource = cpuOver && balanceCore <= 0 ? "cores" : "memory" });
                overageActive = false;
            }
            target = double.PositiveInfinity;
        }
        else
        {
            overageActive = false;
            if (ratio > 1)
            {
                withinSince = null;
                if (burstStartedAt is null)
                {
                    burstStartedAt = now;
                    Emit(raised, new() { Type = "burst-started", At = now, Ratio = ratio });
                }
                burstUsedMs += dt;
                periodBurstMs += dt;
            }
            else
            {
                withinSince ??= t;
                if (burstStartedAt is not null && (t - withinSince.Value).TotalSeconds >= policy.BurstCooldownSeconds)
                {
                    Emit(raised, new() { Type = "burst-ended", At = now });
                    burstStartedAt = null;
                    burstUsedMs = 0;
                }
            }
            target = burstStartedAt is not null && burstUsedMs >= windowMs ? 1 : 1 + policy.BurstRatio;
        }
        var next = double.IsPositiveInfinity(target) || ratio <= 0 ? 1 : Math.Min(1, factor * Math.Min(2, target / ratio));
        SetFactor(Math.Max(0.01, next), now, raised);
        return new(ratio, coresRatio, memoryRatio, factor, throttled, burstStartedAt is not null && ratio > 1 && !covered, covered,
            limits.Scope == "host" ? Math.Min(available?.Cores ?? limits.Cores, limits.Cores) : null);
    }

    private void SetFactor(double value, DateTimeOffset now, List<UsageEvent> raised)
    {
        factor = value;
        var isThrottled = value < 1;
        if (isThrottled != throttled) Emit(raised, new() { Type = isThrottled ? "throttle-started" : "throttle-ended", At = now });
        throttled = isThrottled;
        if (isThrottled) periodThrottled = true;
    }

    internal UsageReport TakeReport(DateTimeOffset now, string instanceId, string? leaseId, WorkGateStats? queue)
    {
        lock (sync)
        {
            // Periods strictly increase, so no two reports share the server's idempotency key.
            var from = periodFrom ?? now;
            var to = now > from ? now : from.AddMilliseconds(1);
            var report = new UsageReport(
                LicensingProtocol.Version,
                instanceId,
                leaseId,
                new(from, to),
                new(peakCores, samples > 0 ? sumCores / samples : 0, peakMemory, samples > 0 ? (long)Math.Round(sumMemory / samples) : 0),
                new(burstStartedAt is not null, burstStartedAt, Math.Round(periodBurstMs / 1000)),
                new(periodThrottled || throttled, queue?.Queued ?? 0, queue?.MaxDepth ?? 0, queue?.WaitMilliseconds ?? 0),
                new() { CoreSeconds = consumedCore, MemoryGibSeconds = consumedMemory },
                events.ToArray());
            periodFrom = to;
            periodBurstMs = 0;
            periodThrottled = false;
            samples = 0;
            sumCores = peakCores = sumMemory = 0;
            peakMemory = 0;
            consumedCore = consumedMemory = 0;
            events.Clear();
            return report;
        }
    }
}
