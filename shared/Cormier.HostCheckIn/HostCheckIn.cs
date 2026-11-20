using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cormier.HostCheckIn;

public sealed record HostCheckInOptions(bool Configured, string ProductId, string InstallationId, string InstanceId, string Application, string ApplicationVersion, string LicensingOrigin, string AgentToken, string? Namespace, string? PodName, string? PodUid, string? NodeName, bool Container)
{
    public static HostCheckInOptions Read(IConfiguration configuration, string productId, string application, string version)
    {
        var installation = configuration["CORMIER_LICENSE_INSTALLATION_ID"] ?? "";
        var tokenFile = configuration["CORMIER_LICENSE_AGENT_TOKEN_FILE"];
        var token = string.IsNullOrWhiteSpace(tokenFile) ? configuration["CORMIER_LICENSE_AGENT_TOKEN"] ?? "" : File.ReadAllText(tokenFile).Trim();
        var origin = (configuration["CORMIER_LICENSING_URL"] ?? configuration["CORMIER_LICENSE_SERVER_URL"] ?? configuration["Licensing:Origin"] ?? "").TrimEnd('/');
        var configured = installation.Length > 0 || token.Length > 0;
        if (configured && (installation.Length is < 8 or > 128 || token.Length < 32 || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
            throw new InvalidOperationException("Host check-in requires a stable installation ID, dedicated agent credential and exact HTTPS licensing origin.");
        var instance = configuration["CORMIER_LICENSE_INSTANCE_ID"] ?? application + ":" + (configuration["POD_UID"] ?? Environment.MachineName);
        var container = !string.IsNullOrWhiteSpace(configuration["KUBERNETES_SERVICE_HOST"]) || configuration["DOTNET_RUNNING_IN_CONTAINER"] == "true" || File.Exists("/.dockerenv");
        return new(configured, productId, installation, instance, application, version, origin, token, configuration["POD_NAMESPACE"], configuration["POD_NAME"], configuration["POD_UID"], configuration["NODE_NAME"], container);
    }
    public override string ToString() => $"HostCheckInOptions {{ Configured = {Configured}, Credentials = [redacted] }}";
}

public sealed record HostCheckInSnapshot(bool Configured, DateTimeOffset? LastSuccess, bool Stale, string? Error);
public sealed record HostInventory(string Hostname, string Os, string OsVersion, string Architecture, int CpuCount, long MemoryBytes, string ResourceScope);
public sealed record KubernetesInventory(string? Namespace, string? PodName, string? PodUid, string? NodeName);
public sealed record HostCheckInRequest(string ProductId, string InstallationId, string InstanceId, string Application, string ApplicationVersion, string DeploymentType, HostInventory Host, KubernetesInventory? Kubernetes, string? LicenseState);
public sealed record HostCheckInResponse(string InstallationId, string InstanceId, DateTimeOffset ReceivedAt, int NextCheckInSeconds);

public sealed class InstallationHostCheckIn(HostCheckInOptions options, IHttpClientFactory clients, ILogger<InstallationHostCheckIn> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(LogLevel.Warning, new EventId(4401, "HostCheckInFailed"), "Authenticated host check-in failed; existing work and the last successful report are preserved.");
    private sealed record State(DateTimeOffset? ReceivedAt, long SucceededAt, string? Error);
    private State state = new(null, 0, null);
    public HostCheckInSnapshot Snapshot { get { var current = Volatile.Read(ref state); return new(options.Configured, current.ReceivedAt, options.Configured && (current.ReceivedAt is null || Environment.TickCount64 - current.SucceededAt > 180_000), current.Error); } }
    public string HealthStatus => !options.Configured ? "unconfigured" : Snapshot.Error is not null ? "failed" : Snapshot.Stale ? "stale" : "healthy";
    public HostCheckInRequest Collect()
    {
        var kubernetes = options.Namespace is not null || options.PodUid is not null ? new KubernetesInventory(options.Namespace, options.PodName, options.PodUid, options.NodeName) : null;
        var os = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsLinux() ? "linux" : "darwin";
        var host = new HostInventory(Environment.MachineName, os, Environment.OSVersion.VersionString, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), Environment.ProcessorCount, Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes), options.Container ? "container" : "host");
        return new(options.ProductId, options.InstallationId, options.InstanceId, options.Application, options.ApplicationVersion, kubernetes is not null ? "kubernetes" : options.Container ? "docker" : OperatingSystem.IsWindows() ? "windows" : "linux", host, kubernetes, null);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Configured) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckInAsync(stoppingToken).ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
    public async Task CheckInAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Configured) return;
        try
        {
            using var client = clients.CreateClient("installation-host-checkin");
            using var request = new HttpRequestMessage(HttpMethod.Post, options.LicensingOrigin + "/v1/installations/check-in");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AgentToken);
            request.Content = new StringContent(JsonSerializer.Serialize(Collect(), HostCheckInJsonContext.Default.HostCheckInRequest), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException();
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length > 32 * 1024) throw new InvalidDataException();
            var result = JsonSerializer.Deserialize(text, HostCheckInJsonContext.Default.HostCheckInResponse) ?? throw new InvalidDataException();
            if (result.InstallationId != options.InstallationId || result.InstanceId != options.InstanceId || result.NextCheckInSeconds != 60 || result.ReceivedAt > DateTimeOffset.UtcNow.AddMinutes(5) || result.ReceivedAt < DateTimeOffset.UtcNow.AddDays(-1)) throw new InvalidDataException();
            Volatile.Write(ref state, new(result.ReceivedAt, Environment.TickCount64, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
        { var current = Volatile.Read(ref state); Volatile.Write(ref state, current with { Error = "Host check-in failed; the last successful report is retained." }); Failed(logger, null); }
    }
}

public static class HostCheckInHosting
{
    public static IServiceCollection AddInstallationHostCheckIn(this IServiceCollection services, IConfiguration configuration, string productId, string application, string version)
    {
        services.AddSingleton(HostCheckInOptions.Read(configuration, productId, application, version));
        services.AddHttpClient("installation-host-checkin", client => { client.Timeout = TimeSpan.FromSeconds(8); client.MaxResponseContentBufferSize = 32 * 1024; }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<InstallationHostCheckIn>(); services.AddHostedService(provider => provider.GetRequiredService<InstallationHostCheckIn>()); return services;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(HostCheckInRequest))]
[JsonSerializable(typeof(HostCheckInResponse))]
[JsonSerializable(typeof(HostCheckInSnapshot))]
internal sealed partial class HostCheckInJsonContext : JsonSerializerContext;
