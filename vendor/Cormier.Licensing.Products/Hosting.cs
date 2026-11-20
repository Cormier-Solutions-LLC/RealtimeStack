using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cormier.Licensing.Products;

/// <summary>Registers one client per host without changing application authorization or readiness.</summary>
public static class ProductLicensingHosting
{
    /// <summary>Configure CORMIER_LICENSE_* settings and the exact registered application entitlement.</summary>
    public static IServiceCollection AddCormierProductLicensing(this IServiceCollection services, string application, string version)
    {
        services.AddHttpClient("CormierProductLicensing");
        services.AddSingleton(provider => new LicensingClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("CormierProductLicensing"),
            LicensingOptions.FromEnvironment(application, version)));
        services.AddHostedService<ProductLicensingService>();
        return services;
    }
}

internal sealed class ProductLicensingService(LicensingClient client, ILogger<ProductLicensingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Central licensing client started; configured: {Configured}", client.Configured);
        client.Log = (level, message) => logger.Log(level == "warn" ? LogLevel.Warning : LogLevel.Information,
            "Central licensing: {Message}", message);
        await client.StartAsync(stoppingToken).ConfigureAwait(false);
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await client.StopAsync().ConfigureAwait(false);
    }
}
