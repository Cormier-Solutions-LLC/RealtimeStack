using System.Text.Json.Serialization;

namespace Cormier.Licensing.Products;

internal sealed record ClientIdentity(string name, string version);
internal sealed class ActivationRequest
{
    public int protocol { get; init; }
    public string? licenseKey { get; init; }
    public string? application { get; init; }
    public string? applicationVersion { get; init; }
    public ClientIdentity? client { get; init; }
    public string? deploymentType { get; init; }
    public string? instanceId { get; init; }
    public string? hostname { get; init; }
    public ResourceAmount? resources { get; init; }
    public string? nonce { get; init; }
}
internal sealed class RenewalRequest
{
    public int protocol { get; init; }
    public string? instanceId { get; init; }
    public string? nonce { get; init; }
    public ResourceAmount? resources { get; init; }
    public ResourceAmount? usage { get; init; }
}
internal sealed record DeactivationRequest(int protocol, string instanceId, string reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActivationRequest))]
[JsonSerializable(typeof(RenewalRequest))]
[JsonSerializable(typeof(DeactivationRequest))]
[JsonSerializable(typeof(ActivationResponse))]
[JsonSerializable(typeof(LeaseResponse))]
[JsonSerializable(typeof(UsageResponse))]
[JsonSerializable(typeof(UsageReport))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(LeasePayload))]
[JsonSerializable(typeof(object))]
internal partial class LicensingJson : JsonSerializerContext;
