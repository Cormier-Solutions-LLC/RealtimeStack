using RealtimeStack.Contracts;
using RealtimeStack.Redis;
using Microsoft.AspNetCore.Http.Features;

namespace RealtimeStack.Gateway;

public sealed record RealtimeStackSessionResolution(string SessionId, RealtimeStackIdentity Identity);

public interface IRealtimeStackSessionResolver
{
    ValueTask<RealtimeStackSessionResolution?> ResolveAsync(HttpContext context, CancellationToken cancellationToken);

    ValueTask<RealtimeStackIdentity?> RevalidateAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed class RealtimeStackSessionResolver(
    IRealtimeStackSessionStore sessionStore,
    RealtimeStackOptions options) : IRealtimeStackSessionResolver
{
    public async ValueTask<RealtimeStackSessionResolution?> ResolveAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var sessionId = options.SessionSource switch
        {
            RealtimeStackSessionSource.Cookie => context.Request.Cookies[options.SessionCookieName],
            RealtimeStackSessionSource.AspNetCoreSession => await ResolveAspNetCoreSessionIdAsync(context, cancellationToken),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var identity = await sessionStore.ValidateAsync(sessionId, cancellationToken);
        return identity is null ? null : new RealtimeStackSessionResolution(sessionId, identity);
    }

    public ValueTask<RealtimeStackIdentity?> RevalidateAsync(
        string sessionId,
        CancellationToken cancellationToken) =>
        sessionStore.ValidateAsync(sessionId, cancellationToken);

    private async ValueTask<string?> ResolveAspNetCoreSessionIdAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var feature = context.Features.Get<ISessionFeature>();
        if (feature?.Session is null)
        {
            throw new InvalidOperationException(
                "ASP.NET Core session resolution requires AddSession and UseSession before mapped realtimestack endpoints execute.");
        }

        await feature.Session.LoadAsync(cancellationToken);
        return feature.Session.GetString(options.AspNetCoreSessionIdKey) ?? feature.Session.Id;
    }
}
