using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RealtimeStack.Accounts;

namespace RealtimeStack.Accounts.Tests;

public class AccountsTests
{
    [Fact] public void RedisCredentialsRemainSeparateFromEndpointAndSupportSentinel()
    {
        var values = new Dictionary<string, string?> { ["Sso:RedisEndpoint"] = "redis.example.org:26379,sentinelUser=default", ["Sso:RedisUser"] = "accounts", ["Sso:RedisPassword"] = "fixture,with;separators", ["Sso:RedisSentinelPassword"] = "sentinel-fixture", ["Sso:RedisSentinelServiceName"] = "test-master" };
        var options = RedisTicketStore.ReadRedisOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Equal("accounts", options.User); Assert.Equal("fixture,with;separators", options.Password); Assert.Equal("sentinel-fixture", options.SentinelPassword); Assert.Equal("test-master", options.ServiceName); Assert.Equal("default", options.SentinelUser);
    }
    private const string Issuer = "https://access.example.org";
    [Fact] public void IdentityRejectsEmailOnlyBindingAndUnsignedOwnerTypes()
    {
        var identity = new ClaimsIdentity([new Claim("iss", Issuer), new Claim("email", "owner@example.org"), new Claim("email_verified", "true", ClaimValueTypes.Boolean)], "oidc");
        var user = new ClaimsPrincipal(identity); Assert.False(AccountsAccess.ValidIdentity(user, Issuer));
        identity.AddClaim(new Claim("sub", "usr-stable")); Assert.True(AccountsAccess.ValidIdentity(user, Issuer));
        Assert.False(AccountsAccess.ValidIdentity(user, "https://other.example.org"));
        identity.AddClaim(new Claim("god", "true")); Assert.False(AccountsAccess.IsGod(user));
        identity.AddClaim(new Claim("god", "true", ClaimValueTypes.Boolean)); Assert.True(AccountsAccess.IsGod(user));
    }
    [Fact] public void OidcRequiresCodePkceIssuerSignatureAndLicensingScope()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sso:Enabled"] = "true", ["Sso:Authority"] = Issuer, ["Sso:PublicUrl"] = "https://realtime.example.org", ["Sso:ClientSecret"] = new string('x', 32), ["Licensing:Origin"] = "https://licensing.example.org" });
        builder.AddAccountsAccess(); using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AccountsAccessOptions.OidcScheme);
        Assert.Equal("code", options.ResponseType); Assert.True(options.UsePkce); Assert.True(options.RequireHttpsMetadata); Assert.False(options.GetClaimsFromUserInfoEndpoint);
        Assert.True(options.TokenValidationParameters.ValidateIssuer); Assert.True(options.TokenValidationParameters.ValidateAudience); Assert.True(options.TokenValidationParameters.RequireSignedTokens);
        Assert.Contains("licensing", options.Scope); Assert.True(options.SaveTokens);
        Assert.DoesNotContain(options.ClaimActions, action => action.ClaimType == "acr");
        Assert.DoesNotContain(options.ClaimActions, action => action.ClaimType == "iss");
    }
    [Fact] public void SignedCredentialStampMustBeSingleStrictAcrAndSurvivesCentralProfileRefresh()
    {
        var identity=new ClaimsIdentity([new Claim("sub","usr-fixture")],"oidc");var principal=new ClaimsPrincipal(identity);
        Assert.Null(AccountsAccess.CredentialStamp(principal));
        var valid=new Claim("acr","urn:cormier:access:credential:"+new string('a',64));identity.AddClaim(valid);Assert.Equal(new string('a',64),AccountsAccess.CredentialStamp(principal));
        var refreshed=AccountsAccess.WithCentralProfile(principal,new("usr-fixture",true,"fixture@example.test","Fixture",true,new string('b',64),DateTimeOffset.UtcNow));Assert.Equal(new string('a',64),AccountsAccess.CredentialStamp(refreshed));
        identity.AddClaim(valid);Assert.Null(AccountsAccess.CredentialStamp(principal));
    }
    [Theory] [InlineData("subject-swap", "installation-1", 0)] [InlineData("usr-stable", "installation-swap", 0)] [InlineData("usr-stable", "installation-1", 1)]
    public void RegistrationRejectsAccountInstallationAndPriceChanges(string subject, string installation, int cents)
    {
        using var document = JsonDocument.Parse(Receipt(subject, installation, cents));
        Assert.Throws<InvalidDataException>(() => RegistrationReceipt.Validate(document.RootElement, "usr-stable", "installation-1"));
    }
    [Fact] public void RegistrationAcceptsPermanentFreeReceipt()
    {
        using var document = JsonDocument.Parse(Receipt("usr-stable", "installation-1", 0));
        var receipt = RegistrationReceipt.Validate(document.RootElement, "usr-stable", "installation-1"); Assert.Null(receipt.ExpiresAt); Assert.Equal(0, receipt.Pricing.AmountCents);
    }
    [Fact] public void CatalogRejectsPaidOrMalformedPolicies()
    {
        using var document = JsonDocument.Parse(Catalog()); var snapshot = ProductConfigurationClient.Validate(document.RootElement, "realtime"); Assert.Equal(1, snapshot.Version);
        foreach (var invalid in new[] { Catalog().Replace("\"amountCents\":0", "\"amountCents\":1", StringComparison.Ordinal), Catalog().Replace("\"amountCents\":0", "\"amountCents\":\"0\"", StringComparison.Ordinal), Catalog().Replace("\"version\":1", "\"version\":\"1\"", StringComparison.Ordinal) })
        { using var changed = JsonDocument.Parse(invalid); Assert.Throws<InvalidDataException>(() => ProductConfigurationClient.Validate(changed.RootElement, "realtime", snapshot)); }
    }
    private static string Receipt(string subject, string installationId, int amountCents) => JsonSerializer.Serialize(new { productId = "realtime", subject, installationId, status = "registered", registeredAt = DateTimeOffset.UtcNow, expiresAt = (string?)null, pricing = new { model = "free-registration", amountCents, currency = "USD" } });
    private static string Catalog() => JsonSerializer.Serialize(new { productId = "realtime", revision = new string('a', 64), version = 1, pricing = new { model = "free-registration", amountCents = 0, currency = "USD" }, configuration = new { }, registrationRequired = true });
}
