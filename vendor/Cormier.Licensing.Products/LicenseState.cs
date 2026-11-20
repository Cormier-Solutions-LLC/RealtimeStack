namespace Cormier.Licensing.Products;

/// <summary>License kinds. <see cref="None"/> means no valid lease: DEMO.</summary>
public enum LicenseKind
{
    /// <summary>No license, expired lease or refused activation.</summary>
    None,
    /// <summary>28-day trial.</summary>
    Trial,
    /// <summary>Developer license.</summary>
    Developer,
    /// <summary>Production license.</summary>
    Production,
}

/// <summary>The license state the watermark seam reads: <c>{ kind, trialDay }</c>.</summary>
/// <param name="Kind">License kind.</param>
/// <param name="TrialDay">1-based trial day for <see cref="LicenseKind.Trial"/>.</param>
public sealed record LicenseState(LicenseKind Kind, int? TrialDay = null)
{
    /// <summary>No license (DEMO).</summary>
    public static readonly LicenseState None = new(LicenseKind.None);

    /// <summary>Lower-case kind as the protocol and the npm package spell it.</summary>
    public string KindName => Kind.ToString().ToLowerInvariant();

    /// <summary>Watermark text: DEMO, TRIAL, DEVELOPMENT or null for a clean output.</summary>
    public string? Watermark => Kind switch
    {
        LicenseKind.Production => null,
        LicenseKind.Developer => "DEVELOPMENT",
        LicenseKind.Trial => (TrialDay ?? 1) <= 7 ? null : "TRIAL",
        _ => "DEMO",
    };

    /// <summary>The state a lease carries at <paramref name="now"/>.</summary>
    public static LicenseState FromLease(LeasePayload? lease, DateTimeOffset now)
    {
        if (lease is null) return None;
        switch (lease.Kind)
        {
            case "production": return new(LicenseKind.Production);
            case "developer": return new(LicenseKind.Developer);
            case "trial" when lease.TrialStartedAt is { } started:
                var day = (int)Math.Floor(Math.Max(0, (now - started).TotalDays)) + 1;
                return day > 28 ? None : new(LicenseKind.Trial, day);
            default: return None;
        }
    }
}
