using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using RealtimeStack.Accounts;
using Cormier.HostCheckIn;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInstallationHostCheckIn(builder.Configuration, "realtime", "RealtimeStack.Accounts", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
var access = builder.AddAccountsAccess();
builder.Services.AddHttpClient("product-configuration", client => { client.Timeout = TimeSpan.FromSeconds(10); client.MaxResponseContentBufferSize = 64 * 1024; }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("registration", client => { client.Timeout = TimeSpan.FromSeconds(15); client.MaxResponseContentBufferSize = 64 * 1024; }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<ProductConfigurationClient>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ProductConfigurationClient>());
var licensingOrigin = builder.Configuration["Licensing:Origin"]?.TrimEnd('/') ?? "";
var installationId = builder.Configuration["Registration:InstallationId"] ?? "";
var deploymentType = builder.Configuration["Registration:DeploymentType"] ?? "";
var applicationVersion = builder.Configuration["Registration:ApplicationVersion"] ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
if (access.Enabled && (!AccountsAccessOptions.SecureOrigin(licensingOrigin) || installationId.Length is < 8 or > 128 || !new[] { "kubernetes", "docker", "linux", "windows" }.Contains(deploymentType, StringComparer.Ordinal)))
    throw new InvalidOperationException("Enabled registration needs an HTTPS Licensing:Origin, stable 8–128 character installation ID and supported deployment type.");
var serviceTokenFile = builder.Configuration["Registration:ServiceTokenFile"];
var serviceToken = !string.IsNullOrWhiteSpace(serviceTokenFile) ? File.ReadAllText(serviceTokenFile).Trim() : builder.Configuration["Registration:ServiceToken"] ?? "";
if (serviceToken.Length is > 0 and < 32) throw new InvalidOperationException("Registration service tokens need at least 32 characters.");
var app = builder.Build();
if (access.Enabled) app.UseForwardedHeaders();
if (access.Enabled) { app.UseAuthentication(); app.UseAuthorization(); }
app.MapAccountsAccess(access);
app.MapGet("/health/live", () => Results.Json(new { status = "live" }));
app.MapGet("/health/ready", (HttpContext context) => { var ready = !access.Enabled || context.RequestServices.GetRequiredService<RedisTicketStore>().IsConnected; return Results.Json(new { status = ready ? "ready" : "storage-unavailable" }, statusCode: ready ? 200 : 503); });
app.MapGet("/", () => Results.Redirect("/account"));
app.MapGet("/account", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "wwwroot/account.html"), "text/html; charset=utf-8"));
app.MapGet("/api/auth/sso/account.js", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "wwwroot/account.js"), "text/javascript; charset=utf-8"));
app.MapGet("/api/product/config", (HttpContext context, ProductConfigurationClient catalog) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var current = catalog.Snapshot;
    return current is null ? Results.Json(new { configured = catalog.Configured, available = false }, statusCode: catalog.Configured ? 503 : 200)
        : Results.Json(new { configured = true, available = true, observedAt = current.ObservedAt, configuration = current.Document });
});
app.MapGet("/api/product/registration", async (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!access.Enabled || !AccountsAccess.ValidIdentity(context.User, access.Authority)) return Results.Unauthorized();
    var store = context.RequestServices.GetRequiredService<RedisTicketStore>();
    var value = await store.ReadRegistrationAsync(installationId);
    if (value is not null)
    {
        using var receipt = JsonDocument.Parse(value);
        if (receipt.RootElement.GetProperty("subject").GetString() == context.User.FindFirstValue("sub")) return Results.Json(receipt.RootElement.Clone());
    }
    return Results.Json(new { productId = "realtime", installationId, registered = false, pricing = new { model = "free-registration", amountCents = 0, currency = "USD" } });
});
app.MapPost("/api/product/registration", async (HttpContext context, IHttpClientFactory clients, IAntiforgery antiforgery) =>
{
    if (!access.Enabled || !AccountsAccess.ValidIdentity(context.User, access.Authority)) return Results.Unauthorized();
    try { await antiforgery.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "Refresh your account session before registering." }); }
    var token = await context.GetTokenAsync(AccountsAccessOptions.CookieScheme, "access_token");
    if (string.IsNullOrWhiteSpace(token)) return Results.Unauthorized();
    using var request = new HttpRequestMessage(HttpMethod.Post, licensingOrigin + "/v1/account/products/realtime/registration");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    request.Content = JsonContent.Create(new { installationId, deploymentType, application = "RealtimeStack.Gateway", applicationVersion });
    try
    {
        using var response = await clients.CreateClient("registration").SendAsync(request, context.RequestAborted);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) return Results.Json(new { error = "Sign in again to renew registration authorization.", loginUrl = access.PublicUrl + "/api/auth/sso/login" }, statusCode: 401);
        if (!response.IsSuccessStatusCode) return Results.Json(new { error = "Central registration is unavailable. No payment was requested." }, statusCode: 503);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.RequestAborted));
        var receipt = RegistrationReceipt.Validate(document.RootElement, context.User.FindFirstValue("sub")!, installationId);
        var store = context.RequestServices.GetRequiredService<RedisTicketStore>();
        await store.WriteRegistrationAsync(installationId, JsonSerializer.Serialize(receipt, JsonSerializerOptions.Web));
        return Results.Json(receipt);
    }
    catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
    { return Results.Json(new { error = "Central registration could not be verified. Retry with the same installation ID." }, statusCode: 503); }
});
app.MapGet("/internal/product/registration", async (HttpContext context) =>
{
    if (!access.Enabled || serviceToken.Length == 0) return Results.NotFound();
    var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
    var expected = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + serviceToken));
    if (!CryptographicOperations.FixedTimeEquals(supplied, expected)) return Results.Unauthorized();
    var store = context.RequestServices.GetRequiredService<RedisTicketStore>();
    var value = await store.ReadRegistrationAsync(installationId);
    return Results.Json(new { productId = "realtime", installationId, registered = value is not null, pricing = new { model = "free-registration", amountCents = 0, currency = "USD" } });
});
app.Run();

public partial class Program;
