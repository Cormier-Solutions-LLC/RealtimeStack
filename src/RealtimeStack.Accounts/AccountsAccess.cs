using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Cormier.Access;

namespace RealtimeStack.Accounts;

public sealed record AccountsAccessOptions(bool Enabled, string Authority, string ClientId, string ClientSecret, string PublicUrl)
{
    public const string CookieScheme = "RealtimeStack.Accounts";
    public const string OidcScheme = "RealtimeStack.Oidc";
    public const string Callback = "/api/auth/sso/callback";
    public static AccountsAccessOptions Read(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>("Sso:Enabled");
        var secretFile = configuration["Sso:ClientSecretFile"];
        var result = new AccountsAccessOptions(enabled, configuration["Sso:Authority"]?.TrimEnd('/') ?? "", configuration["Sso:ClientId"] ?? "realtime",
            !string.IsNullOrEmpty(secretFile) ? File.ReadAllText(secretFile).Trim() : configuration["Sso:ClientSecret"] ?? "", configuration["Sso:PublicUrl"]?.TrimEnd('/') ?? "");
        if (enabled && (!SecureOrigin(result.Authority) || !SecureOrigin(result.PublicUrl) || result.ClientId.Length is < 1 or > 200 || result.ClientSecret.Length < 32))
            throw new InvalidOperationException("Enabled SSO requires HTTPS authority/public origins, client ID, and a Secret-backed client secret of at least 32 characters.");
        return result;
    }
    public static bool SecureOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    public override string ToString() => $"AccountsAccessOptions {{ Enabled = {Enabled}, Credentials = [redacted] }}";
}

