using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RealtimeStack.Gateway;

namespace RealtimeStack.UnitTests;

public class GatewayRegistrationTests
{
    [Fact]
    public void DisabledRegistrationPreservesDefaultReadinessAndDoesNotReadSecrets()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Registration:ServiceTokenFile"] = "missing-fixture-secret" }).Build();
        var options = GatewayRegistrationOptions.Read(configuration);
        using var monitor = new GatewayRegistrationMonitor(options, new FixtureClientFactory(), NullLogger<GatewayRegistrationMonitor>.Instance);
        Assert.False(monitor.Required); Assert.True(monitor.Ready); Assert.Equal("not-required", monitor.HealthStatus);
    }
    [Fact]
    public async Task RequiredRegistrationStaysUnreadyUntilAValidFreeReceipt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "realtime-registration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options(folder); var clients = new FixtureClientFactory();
            using var monitor = new GatewayRegistrationMonitor(options, clients, NullLogger<GatewayRegistrationMonitor>.Instance);
            Assert.False(monitor.Ready); Assert.Equal("setup-required", monitor.HealthStatus);
            foreach (var invalid in new[] { Response(registered: false), Response(installation: "another-installation"), Response(cents: 1), Response().Replace("\"amountCents\":0,", "", StringComparison.Ordinal), "{}", "null" })
            { clients.Body = invalid; await monitor.PollAsync(); Assert.False(monitor.Ready); }
            clients.Body = Response(); await monitor.PollAsync(); Assert.True(monitor.Ready); Assert.True(File.Exists(options.CacheFile));
            Assert.Equal("Bearer " + options.ServiceToken, clients.Authorization);
            clients.Status = HttpStatusCode.ServiceUnavailable; await monitor.PollAsync(); Assert.True(monitor.Ready);
            clients.Body = Response(registered: false); clients.Status = HttpStatusCode.OK; await monitor.PollAsync(); Assert.True(monitor.Registered); Assert.False(monitor.Ready);
            clients.Body = Response(); await monitor.PollAsync(); Assert.True(monitor.Ready);
            using var restarted = new GatewayRegistrationMonitor(options, clients, NullLogger<GatewayRegistrationMonitor>.Instance);
            Assert.True(restarted.Registered); Assert.False(restarted.Ready); await restarted.PollAsync(); Assert.True(restarted.Ready); Assert.Equal("registered", restarted.HealthStatus);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
    [Fact]
    public async Task CacheIsBoundToServiceTokenEndpointAndInstallation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "realtime-registration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options(folder); var clients = new FixtureClientFactory { Body = Response() };
            using var monitor = new GatewayRegistrationMonitor(options, clients, NullLogger<GatewayRegistrationMonitor>.Instance); await monitor.PollAsync();
            using var rotated = new GatewayRegistrationMonitor(options with { ServiceToken = new string('y', 32) }, clients, NullLogger<GatewayRegistrationMonitor>.Instance); Assert.False(rotated.Ready);
            using var moved = new GatewayRegistrationMonitor(options with { StatusUrl = new Uri("https://other.example.org/internal/product/registration") }, clients, NullLogger<GatewayRegistrationMonitor>.Instance); Assert.False(moved.Ready);
            using var another = new GatewayRegistrationMonitor(options with { InstallationId = "other-installation" }, clients, NullLogger<GatewayRegistrationMonitor>.Instance); Assert.False(another.Ready);
            var bytes = await File.ReadAllTextAsync(options.CacheFile); await File.WriteAllTextAsync(options.CacheFile, bytes.Replace("installation-1", "installation-2", StringComparison.Ordinal));
            using var tampered = new GatewayRegistrationMonitor(options, clients, NullLogger<GatewayRegistrationMonitor>.Instance); Assert.False(tampered.Ready);
            await File.WriteAllTextAsync(options.CacheFile, "{}"); using var malformed = new GatewayRegistrationMonitor(options, clients, NullLogger<GatewayRegistrationMonitor>.Instance); Assert.False(malformed.Ready);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
    [Fact]
    public void RequiredRegistrationRejectsUnsafeOrIncompleteConfiguration()
    {
        var values = new Dictionary<string, string?> { ["Registration:Required"] = "true", ["Registration:AccountIssuer"] = "https://access.example.org", ["Registration:StatusUrl"] = "http://accounts.example.org/internal/product/registration", ["Registration:InstallationId"] = "installation-1", ["Registration:ServiceToken"] = new string('x', 32), ["Registration:CacheFile"] = Path.Combine(Path.GetTempPath(), "registration-fixture.json") };
        Assert.Throws<InvalidOperationException>(() => GatewayRegistrationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
        values["Registration:AllowInsecureHttp"] = "true"; Assert.True(GatewayRegistrationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).Required);
        values["Registration:StatusUrl"] = "https://accounts.example.org/internal/product/registration?secret=query";
        Assert.Throws<InvalidOperationException>(() => GatewayRegistrationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }
    private static GatewayRegistrationOptions Options(string folder) => new(true, new Uri("https://accounts.example.org/internal/product/registration"), "installation-1", new string('x', 32), Path.Combine(folder, "registration.json"), TimeSpan.FromMinutes(1));
    private static string Response(bool registered = true, string installation = "installation-1", int cents = 0) => System.Text.Json.JsonSerializer.Serialize(new { productId = "realtime", installationId = installation, registered, accountEnabled = true, accountStateCheckedAt = DateTimeOffset.UtcNow, pricing = new { model = "free-registration", amountCents = cents, currency = "USD" } });
    private sealed class FixtureClientFactory : IHttpClientFactory
    {
        public string Body { get; set; } = Response(registered: false);
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Authorization { get; set; }
        public HttpClient CreateClient(string name) => new(new FixtureHandler(this));
    }
    private sealed class FixtureHandler(FixtureClientFactory fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { fixture.Authorization = request.Headers.Authorization?.ToString(); return Task.FromResult(new HttpResponseMessage(fixture.Status) { Content = new StringContent(fixture.Body, Encoding.UTF8, "application/json") }); }
    }
}
