using RealtimeStack.AspNetCore;
using RealtimeStack.Contracts;
using RealtimeStack.Gateway;
using RealtimeStack.HttpFallback;
using RealtimeStack.Redis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RealtimeStack.UnitTests;

public sealed class AspNetCoreHostingIntegrationTests
{
    [Fact]
    public async Task AddRealtimeStackGatewayPreservesAGlobalProgrammaticLoggingFilter()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(ValidConfiguration());
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter(static (_, level) => level >= LogLevel.Warning);
        builder.Services.AddRealtimeStackGateway(builder.Configuration);
        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RealtimeStack.Filtered");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public async Task AddRealtimeStackGatewayBindsConfigurationAndRegistersPublicIntegrationServices()
    {
        await using var provider = CreateServices(new Dictionary<string, string?>
        {
            ["RealtimeStack:EndpointPath"] = "/socket",
            ["RealtimeStack:TicketEndpointPath"] = "/tickets",
            ["RealtimeStack:AllowedOrigins:0"] = "https://app.example",
            ["Redis:Endpoint"] = "redis.example:6379",
        }).BuildServiceProvider();

        var options = provider.GetRequiredService<RealtimeStackOptions>();

        Assert.Equal("/socket", options.EndpointPath);
        Assert.Equal("/tickets", options.TicketEndpointPath);
        Assert.IsType<RealtimeStackSessionResolver>(provider.GetRequiredService<IRealtimeStackSessionResolver>());
        Assert.NotNull(provider.GetRequiredService<RealtimeStackWebSocketHandler>());
    }

    [Theory]
    [InlineData("RealtimeStack:EndpointPath", "relative")]
    [InlineData("RealtimeStack:EndpointPath", "/socket?query=true")]
    [InlineData("RealtimeStack:EndpointPath", "/{broken")]
    [InlineData("RealtimeStack:AllowedOrigins:0", "https://app.example/path")]
    [InlineData("RealtimeStack:AllowedOrigins:0", "ftp://app.example")]
    public void AddRealtimeStackGatewayRejectsInvalidRoutesAndOrigins(string key, string value)
    {
        var values = ValidConfiguration();
        values[key] = value;
        using var provider = CreateServices(values).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RealtimeStackOptions>>().Value);

