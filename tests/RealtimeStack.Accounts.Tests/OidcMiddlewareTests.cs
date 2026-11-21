using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cormier.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RealtimeStack.Accounts;
using StackExchange.Redis;

namespace RealtimeStack.Accounts.Tests;

public sealed class OidcMiddlewareTests
{
    [RedisAvailableFact]
    public async Task SignedCodePkceCallbackPersistsIssuerStampAndGodAcrossAccountReplicas()
    {
        using var issuer = new Issuer();
        await using var fixture = await Fixture.Start(issuer);
        var cookies = new BrowserCookies();
        var callback = await fixture.Begin(cookies);
        using var response = await fixture.Send(fixture.First, cookies, callback);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, "Callback failed: " + await response.Content.ReadAsStringAsync() + "; verified-principal metadata: " + issuer.PrincipalMetadata);
        Assert.Equal(Issuer.PublicOrigin + "/account", response.Headers.Location?.OriginalString);
        Assert.True(issuer.PkceVerified);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-RealtimeStack.Accounts=", StringComparison.Ordinal));
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-access-token", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain(issuer.Subject, cookie, StringComparison.Ordinal);
        using var session = await fixture.Session(fixture.Second, cookies);
        Assert.True(session.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.True(session.RootElement.GetProperty("registrationAllowed").GetBoolean());
        Assert.True(session.RootElement.GetProperty("user").GetProperty("god").GetBoolean());
        Assert.Equal(issuer.Subject, session.RootElement.GetProperty("user").GetProperty("subject").GetString());

        // A reset between callback and later requests invalidates the original stamp.
        issuer.StateStamp = new string('b', 64);
        fixture.Second.Services.GetRequiredService<AccountStateClient>().Clear(issuer.Subject);
        using var invalidated = await fixture.Session(fixture.Second, cookies);
        Assert.False(invalidated.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [RedisAvailableTheory]
    [InlineData("strip-issuer")]
    [InlineData("missing-acr")]
    [InlineData("unverified")]
    [InlineData("disabled")]
    [InlineData("stamp-change")]
    [InlineData("outage")]
    public async Task CallbackAuthorityFailureHandles401WithoutIssuingOrPersistingAnAccountCookie(string failure)
    {
        using var issuer = new Issuer { Failure = failure };
        await using var fixture = await Fixture.Start(issuer, stripIssuer: failure == "strip-issuer");
        var cookies = new BrowserCookies();
        using var response = await fixture.Send(fixture.First, cookies, await fixture.Begin(cookies));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var headers) && headers.Any(value => value.StartsWith("__Host-RealtimeStack.Accounts=", StringComparison.Ordinal)));
        Assert.Empty(await fixture.Keys());
        using var session = await fixture.Session(fixture.Second, cookies);
        Assert.False(session.RootElement.GetProperty("authenticated").GetBoolean());
    }

