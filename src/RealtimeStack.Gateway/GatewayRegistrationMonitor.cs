using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RealtimeStack.Gateway;

public sealed record GatewayRegistrationOptions(bool Required, Uri? StatusUrl, string InstallationId, string ServiceToken, string CacheFile, TimeSpan PollInterval)
{
    public static GatewayRegistrationOptions Read(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Registration:Required")) return new(false, null, "", "", "", TimeSpan.FromMinutes(1));
        var endpoint = configuration["Registration:StatusUrl"];
        var installationId = configuration["Registration:InstallationId"] ?? "";
        var file = configuration["Registration:ServiceTokenFile"];
        var token = !string.IsNullOrWhiteSpace(file) ? File.ReadAllText(file).Trim() : configuration["Registration:ServiceToken"] ?? "";
        var cache = configuration["Registration:CacheFile"] ?? "";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && configuration.GetValue<bool>("Registration:AllowInsecureHttp")) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/internal/product/registration" ||
            installationId.Length is < 8 or > 128 || installationId.Any(char.IsControl) || token.Length < 32 || !Path.IsPathFullyQualified(cache))
            throw new InvalidOperationException("Required registration needs an explicit trusted status endpoint, stable installation ID, dedicated service token and absolute persistent cache file.");
        return new(true, uri, installationId, token, cache, TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Registration:PollSeconds", 60), 30, 600)));
    }
    public override string ToString() => $"GatewayRegistrationOptions {{ Required = {Required}, Credentials = [redacted] }}";
}

/// <summary>Optional installation registration readiness, independent of application wire authorization.</summary>
public sealed class GatewayRegistrationMonitor : BackgroundService
{
    private static readonly Action<ILogger, Exception?> RefreshFailed = LoggerMessage.Define(LogLevel.Warning,
        new EventId(4301, "RegistrationRefreshFailed"), "Installation registration refresh failed; retaining any previously verified permanent free registration.");
    private readonly GatewayRegistrationOptions options;
    private readonly IHttpClientFactory clients;
    private readonly ILogger<GatewayRegistrationMonitor> logger;
    private CachedGatewayRegistration? receipt;
    public bool Required => options.Required;
    public bool Registered => Volatile.Read(ref receipt) is not null;
    public bool Ready => !Required || Registered;
    public string HealthStatus => !Required ? "not-required" : Registered ? "registered" : "setup-required";

    public GatewayRegistrationMonitor(GatewayRegistrationOptions options, IHttpClientFactory clients, ILogger<GatewayRegistrationMonitor> logger)
    {
        this.options = options; this.clients = clients; this.logger = logger;
        if (!options.Required) return;
        try
        {
            if (!File.Exists(options.CacheFile)) return;
            if (new FileInfo(options.CacheFile).Length > 32 * 1024) throw new InvalidDataException();
            var envelope = JsonSerializer.Deserialize(File.ReadAllText(options.CacheFile), GatewayRegistrationJsonContext.Default.GatewayRegistrationCache) ?? throw new InvalidDataException();
            var mac = Convert.FromBase64String(envelope.Mac);
            if (!CryptographicOperations.FixedTimeEquals(mac, Authenticate(envelope.Payload))) throw new InvalidDataException();
            var saved = JsonSerializer.Deserialize(envelope.Payload, GatewayRegistrationJsonContext.Default.CachedGatewayRegistration) ?? throw new InvalidDataException();
            Validate(saved.Status, options.InstallationId);
            if (saved.ValidatedAt > DateTimeOffset.UtcNow.AddMinutes(1)) throw new InvalidDataException();
            receipt = saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException or ArgumentException)
        { RefreshFailed(logger, null); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Required) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
            try { await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Required) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, options.StatusUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceToken);
            using var response = await clients.CreateClient("registration-state").SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 32 * 1024) throw new InvalidDataException();
            var status = JsonSerializer.Deserialize(body, GatewayRegistrationJsonContext.Default.GatewayRegistrationStatus) ?? throw new InvalidDataException();
            Validate(status, options.InstallationId);
            if (Registered) return;
            var saved = new CachedGatewayRegistration(status, DateTimeOffset.UtcNow);
            Save(saved);
            Volatile.Write(ref receipt, saved);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException or IOException or UnauthorizedAccessException)
        { RefreshFailed(logger, null); }
    }

    public static void Validate(GatewayRegistrationStatus status, string installationId)
    {
        if (status.ProductId != "realtime" || status.InstallationId != installationId || !status.Registered ||
            status.Pricing is not { Model: "free-registration", AmountCents: 0, Currency: "USD" })
            throw new InvalidDataException("A verified free registration for this installation is required.");
    }
    private byte[] Authenticate(string payload) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.ServiceToken), Encoding.UTF8.GetBytes(options.StatusUrl!.AbsoluteUri + "\n" + payload));
    private void Save(CachedGatewayRegistration saved)
    {
        var payload = JsonSerializer.Serialize(saved, GatewayRegistrationJsonContext.Default.CachedGatewayRegistration);
        var envelope = new GatewayRegistrationCache(payload, Convert.ToBase64String(Authenticate(payload)));
        Directory.CreateDirectory(Path.GetDirectoryName(options.CacheFile)!);
        var temporary = options.CacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, fileOptions))
            { JsonSerializer.Serialize(stream, envelope, GatewayRegistrationJsonContext.Default.GatewayRegistrationCache); stream.Flush(true); }
            File.Move(temporary, options.CacheFile, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record GatewayRegistrationPricing(string Model, long AmountCents, string Currency);
public sealed record GatewayRegistrationStatus(string ProductId, string InstallationId, bool Registered, GatewayRegistrationPricing? Pricing);
public sealed record CachedGatewayRegistration(GatewayRegistrationStatus Status, DateTimeOffset ValidatedAt);
public sealed record GatewayRegistrationCache(string Payload, string Mac);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(GatewayRegistrationStatus))]
[JsonSerializable(typeof(CachedGatewayRegistration))]
[JsonSerializable(typeof(GatewayRegistrationCache))]
internal sealed partial class GatewayRegistrationJsonContext : JsonSerializerContext;