public static class AccountsAccess
{
    public const string CredentialStampKey = "cormier-access.credential-stamp";
    public static AccountsAccessOptions AddAccountsAccess(this WebApplicationBuilder builder)
    {
        var access = AccountsAccessOptions.Read(builder.Configuration);
        builder.Services.AddSingleton(access);
        builder.Services.AddAntiforgery(options => { options.HeaderName = "X-RealtimeStack-CSRF"; options.Cookie.SecurePolicy = CookieSecurePolicy.Always; options.Cookie.SameSite = SameSiteMode.Lax; });
        if (!access.Enabled) return access;
        builder.Services.AddSingleton(_ => new AccountStateClient(access.Authority, access.ClientId, access.ClientSecret));
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto; options.ForwardLimit = 1;
            foreach (var network in builder.Configuration.GetSection("Sso:KnownProxyNetworks").Get<string[]>() ?? []) options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            foreach (var proxy in builder.Configuration.GetSection("Sso:KnownProxies").Get<string[]>() ?? []) options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
        });
        // Authentication requests carry authorization codes in their query string.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
        var dataProtection = builder.Services.AddDataProtection().SetApplicationName(builder.Configuration["Sso:ApplicationName"] ?? "RealtimeStack.Accounts");
        var keyPath = builder.Configuration["Sso:DataProtectionPath"];
        if (!string.IsNullOrWhiteSpace(keyPath)) dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(keyPath)) throw new InvalidOperationException("Production SSO requires a persistent Sso:DataProtectionPath shared by the API replicas.");
        builder.Services.AddAuthentication(options => { options.DefaultScheme = AccountsAccessOptions.CookieScheme; options.DefaultChallengeScheme = AccountsAccessOptions.OidcScheme; })
            .AddCookie(AccountsAccessOptions.CookieScheme, options =>
            {
                options.Cookie.Name = "__Host-RealtimeStack.Accounts"; options.Cookie.Path = "/"; options.Cookie.HttpOnly = true; options.Cookie.SecurePolicy = CookieSecurePolicy.Always; options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
                options.Events.OnValidatePrincipal = async context =>
                {
                    try
                    {
                        if (!ValidIdentity(context.Principal, access.Authority)) throw new AccountStateUnavailableException();
                        var principal = context.Principal!;
                        context.Properties.Items.TryGetValue(CredentialStampKey, out var stamp);
                        if (stamp is null || CredentialStamp(principal) != stamp) throw new AccountStateUnavailableException();
                        var state = await context.HttpContext.RequestServices.GetRequiredService<AccountStateClient>().ValidateAsync(
                            principal.FindFirstValue("sub")!, stamp, context.HttpContext.RequestAborted);
                        if (state is null) throw new AccountStateUnavailableException();
                        var updated = WithCentralProfile(principal, state);
                        if (principal.FindFirstValue("name") != state.Name || principal.FindFirstValue("email") != state.Email)
                        { context.ReplacePrincipal(updated); context.ShouldRenew = true; }
                    }
                    catch (AccountStateUnavailableException)
                    { context.RejectPrincipal(); await context.HttpContext.SignOutAsync(AccountsAccessOptions.CookieScheme); }
                };
            })
            .AddOpenIdConnect(AccountsAccessOptions.OidcScheme, options =>
            {
                options.SignInScheme = AccountsAccessOptions.CookieScheme; options.Authority = access.Authority; options.ClientId = access.ClientId; options.ClientSecret = access.ClientSecret;
                options.CallbackPath = AccountsAccessOptions.Callback; options.ResponseType = "code"; options.ResponseMode = "query"; options.UsePkce = true; options.MapInboundClaims = false;
                options.RequireHttpsMetadata = true; options.SaveTokens = true; options.GetClaimsFromUserInfoEndpoint = false;
                options.Scope.Clear(); options.Scope.Add("openid"); options.Scope.Add("profile"); options.Scope.Add("email"); options.Scope.Add("licensing");
                options.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified"); options.ClaimActions.MapUniqueJsonKey("god", "god");
                options.ClaimActions.Remove("acr");
                options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = access.Authority, ValidateAudience = true, ValidAudience = access.ClientId, ValidateLifetime = true, RequireSignedTokens = true, NameClaimType = "name", ClockSkew = TimeSpan.FromMinutes(1) };
                options.Events.OnRedirectToIdentityProvider = context => { context.ProtocolMessage.RedirectUri = access.PublicUrl + AccountsAccessOptions.Callback; context.ProtocolMessage.SetParameter("resource", builder.Configuration["Licensing:Origin"]); return Task.CompletedTask; };
                options.Events.OnAuthorizationCodeReceived = context => { context.TokenEndpointRequest?.SetParameter("resource", builder.Configuration["Licensing:Origin"]); return Task.CompletedTask; };
                options.Events.OnTicketReceived = async context =>
                {
                    try
                    {
                        if (!ValidIdentity(context.Principal, access.Authority)) throw new AccountStateUnavailableException();
                        var state = await context.HttpContext.RequestServices.GetRequiredService<AccountStateClient>().GetAsync(context.Principal!.FindFirstValue("sub")!, fresh: true, context.HttpContext.RequestAborted);
                        var signedStamp = CredentialStamp(context.Principal!);
                        if (!state.Enabled || !state.EmailVerified || signedStamp is null || state.CredentialStamp != signedStamp) throw new AccountStateUnavailableException();
                        context.Principal = WithCentralProfile(context.Principal!, state);
                        (context.Properties ?? throw new AccountStateUnavailableException()).Items[CredentialStampKey] = signedStamp;
                    }
                    catch (AccountStateUnavailableException) { context.Fail("An active verified central account is required."); }
                };
                options.Events.OnRemoteFailure = async context => { context.HandleResponse(); context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "Central sign-in could not be verified. Please sign in again." }); };
            });
        builder.Services.AddSingleton<RedisTicketStore>();
        builder.Services.AddOptions<CookieAuthenticationOptions>(AccountsAccessOptions.CookieScheme).Configure<RedisTicketStore>((options, store) => options.SessionStore = store);
        builder.Services.AddAuthorization();
        return access;
    }

    public static bool ValidIdentity(ClaimsPrincipal? principal, string issuer) => principal?.Identity?.IsAuthenticated == true &&
        principal.FindAll("iss").Select(c => c.Value).Distinct(StringComparer.Ordinal).SequenceEqual([issuer]) &&
        principal.FindAll("sub").Select(c => c.Value).Distinct(StringComparer.Ordinal).Count() == 1 && principal.FindFirst("sub")?.Value.Length is > 0 and <= 128 &&
        principal.FindAll("email_verified").Any(c => c.Value == "true" && c.ValueType == ClaimValueTypes.Boolean);

    public static bool IsGod(ClaimsPrincipal principal) => principal.FindAll("god").Any(c => c.Value == "true" && c.ValueType == ClaimValueTypes.Boolean);
    public static bool CanRegisterInstallation(ClaimsPrincipal? principal, string issuer, bool requireGod) => ValidIdentity(principal, issuer) && (!requireGod || IsGod(principal!));

    // Called only on the middleware-validated principal or its protected cookie.
    public static string? CredentialStamp(ClaimsPrincipal principal)
    {
        const string prefix = "urn:cormier:access:credential:";
        var claims = principal.FindAll("acr").ToArray();
        if (claims.Length != 1 || !claims[0].Value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var stamp = claims[0].Value[prefix.Length..];
        return stamp.Length == 64 && stamp.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f') ? stamp : null;
    }

    public static ClaimsPrincipal WithCentralProfile(ClaimsPrincipal principal, CentralAccountState state)
    {
        var identities = principal.Identities.Select(identity => new ClaimsIdentity(identity)).ToArray();
        var target = identities.First(identity => identity.IsAuthenticated);
        foreach (var claim in target.FindAll("email").Concat(target.FindAll("name")).ToArray()) target.RemoveClaim(claim);
        target.AddClaim(new Claim("email", state.Email ?? "")); target.AddClaim(new Claim("name", state.Name ?? ""));
        return new ClaimsPrincipal(identities);
    }

    public static void MapAccountsAccess(this WebApplication app, AccountsAccessOptions access)
    {
        app.MapGet("/api/auth/sso/session", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var authenticated = access.Enabled && ValidIdentity(context.User, access.Authority);
            return Results.Json(new { configured = access.Enabled, authenticated, loginUrl = access.Enabled ? access.PublicUrl + "/api/auth/sso/login" : null,
                registrationAllowed = authenticated && CanRegisterInstallation(context.User, access.Authority, app.Configuration.GetValue<bool>("Registration:RequireGod")),
                accountUrl = access.Enabled ? access.Authority + "/account" : null, adminAccountsUrl = authenticated && IsGod(context.User) ? access.Authority + "/admin/accounts" : null,
                user = authenticated ? new { subject = context.User.FindFirstValue("sub"), email = context.User.FindFirstValue("email"), name = context.User.FindFirstValue("name"), god = IsGod(context.User) } : null,
                csrfToken = authenticated ? antiforgery.GetAndStoreTokens(context).RequestToken : null });
        });
        app.MapGet("/api/auth/sso/login", (HttpContext context) => access.Enabled ? Results.Challenge(new AuthenticationProperties { RedirectUri = access.PublicUrl + "/account" }, [AccountsAccessOptions.OidcScheme]) : Results.NotFound());
        app.MapPost("/api/auth/sso/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!access.Enabled) return Results.NotFound();
            if (!ValidIdentity(context.User, access.Authority)) return Results.Unauthorized();
            try { await antiforgery.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "Refresh your account session before signing out." }); }
            await context.SignOutAsync(AccountsAccessOptions.CookieScheme); return Results.NoContent();
        });
    }
}
