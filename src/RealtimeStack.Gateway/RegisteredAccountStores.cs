using System.Text.Json;
using RealtimeStack.Contracts;
using RealtimeStack.Redis;

namespace RealtimeStack.Gateway;

public sealed class RegisteredAccountSessionStore(IRealtimeStackSessionStore inner, RedisConnectionProvider connections, RedisOptions redis, GatewayRegistrationOptions options, IRegisteredAccountAuthority authority) : IRealtimeStackSessionStore
{
    public async ValueTask<RealtimeStackIdentity?> ValidateAsync(string sessionId, CancellationToken cancellationToken)
    {
        var identity = await inner.ValidateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!options.Required || identity is null) return identity;
        var connection = await connections.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        var value = await connection.GetDatabase().StringGetAsync($"{redis.InstancePrefix}:{redis.SessionKeyPrefix}:{sessionId}").ConfigureAwait(false);
        if (value.IsNullOrEmpty) return null;
        RedisSessionRecord? record;
        try { record = JsonSerializer.Deserialize(value.ToString(), RealtimeStackJsonSerializerContext.Default.RedisSessionRecord); }
        catch (JsonException) { return null; }
        if (!IsRegistered(record, identity, options.AccountIssuer) || !await authority.ValidateAsync(record!.RegisteredIdentity!.Subject, record.RegisteredIdentity.CredentialStamp!, cancellationToken).ConfigureAwait(false)) return null;
        return identity with { RegisteredIdentity = record.RegisteredIdentity };
    }
    public static bool IsRegistered(RedisSessionRecord? record, RealtimeStackIdentity identity, string issuer) =>
        record is { Revoked: false, RegisteredIdentity: { Registered: true, EmailVerified: true } registration } &&
        record.TenantId == identity.TenantId && record.UserId == identity.UserId && record.ExpiresAt >= identity.ExpiresAt && record.ExpiresAt > DateTimeOffset.UtcNow &&
        record.AllowedTopics is not null && identity.AllowedTopics.All(topic => record.AllowedTopics.Contains("*", StringComparer.Ordinal) || record.AllowedTopics.Contains(topic, StringComparer.Ordinal)) &&
        registration.Issuer == issuer && issuer.Length > 0 && GatewayAccountAuthorityClient.ValidSubject(registration.Subject) && GatewayAccountAuthorityClient.ValidStamp(registration.CredentialStamp);
}

public sealed class RegisteredAccountTicketStore(IConnectionTicketStore inner, IRealtimeStackSessionStore sessions, GatewayRegistrationOptions options) : IConnectionTicketStore
{
    public async ValueTask<string> IssueAsync(RealtimeStackIdentity identity, string audience, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (options.Required && !await BoundAsync(identity, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("A registered account session is required to issue a connection ticket.");
        return await inner.IssueAsync(identity, audience, lifetime, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<RealtimeStackIdentity?> ConsumeAsync(string ticket, string audience, CancellationToken cancellationToken)
    {
        var identity = await inner.ConsumeAsync(ticket, audience, cancellationToken).ConfigureAwait(false);
        return identity is null || options.Required && !await BoundAsync(identity, cancellationToken).ConfigureAwait(false) ? null : identity;
    }
    private async ValueTask<bool> BoundAsync(RealtimeStackIdentity identity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity.SessionId)) return false;
        var session = await sessions.ValidateAsync(identity.SessionId, cancellationToken).ConfigureAwait(false);
        return session is not null && session.TenantId == identity.TenantId && session.UserId == identity.UserId &&
            identity.RegisteredIdentity is not null && session.RegisteredIdentity == identity.RegisteredIdentity &&
            session.ExpiresAt >= identity.ExpiresAt && identity.AllowedTopics.All(topic => session.AllowedTopics.Contains("*", StringComparer.Ordinal) || session.AllowedTopics.Contains(topic, StringComparer.Ordinal));
    }
}