    private sealed class BrowserCookies
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public string Header => string.Join("; ", values.Select(pair => pair.Key + "=" + pair.Value));
        public void Capture(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) return;
            foreach (var header in headers)
            {
                var pair = header.Split(';', 2)[0].Split('=', 2);
                if (pair.Length != 2) continue;
                if (pair[1].Length == 0) values.Remove(pair[0]); else values[pair[0]] = pair[1];
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication First { get; init; }
        public required WebApplication Second { get; init; }
        public required string Prefix { get; init; }
        public required ConnectionMultiplexer Redis { get; init; }
        public required Issuer Authority { get; init; }
        private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

        public static async Task<Fixture> Start(Issuer issuer, bool stripIssuer = false)
        {
            var endpoint = Environment.GetEnvironmentVariable("REDIS_TEST_ENDPOINT")!;
            var prefix = "accounts-oidc-fixture:" + Guid.NewGuid().ToString("N") + ":";
            var protection = new EphemeralDataProtectionProvider();
            async Task<WebApplication> Host()
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sso:Enabled"] = "true", ["Sso:Authority"] = Issuer.Origin, ["Sso:PublicUrl"] = Issuer.PublicOrigin,
                    ["Sso:ClientId"] = "realtime", ["Sso:ClientSecret"] = Issuer.Secret, ["Licensing:Origin"] = "https://licensing.example.test",
                    ["Sso:RedisConnection"] = endpoint, ["Sso:RedisPrefix"] = prefix, ["Registration:RequireGod"] = "true"
                });
                var access = builder.AddAccountsAccess();
                builder.Services.AddSingleton<IDataProtectionProvider>(protection);
                builder.Services.AddSingleton(_ => new AccountStateClient(Issuer.Origin, "realtime", Issuer.Secret, issuer.Handler()));
                builder.Services.PostConfigure<OpenIdConnectOptions>(AccountsAccessOptions.OidcScheme, options =>
                {
                    var backchannel = new HttpClient(issuer.Handler());
                    options.Backchannel = backchannel;
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(Issuer.Origin + "/.well-known/openid-configuration",
                        new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(backchannel) { RequireHttps = true });
                    if (stripIssuer) options.ClaimActions.DeleteClaim("iss");
                    var received = options.Events.OnTicketReceived;
                    options.Events.OnTicketReceived = async context =>
                    {
                        issuer.PrincipalMetadata = JsonSerializer.Serialize(new { validIdentity = AccountsAccess.ValidIdentity(context.Principal, Issuer.Origin), signedRevision = context.Principal is not null && AccountsAccess.CredentialStamp(context.Principal) is not null,
                            claims = context.Principal?.Claims.Where(claim => claim.Type is "iss" or "god" or "email_verified").Select(claim => new { claim.Type, claim.ValueType, issuerMatches = claim.Type == "iss" && claim.Value == Issuer.Origin, booleanValue = claim.Type == "iss" ? null : claim.Value }) });
                        await received(context);
                    };
                });
                var app = builder.Build();
                app.UseForwardedHeaders(); app.UseAuthentication(); app.UseAuthorization();
                app.MapAccountsAccess(access);
                await app.StartAsync(); return app;
            }
            return new Fixture { First = await Host(), Second = await Host(), Prefix = prefix, Redis = await ConnectionMultiplexer.ConnectAsync(endpoint), Authority = issuer };
        }

        public async Task<HttpResponseMessage> Send(WebApplication app, BrowserCookies cookies, string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single().TrimEnd('/') + path);
            request.Headers.Host = "realtime.example.test";
            request.Headers.Add("X-Forwarded-Proto", "https");
            if (cookies.Header.Length > 0) request.Headers.Add("Cookie", cookies.Header);
            var response = await client.SendAsync(request); cookies.Capture(response); return response;
        }
        public async Task<string> Begin(BrowserCookies cookies)
        {
            using var response = await Send(First, cookies, "/api/auth/sso/login");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            Assert.Equal("code", query["response_type"]); Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("realtime", query["client_id"]); Assert.Equal(Issuer.PublicOrigin + AccountsAccessOptions.Callback, query["redirect_uri"]);
            Authority.Nonce = query["nonce"]!; Authority.Challenge = query["code_challenge"]!;
            return AccountsAccessOptions.Callback + "?code=fixture-code&state=" + Uri.EscapeDataString(query["state"]!);
        }
        public async Task<JsonDocument> Session(WebApplication app, BrowserCookies cookies)
        {
            using var response = await Send(app, cookies, "/api/auth/sso/session");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        public Task<RedisKey[]> Keys() => Task.FromResult(Redis.GetEndPoints().SelectMany(endpoint => Redis.GetServer(endpoint).Keys(pattern: Prefix + "*")).Distinct().ToArray());
        public async ValueTask DisposeAsync()
        {
            await First.DisposeAsync(); await Second.DisposeAsync();
            var keys = await Keys(); if (keys.Length > 0) await Redis.GetDatabase().KeyDeleteAsync(keys);
            Redis.Dispose(); client.Dispose();
        }
    }

    private sealed class Issuer : IDisposable
    {
        public const string Origin = "https://access.example.test", PublicOrigin = "https://realtime.example.test", Secret = "fixture-client-secret-0123456789abcdef";
        private static readonly string[] ResponseTypes = ["code"], SubjectTypes = ["public"], SigningAlgorithms = ["RS256"], ClientAuthentication = ["client_secret_post"], ChallengeMethods = ["S256"];
        private readonly RSA key = RSA.Create(2048);
        public string Subject { get; } = "usr-synthetic-owner";
        public string? Failure { get; init; }
        public string StateStamp { get; set; } = new('a', 64);
        public string Nonce { get; set; } = "";
        public string Challenge { get; set; } = "";
        public bool PkceVerified { get; private set; }
        public string PrincipalMetadata { get; set; } = "ticket event not reached";
        public HttpMessageHandler Handler() => new Transport(this);
        private sealed class Transport(Issuer issuer) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await issuer.Reply(request, cancellationToken);
                response.RequestMessage = request;
                return response;
            }
        }
        private async Task<HttpResponseMessage> Reply(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/.well-known/openid-configuration") return Response(new { issuer = Origin, authorization_endpoint = Origin + "/authorize", token_endpoint = Origin + "/token", jwks_uri = Origin + "/jwks", response_types_supported = ResponseTypes, subject_types_supported = SubjectTypes, id_token_signing_alg_values_supported = SigningAlgorithms, token_endpoint_auth_methods_supported = ClientAuthentication, code_challenge_methods_supported = ChallengeMethods });
            if (path == "/jwks")
            {
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(key) { KeyId = "synthetic-fixture" });
                return Response(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, n = jwk.N, e = jwk.E, use = "sig", alg = "RS256" } } });
            }
            if (path == "/api/sso/account-state")
            {
                Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
                Assert.Equal("realtime:" + Secret, Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
                if (Failure == "outage") return new(HttpStatusCode.ServiceUnavailable);
                return Response(new { accounts = new[] { new { subject = Subject, enabled = Failure != "disabled", email = "owner@example.test", name = "Synthetic owner", emailVerified = true, credentialStamp = Failure == "stamp-change" ? new string('b', 64) : StateStamp } } });
            }
            if (path != "/token") return new(HttpStatusCode.NotFound);
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(Secret, form["client_secret"]); Assert.Equal("realtime", form["client_id"]);
            PkceVerified = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]!))) == Challenge;
            Assert.True(PkceVerified);
            var claims = new Dictionary<string, object> { ["sub"] = Subject, ["email"] = "owner@example.test", ["name"] = "Synthetic owner", ["email_verified"] = Failure != "unverified", ["god"] = true, ["nonce"] = Nonce };
            if (Failure != "missing-acr") claims["acr"] = "urn:cormier:access:credential:" + new string('a', 64);
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = Origin, Audience = "realtime", IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow.AddSeconds(-1), Expires = DateTime.UtcNow.AddMinutes(5), Claims = claims, SigningCredentials = new(new RsaSecurityKey(key) { KeyId = "synthetic-fixture" }, SecurityAlgorithms.RsaSha256) });
            return Response(new { access_token = "fixture-access-token", token_type = "Bearer", expires_in = 300, id_token = token });
        }
        private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        public void Dispose() => key.Dispose();
    }
}

public sealed class RedisAvailableTheoryAttribute : TheoryAttribute
{
    public RedisAvailableTheoryAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_TEST_ENDPOINT"))) Skip = "Set REDIS_TEST_ENDPOINT to an isolated Redis instance to validate real OIDC session persistence."; }
}