        Assert.NotEmpty(exception.Failures);
    }

    [Theory]
    [InlineData("/realtimestack/ws", false)]
    [InlineData("/health/live", false)]
    [InlineData("/diagnostics/v1/snapshot", true)]
    public void AddRealtimeStackGatewayRejectsMetricsRouteCollisions(string metricsPath, bool diagnosticsEnabled)
    {
        var values = ValidConfiguration();
        values["Metrics:Enabled"] = "true";
        values["Metrics:Path"] = metricsPath;
        values["Diagnostics:Enabled"] = diagnosticsEnabled.ToString();
        values["Diagnostics:AuthorizationPolicy"] = "diagnostics-operator";
        using var provider = CreateServices(values).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<MetricsOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains("collide", StringComparison.Ordinal));
    }

    [Fact]
    public void AddRealtimeStackGatewayRejectsConfiguredCrossRolePolicyCollisions()
    {
        var values = ValidConfiguration();
        values["Diagnostics:Enabled"] = "true";
        values["Diagnostics:AuthorizationPolicy"] = "operator-access";
        values["Metrics:Enabled"] = "true";
        values["Metrics:AuthorizationPolicy"] = "OPERATOR-ACCESS";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddRealtimeStackGateway(builder.Configuration);

        var exception = Assert.Throws<OptionsValidationException>(
            () => builder.Build());

        Assert.Contains(exception.Failures, failure => failure.Contains("distinct authorization policies", StringComparison.Ordinal));
    }

    [Fact]
    public void BearerHelpersRejectExistingApplicationPolicies()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(options => options.AddPolicy(
            "application-administrator",
            policy => policy.RequireClaim("role", "administrator")));

        services.AddRealtimeStackDiagnosticsBearer(
            "APPLICATION-ADMINISTRATOR",
            new string('d', 32));
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains("collides", StringComparison.Ordinal));
    }

    [Fact]
    public void BearerHelpersRejectFactoryBackedApplicationPolicies()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigureOptions<AuthorizationOptions>>(_ =>
            new ConfigureOptions<AuthorizationOptions>(options => options.AddPolicy(
                "application-auditor",
                policy => policy.RequireClaim("role", "auditor"))));
        services.AddRealtimeStackMetricsBearer(
            "APPLICATION-AUDITOR",
            new string('m', 32));
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains("collides", StringComparison.Ordinal));
    }

    [Fact]
    public void BearerHelpersDetectPoliciesReplacedAfterRegistration()
    {
        var services = new ServiceCollection();
        services.AddRealtimeStackMetricsBearer("metrics-scraper", new string('m', 32));
        services.AddAuthorization(options => options.AddPolicy(
            "METRICS-SCRAPER",
            policy => policy.RequireClaim("role", "application-administrator")));
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains("collides", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("RealtimeStack:EndpointPath", "/diagnostics/v1/snapshot")]
    [InlineData("RealtimeStack:TicketEndpointPath", "/diagnostics/v1/logging/overrides")]
    public void AddRealtimeStackGatewayRejectsDiagnosticsRouteCollisions(string setting, string route)
    {
        var values = ValidConfiguration();
        values[setting] = route;
        values["Diagnostics:Enabled"] = "true";
        using var provider = CreateServices(values).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RealtimeStackOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains("diagnostics", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/diagnostics/v1/snapshot", false)]
    [InlineData("/diagnostics/v1/logging/overrides/{id}", true)]
    [InlineData("/diagnostics/v1/logging/overrides/{overrideId}", true)]
    public async Task MapRealtimeStackDiagnosticsRejectsAConflictingApplicationRoute(string route, bool delete)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Diagnostics:Enabled"] = "true",
            ["Diagnostics:AuthorizationPolicy"] = "diagnostics-operator",
            ["RealtimeStack:AllowedOrigins:0"] = "https://app.example",
            ["Redis:Endpoint"] = "redis.example:6379",
        });
        builder.Services.AddRealtimeStackGateway(builder.Configuration);
        await using var app = builder.Build();
        if (delete)
        {
            app.MapDelete(route, () => Results.Ok());
        }
        else
        {
            app.MapGet(route, () => Results.Ok());
        }

        var exception = Assert.Throws<InvalidOperationException>(() => app.MapRealtimeStackDiagnostics());

        Assert.Contains("already mapped", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapRealtimeStackDiagnosticsAllowsAnEquivalentParameterizedRouteForAnotherMethod()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Diagnostics:Enabled"] = "true",
            ["Diagnostics:AuthorizationPolicy"] = "diagnostics-operator",
            ["RealtimeStack:AllowedOrigins:0"] = "https://app.example",
            ["Redis:Endpoint"] = "redis.example:6379",
        });
        builder.Services.AddRealtimeStackGateway(builder.Configuration);
        await using var app = builder.Build();
        app.MapGet("/diagnostics/v1/logging/overrides/{overrideId}", () => Results.Ok());

        app.MapRealtimeStackDiagnostics();
    }

    [Fact]
    public void DiagnosticsNetworkAllowsIpv4MappedAddressesInsideIpv4Cidrs()
    {
        Assert.True(DiagnosticsEndpointExtensions.IsNetworkAllowed(
            System.Net.IPAddress.Parse("::ffff:192.0.2.10"),
            ["192.0.2.0/24"]));
        Assert.False(DiagnosticsEndpointExtensions.IsNetworkAllowed(
            System.Net.IPAddress.Parse("::ffff:198.51.100.10"),
            ["192.0.2.0/24"]));
    }

    [Fact]
    public void AddRealtimeStackGatewayDoesNotDuplicateHostedInfrastructure()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ValidConfiguration()).Build();
        var services = new ServiceCollection();

        services.AddRealtimeStackGateway(configuration);
        services.AddRealtimeStackGateway(configuration);

        Assert.Equal(6, services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)));
    }

    [Fact]
    public async Task CookieSessionResolutionUsesExistingSessionContractAndRejectsRevocation()
    {
        var store = new FakeSessionStore();
        var resolver = new RealtimeStackSessionResolver(store, new RealtimeStackOptions());
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "realtimestack_session=valid-session-123456";

        var resolution = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.Equal("tenant-1", resolution?.Identity.TenantId);
        Assert.Equal("user-1", resolution?.Identity.UserId);
        store.Revoked = true;
        Assert.Null(await resolver.RevalidateAsync("valid-session-123456", CancellationToken.None));
    }

    [Fact]
    public async Task AspNetCoreSessionResolutionUsesConfiguredSessionValueAcrossInstances()
    {
        var store = new FakeSessionStore();
        var options = new RealtimeStackOptions
        {
            SessionSource = RealtimeStackSessionSource.AspNetCoreSession,
            AspNetCoreSessionIdKey = "realtimestack-id",
        };
        var firstResolver = new RealtimeStackSessionResolver(store, options);
        var secondResolver = new RealtimeStackSessionResolver(store, options);
        var context = new DefaultHttpContext();
        var session = new FakeSession("aspnet-session-id");
        session.SetString("realtimestack-id", "valid-session-123456");
        context.Features.Set<ISessionFeature>(new SessionFeature { Session = session });

        var first = await firstResolver.ResolveAsync(context, CancellationToken.None);
        var second = await secondResolver.ResolveAsync(context, CancellationToken.None);

        Assert.Equal("valid-session-123456", first?.SessionId);
        Assert.Equal(first?.SessionId, second?.SessionId);
        Assert.Equal(first?.Identity.TenantId, second?.Identity.TenantId);
        Assert.Equal(first?.Identity.UserId, second?.Identity.UserId);
        Assert.Equal(2, store.ValidationCount);
    }

    [Fact]
    public async Task AspNetCoreSessionResolutionExplainsRequiredMiddlewareOrder()
    {
        var resolver = new RealtimeStackSessionResolver(new FakeSessionStore(), new RealtimeStackOptions
        {
            SessionSource = RealtimeStackSessionSource.AspNetCoreSession,
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(new DefaultHttpContext(), CancellationToken.None));

        Assert.Contains("AddSession and UseSession", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredOrUnknownSessionIsRejected()
    {
        var resolver = new RealtimeStackSessionResolver(new FakeSessionStore { Expired = true }, new RealtimeStackOptions());
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "realtimestack_session=valid-session-123456";

        Assert.Null(await resolver.ResolveAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task MapRealtimeStackGatewayRejectsDuplicateRegistrationAndAppliesAuthorizationPolicy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(ValidConfiguration());
        builder.Services.AddRealtimeStackGateway(builder.Configuration, options => options.AuthorizationPolicy = "realtimestack-user");
        await using var app = builder.Build();

        app.MapRealtimeStackGateway();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Equal(2, endpoints.Count(endpoint => endpoint.RoutePattern.RawText is "/realtimestack/ws" or "/realtimestack/tickets"));
        Assert.All(endpoints.Where(endpoint => endpoint.RoutePattern.RawText is "/realtimestack/ws" or "/realtimestack/tickets"), endpoint =>
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), metadata => metadata.Policy == "realtimestack-user"));
        Assert.Throws<InvalidOperationException>(() => app.MapRealtimeStackGateway());
    }

    [Fact]
    public async Task MapRealtimeStackGatewayRejectsAConflictingApplicationRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(ValidConfiguration());
        builder.Services.AddRealtimeStackGateway(builder.Configuration);
        await using var app = builder.Build();
        app.MapGet("/realtimestack/ws", () => "application endpoint");

        var exception = Assert.Throws<InvalidOperationException>(() => app.MapRealtimeStackGateway());

        Assert.Contains("already mapped", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapRealtimeStackHttpFallbackAppliesTheRealtimeStackAuthorizationPolicy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(ValidConfiguration());
        builder.Services.AddRealtimeStackGateway(builder.Configuration, options => options.AuthorizationPolicy = "realtimestack-user");
        builder.Services.AddRealtimeStackHttpFallback(builder.Configuration);
        await using var app = builder.Build();

        app.MapRealtimeStackHttpFallback();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/realtimestack/http", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(5, endpoints.Length);
        Assert.All(endpoints, endpoint =>
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), metadata => metadata.Policy == "realtimestack-user"));
    }

    [Fact]
    public async Task MapRealtimeStackHttpFallbackRejectsAConflictingApplicationRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(ValidConfiguration());
        builder.Services.AddRealtimeStackGateway(builder.Configuration);
        builder.Services.AddRealtimeStackHttpFallback(builder.Configuration);
        await using var app = builder.Build();
        app.MapPost("/realtimestack/http/connections/{id}/poll", () => "application endpoint");

        var exception = Assert.Throws<InvalidOperationException>(() => app.MapRealtimeStackHttpFallback());

        Assert.Contains("already mapped", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceCollection CreateServices(IDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRealtimeStackGateway(configuration);
        return services;
    }

    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["RealtimeStack:EndpointPath"] = "/realtimestack/ws",
        ["RealtimeStack:TicketEndpointPath"] = "/realtimestack/tickets",
        ["RealtimeStack:AllowedOrigins:0"] = "https://app.example",
        ["Redis:Endpoint"] = "redis.example:6379",
    };

    private sealed class FakeSessionStore : IRealtimeStackSessionStore
    {
        public bool Revoked { get; set; }

        public bool Expired { get; set; }

        public int ValidationCount { get; private set; }

        public ValueTask<RealtimeStackIdentity?> ValidateAsync(string sessionId, CancellationToken cancellationToken)
        {
            ValidationCount++;
            var valid = sessionId == "valid-session-123456" && !Revoked && !Expired;
            return ValueTask.FromResult<RealtimeStackIdentity?>(valid
                ? new RealtimeStackIdentity("tenant-1", "user-1", ["orders"], DateTimeOffset.UtcNow.AddMinutes(5))
                : null);
        }
    }

    private sealed class SessionFeature : ISessionFeature
    {
        public required ISession Session { get; set; }
    }

    private sealed class FakeSession(string id) : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public bool IsAvailable => true;

        public string Id { get; } = id;

        public IEnumerable<string> Keys => _values.Keys;

        public void Clear() => _values.Clear();

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Remove(string key) => _values.Remove(key);

        public void Set(string key, byte[] value) => _values[key] = value;

        public bool TryGetValue(string key, out byte[] value) => _values.TryGetValue(key, out value!);
    }
}
