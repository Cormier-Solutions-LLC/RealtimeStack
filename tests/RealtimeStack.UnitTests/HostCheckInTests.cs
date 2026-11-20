using System.Net;
using System.Text;
using System.Text.Json;
using Cormier.HostCheckIn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace RealtimeStack.UnitTests;

public class HostCheckInTests
{
    [Fact] public async Task HostCheckInUsesDedicatedCredentialAndPreservesSuccessOnFailure()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["CORMIER_LICENSE_INSTALLATION_ID"] = "kubernetes:production:realtime", ["CORMIER_LICENSE_AGENT_TOKEN"] = new string('x', 32), ["CORMIER_LICENSE_INSTANCE_ID"] = "gateway:pod-fixture", ["CORMIER_LICENSE_SERVER_URL"] = "https://licensing.example.org", ["KUBERNETES_SERVICE_HOST"] = "127.0.0.1", ["POD_NAMESPACE"] = "fixture-namespace", ["POD_UID"] = "pod-fixture", ["DATABASE_URL"] = "must-not-send", ["Sso:ClientSecret"] = "must-not-send" }).Build();
        var options = HostCheckInOptions.Read(configuration, "realtime", "RealtimeStack.Gateway", "2.0.3"); var clients = new ClientFactory();
        using var client = new InstallationHostCheckIn(options, clients, NullLogger<InstallationHostCheckIn>.Instance); await client.CheckInAsync();
        Assert.False(client.Snapshot.Stale); Assert.NotNull(client.Snapshot.LastSuccess); Assert.Equal("Bearer " + new string('x', 32), clients.Authorization);
        using var body = JsonDocument.Parse(clients.Body!); Assert.Equal("realtime", body.RootElement.GetProperty("productId").GetString()); Assert.Equal("gateway:pod-fixture", body.RootElement.GetProperty("instanceId").GetString()); Assert.Equal("container", body.RootElement.GetProperty("host").GetProperty("resourceScope").GetString()); Assert.True(body.RootElement.GetProperty("host").GetProperty("memoryBytes").GetInt64() > 0); Assert.DoesNotContain("must-not-send", clients.Body, StringComparison.Ordinal);
        var success = client.Snapshot.LastSuccess; clients.Fail = true; await client.CheckInAsync(); Assert.Equal(success, client.Snapshot.LastSuccess); Assert.Equal("failed", client.HealthStatus);
    }
    [Fact] public void DedicatedAgentConfigurationCannotFallBackToSsoCredentials()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Sso:ClientSecret"] = new string('x',32) }).Build(); Assert.False(HostCheckInOptions.Read(configuration,"realtime","RealtimeStack.Accounts","2.0.3").Configured);
    }
    private sealed class ClientFactory : IHttpClientFactory
    {
        public bool Fail { get; set; } public string? Body { get; set; } public string? Authorization { get; set; }
        public HttpClient CreateClient(string name) => new(new Handler(this));
    }
    private sealed class Handler(ClientFactory fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            fixture.Body = await request.Content!.ReadAsStringAsync(cancellationToken); fixture.Authorization = request.Headers.Authorization?.ToString();
            using var body = JsonDocument.Parse(fixture.Body); var result = JsonSerializer.Serialize(new { installationId = body.RootElement.GetProperty("installationId").GetString(), instanceId = body.RootElement.GetProperty("instanceId").GetString(), receivedAt = DateTimeOffset.UtcNow, nextCheckInSeconds = 60 });
            return new HttpResponseMessage(fixture.Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }
}
