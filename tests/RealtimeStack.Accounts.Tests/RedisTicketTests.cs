using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace RealtimeStack.Accounts.Tests;

public sealed class RedisAvailableFactAttribute : FactAttribute
{
    public RedisAvailableFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_TEST_ENDPOINT"))) Skip = "Set REDIS_TEST_ENDPOINT to an isolated Redis instance to validate encrypted persistent sessions."; }
}
public class RedisTicketTests
{
    [RedisAvailableFact]
    public async Task TokensStayEncryptedAndRegistrationSurvivesStoreRestart()
    {
        var endpoint = Environment.GetEnvironmentVariable("REDIS_TEST_ENDPOINT")!;
        var prefix = "accounts-test:" + Guid.NewGuid().ToString("N") + ":";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sso:RedisConnection"] = endpoint, ["Sso:RedisPrefix"] = prefix }).Build();
        var protection = new EphemeralDataProtectionProvider();
        using var redis = await ConnectionMultiplexer.ConnectAsync(endpoint); var db = redis.GetDatabase();
        using var first = new RedisTicketStore(configuration, protection);
        var properties = new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) };
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "private-oauth-token-fixture" }]);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "usr-123")], "oidc")), properties, AccountsAccessOptions.CookieScheme);
        var handle = await first.StoreAsync(ticket);
        var key = prefix + "ticket:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(handle)));
        var registrationKey = prefix + "registration:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("installation-1")));
        try
        {
            Assert.Equal(64, handle.Length); Assert.DoesNotContain("private-oauth", handle, StringComparison.Ordinal);
            var raw = (byte[])(await db.StringGetAsync(key))!; Assert.False(Encoding.UTF8.GetString(raw).Contains("private-oauth", StringComparison.Ordinal));
            await first.WriteRegistrationAsync("installation-1", "{\"subject\":\"usr-123\"}");
            using var restarted = new RedisTicketStore(configuration, protection);
            var restored = await restarted.RetrieveAsync(handle); Assert.NotNull(restored); Assert.Equal("private-oauth-token-fixture", restored.Properties.GetTokenValue("access_token"));
            Assert.Equal("{\"subject\":\"usr-123\"}", await restarted.ReadRegistrationAsync("installation-1"));
            await db.StringSetAsync(key, "tampered"); Assert.Null(await restarted.RetrieveAsync(handle));
            await restarted.RemoveAsync(handle); Assert.Null(await first.RetrieveAsync(handle));
        }
        finally { await db.KeyDeleteAsync(key); await db.KeyDeleteAsync(registrationKey); }
    }
}
