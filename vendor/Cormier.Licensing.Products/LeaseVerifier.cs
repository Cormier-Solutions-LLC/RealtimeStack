using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Cormier.Licensing.Products;

/// <summary>A lease failed verification; the client keeps its previous valid lease.</summary>
public sealed class LeaseException(string code, string message) : Exception(message)
{
    /// <summary><c>signature_rejected</c> or <c>lease_rejected</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>Verifies Ed25519-signed leases with configured public keys.</summary>
public sealed partial class LeaseVerifier
{
    private readonly Dictionary<string, Ed25519PublicKeyParameters> keys = new(StringComparer.Ordinal);

    /// <summary>Creates a verifier from raw 32-byte Ed25519 public keys (base64 or base64url) by key ID.</summary>
    public LeaseVerifier(IReadOnlyDictionary<string, string> publicKeys)
    {
        foreach (var (keyId, key) in publicKeys)
        {
            var raw = Base64Url.Decode(key.Trim().Replace('+', '-').Replace('/', '_').TrimEnd('='));
            if (raw.Length != 32) throw new ArgumentException($"Licensing public key {keyId} must be a raw 32-byte Ed25519 key");
            keys[keyId] = new Ed25519PublicKeyParameters(raw, 0);
        }
    }

    /// <summary>Number of configured keys.</summary>
    public int Count => keys.Count;

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex Base64UrlPattern();

    /// <summary>Verify signature, binding and times; returns the payload or throws <see cref="LeaseException"/>.</summary>
    public LeasePayload Verify(SignedLease? envelope, string? activationId, string instanceId, string application, string nonce, DateTimeOffset now)
    {
        if (envelope is null || !Base64UrlPattern().IsMatch(envelope.Payload) || !Base64UrlPattern().IsMatch(envelope.Signature))
            throw new LeaseException("lease_rejected", "Lease envelope is malformed");
        if (!keys.TryGetValue(envelope.KeyId ?? "", out var key))
            throw new LeaseException("signature_rejected", $"Lease is signed by an unknown key ({envelope.KeyId})");
        byte[] signature, payloadBytes;
        try
        {
            signature = Base64Url.Decode(envelope.Signature);
            payloadBytes = Base64Url.Decode(envelope.Payload);
        }
        catch (FormatException)
        {
            throw new LeaseException("lease_rejected", "Lease envelope is not valid base64url");
        }
        var message = Encoding.ASCII.GetBytes(LicensingProtocol.SigningPrefix + envelope.Payload);
        var verifier = new Ed25519Signer();
        verifier.Init(false, key);
        verifier.BlockUpdate(message, 0, message.Length);
        if (signature.Length != 64 || !verifier.VerifySignature(signature))
            throw new LeaseException("signature_rejected", "Lease signature is invalid");
        LeasePayload? lease;
        try
        {
            lease = JsonSerializer.Deserialize(payloadBytes, LicensingJson.Default.LeasePayload);
        }
        catch (JsonException)
        {
            throw new LeaseException("lease_rejected", "Lease payload is not valid JSON");
        }
        static void Reject(string reason) => throw new LeaseException("lease_rejected", reason);
        if (lease is null) throw new LeaseException("lease_rejected", "Lease payload is empty");
        if (lease.V != 1) Reject("Unsupported lease version");
        if (activationId is not null && lease.ActivationId != activationId) Reject("Lease is for another activation");
        if (lease.InstanceId != instanceId) Reject("Lease is for another instance");
        if (lease.Application != application) Reject("Lease is for another application");
        if (lease.Nonce != nonce) Reject("Lease nonce does not match");
        if (lease.Kind is not ("trial" or "developer" or "production")) Reject("Lease kind is invalid");
        if (lease.Kind == "trial" && lease.TrialStartedAt is null) Reject("Trial lease has no trial start");
        if (lease.IssuedAt > now.AddMinutes(5)) Reject("Lease is issued in the future");
        if (lease.ExpiresAt <= lease.IssuedAt || lease.ExpiresAt - lease.IssuedAt > TimeSpan.FromDays(5) + TimeSpan.FromHours(1))
            Reject("Lease lifetime is invalid");
        if (lease.ExpiresAt <= now) Reject("Lease has expired");
        if (!LicensingProtocol.DeploymentTypes.Contains(lease.DeploymentType)) Reject("Lease deployment type is invalid");
        static bool Count(double value) => double.IsFinite(value) && value >= 0;
        if (lease.Limits is null || lease.Limits.Scope is not ("total" or "host") || !(lease.Limits.Cores > 0) || !double.IsFinite(lease.Limits.Cores) || lease.Limits.MemoryBytes <= 0)
            Reject("Lease limits are invalid");
        if (lease.Overage is null || !Count(lease.Overage.CoreSeconds) || !Count(lease.Overage.MemoryGibSeconds)) Reject("Lease overage balance is invalid");
        if (lease.Policy is null || !(lease.Policy.UpgradeThreshold > 0) || !double.IsFinite(lease.Policy.UpgradeThreshold)
            || !Count(lease.Policy.BurstRatio) || !Count(lease.Policy.BurstSeconds) || !Count(lease.Policy.BurstCooldownSeconds))
            Reject("Lease policy is invalid");
        if (lease.Cluster is { } cluster && (!Count(cluster.Cores) || cluster.MemoryBytes < 0)) Reject("Lease cluster usage is invalid");
        return lease with { Entitlements = lease.Entitlements ?? [], Hostnames = lease.Hostnames ?? [] };
    }
}

internal static class Base64Url
{
    public static byte[] Decode(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        if (text.Length % 4 == 1) throw new FormatException("Invalid base64url length");
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        return Convert.FromBase64String(text);
    }

    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
