using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cormier.Licensing.Products;

/// <summary>Licensing protocol version 1 wire types (docs/runtime/licensing-protocol.md).</summary>
public static class LicensingProtocol
{
    /// <summary>The protocol version this client speaks.</summary>
    public const int Version = 1;
    /// <summary>Default LicensingServer address.</summary>
    public const string DefaultServerUrl = "https://licensing.example.invalid";
    /// <summary>Prefix of the bytes a lease signature covers.</summary>
    public const string SigningPrefix = "proof-lease.v1.";
    /// <summary>Deployment types the protocol defines.</summary>
    public static readonly IReadOnlyList<string> DeploymentTypes = ["kubernetes", "docker", "linux", "windows", "hosted", "embedded"];

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>CPU and memory available to (or used by) an instance.</summary>
public sealed record ResourceAmount
{
    /// <summary>CPU cores; may be fractional.</summary>
    public double Cores { get; init; }
    /// <summary>Memory in bytes.</summary>
    public long MemoryBytes { get; init; }
    /// <summary>Where the numbers came from: cgroup-v2, cgroup-v1, host or configured (resources only).</summary>
    public string? Source { get; init; }
    /// <summary>When usage was measured (usage only).</summary>
    public DateTimeOffset? MeasuredAt { get; init; }
}

/// <summary>A signed lease envelope as sent by the server.</summary>
public sealed record SignedLease
{
    /// <summary>base64url of the UTF-8 JSON payload.</summary>
    public string Payload { get; init; } = "";
    /// <summary>base64url Ed25519 signature over <c>proof-lease.v1.</c> + payload.</summary>
    public string Signature { get; init; } = "";
    /// <summary>Which configured public key verifies the signature.</summary>
    public string KeyId { get; init; } = "";
}

/// <summary>Licensed limits.</summary>
public sealed record LeaseLimits
{
    /// <summary><c>total</c> (all live instances together) or <c>host</c> (this instance).</summary>
    public string Scope { get; init; } = "total";
    /// <summary>Licensed CPU cores.</summary>
    public double Cores { get; init; }
    /// <summary>Licensed memory in bytes.</summary>
    public long MemoryBytes { get; init; }
}

/// <summary>Latest usage of the license's other live instances (total scope).</summary>
public sealed record ClusterUsage
{
    /// <summary>Number of other live instances.</summary>
    public int Instances { get; init; }
    /// <summary>Their CPU cores in use.</summary>
    public double Cores { get; init; }
    /// <summary>Their memory in use.</summary>
    public long MemoryBytes { get; init; }
}

/// <summary>Resource rule parameters.</summary>
public sealed record LeasePolicy
{
    /// <summary>Fraction of the limit that raises the upgrade notice.</summary>
    public double UpgradeThreshold { get; init; } = 0.8;
    /// <summary>How far above the limit a burst may go.</summary>
    public double BurstRatio { get; init; } = 0.2;
    /// <summary>Longest burst in seconds.</summary>
    public double BurstSeconds { get; init; } = 3600;
    /// <summary>Seconds within the license before a new burst may start.</summary>
    public double BurstCooldownSeconds { get; init; } = 3600;
}

/// <summary>Prepaid overage balance, or overage consumed.</summary>
public sealed record OverageBalance
{
    /// <summary>CPU core-seconds.</summary>
    public double CoreSeconds { get; init; }
    /// <summary>Memory GiB-seconds.</summary>
    public double MemoryGibSeconds { get; init; }
}

/// <summary>The verified lease payload.</summary>
public sealed record LeasePayload
{
    /// <summary>Payload version (1).</summary>
    public int V { get; init; }
    /// <summary>Lease identifier.</summary>
    public string LeaseId { get; init; } = "";
    /// <summary>License identifier.</summary>
    public string LicenseId { get; init; } = "";
    /// <summary>Activation the lease belongs to.</summary>
    public string ActivationId { get; init; } = "";
    /// <summary>Instance the lease belongs to.</summary>
    public string InstanceId { get; init; } = "";
    /// <summary>Licensed application.</summary>
    public string Application { get; init; } = "";
    /// <summary>Echo of the request nonce.</summary>
    public string Nonce { get; init; } = "";
    /// <summary>trial, developer or production.</summary>
    public string Kind { get; init; } = "";
    /// <summary>Start of the organization's trial (trial leases).</summary>
    public DateTimeOffset? TrialStartedAt { get; init; }
    /// <summary>When the server signed the lease.</summary>
    public DateTimeOffset IssuedAt { get; init; }
    /// <summary>When to renew.</summary>
    public DateTimeOffset RenewAfter { get; init; }
    /// <summary>When the lease stops being valid without renewal (end of offline grace).</summary>
    public DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Offline grace in seconds (24 hours, more with loyalty grace).</summary>
    public double OfflineGraceSeconds { get; init; }
    /// <summary>End of the paid term (informational).</summary>
    public DateTimeOffset? LicenseEndsAt { get; init; }
    /// <summary>Deployment type the license covers.</summary>
    public string DeploymentType { get; init; } = "";
    /// <summary>Licensed limits.</summary>
    public LeaseLimits Limits { get; init; } = new();
    /// <summary>Other instances' usage (total scope).</summary>
    public ClusterUsage? Cluster { get; init; }
    /// <summary>Resource rule parameters.</summary>
    public LeasePolicy Policy { get; init; } = new();
    /// <summary>Remaining overage balance.</summary>
    public OverageBalance Overage { get; init; } = new();
    /// <summary>Applications and plugin families covered.</summary>
    public IReadOnlyList<string> Entitlements { get; init; } = [];
    /// <summary>Allowed hostnames (empty = any); not enforced by the client.</summary>
    public IReadOnlyList<string> Hostnames { get; init; } = [];
}

/// <summary>A usage event: upgrade-threshold, burst-started, burst-ended, throttle-started, throttle-ended, overage-started, overage-exhausted.</summary>
public sealed record UsageEvent
{
    /// <summary>Event type.</summary>
    public string Type { get; init; } = "";
    /// <summary>When it happened.</summary>
    public DateTimeOffset At { get; init; }
    /// <summary>cores or memory, when the event concerns one.</summary>
    public string? Resource { get; init; }
    /// <summary>Usage ratio against the license.</summary>
    public double? Ratio { get; init; }
    /// <summary>Usage of the resource.</summary>
    public double? Used { get; init; }
    /// <summary>Licensed amount of the resource.</summary>
    public double? Licensed { get; init; }
}

internal sealed record ReportPeriod(DateTimeOffset From, DateTimeOffset To);
internal sealed record ReportUsage(double PeakCores, double AverageCores, long PeakMemoryBytes, long AverageMemoryBytes);
internal sealed record ReportBurst(bool Active, DateTimeOffset? StartedAt, double Seconds);
internal sealed record ReportQueue(bool Throttled, int QueuedTasks, int MaxDepth, double WaitMilliseconds);
internal sealed record UsageReport(
    int Protocol,
    string InstanceId,
    string? LeaseId,
    ReportPeriod Period,
    ReportUsage Usage,
    ReportBurst Burst,
    ReportQueue Queue,
    OverageBalance Overage,
    IReadOnlyList<UsageEvent> Events);

internal sealed record ErrorBody(ErrorDetail? Error);
internal sealed record ErrorDetail(string? Code, string? Message);
internal sealed record ActivationResponse(string? ActivationId, string? ActivationToken, SignedLease? Lease);
internal sealed record LeaseResponse(SignedLease? Lease);
internal sealed record UsageResponse(bool Accepted, OverageBalance? Overage);
