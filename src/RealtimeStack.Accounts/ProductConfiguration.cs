using System.Text.Json;

namespace RealtimeStack.Accounts;

public sealed record ProductConfigurationSnapshot(JsonElement Document, string Revision, long Version, DateTimeOffset ObservedAt);

public sealed class ProductConfigurationClient(IHttpClientFactory factory, IConfiguration configuration, ILogger<ProductConfigurationClient> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> RefreshFailed = LoggerMessage.Define(LogLevel.Warning, new EventId(4201, "ProductConfigurationRefreshFailed"), "Central product configuration refresh failed; retaining the last validated configuration.");
    private ProductConfigurationSnapshot? snapshot;
    public ProductConfigurationSnapshot? Snapshot => Volatile.Read(ref snapshot);
    public bool Configured => !string.IsNullOrEmpty(configuration["ProductConfiguration:Url"]);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = configuration["ProductConfiguration:Url"];
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw new InvalidOperationException("ProductConfiguration:Url must be an absolute HTTPS URL.");
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("ProductConfiguration:PollSeconds", 60), 30, 600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var response = await factory.CreateClient("product-configuration").GetAsync(uri, stoppingToken);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("Central product configuration is unavailable.");
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stoppingToken));
                var next = Validate(document.RootElement, "realtime", Snapshot);
                Volatile.Write(ref snapshot, next);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
            { RefreshFailed(logger, null); }
            await Task.Delay(interval, stoppingToken);
        }
    }
    public static ProductConfigurationSnapshot Validate(JsonElement root, string productId, ProductConfigurationSnapshot? previous = null)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("productId", out var product) || product.ValueKind != JsonValueKind.String || product.GetString() != productId ||
            !root.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.String || revision.GetString() is not { Length: 64 } digest || digest.Any(c => !char.IsAsciiHexDigit(c)) ||
            !root.TryGetProperty("version", out var versionValue) || versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt64(out var version) || version < 1 ||
            !root.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object ||
            !pricing.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString()) ||
            !root.TryGetProperty("configuration", out var config) || config.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("registrationRequired", out var registration) || registration.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid central product configuration.");
        if (pricing.GetProperty("model").GetString() != "free-registration" || !pricing.TryGetProperty("amountCents", out var amount) || amount.ValueKind != JsonValueKind.Number || !amount.TryGetInt64(out var cents) || cents != 0 || !pricing.TryGetProperty("currency", out var currency) || currency.ValueKind != JsonValueKind.String || currency.GetString() != "USD") throw new InvalidDataException("RealtimeStack registration must remain free.");
        if (previous is not null && (version < previous.Version || digest == previous.Revision && !JsonElement.DeepEquals(root, previous.Document))) throw new InvalidDataException("Central product version moved backwards or its revision was reused.");
        return new(root.Clone(), digest, version, DateTimeOffset.UtcNow);
    }
}
