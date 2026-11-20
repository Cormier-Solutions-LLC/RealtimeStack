using RealtimeStack.Contracts;
using RealtimeStack.Gateway;
using RealtimeStack.Redis;

namespace RealtimeStack.UnitTests;

public class RegisteredAccountTests
{
    private const string Issuer = "https://access.example.org";
    private static readonly DateTimeOffset Expiry = DateTimeOffset.UtcNow.AddHours(1);
    private static RealtimeStackIdentity Identity(string? session = "session-fixture-123") => new("tenant", "guest-user", ["orders"], Expiry, session);
    private static RedisSessionRecord Record() => new("tenant", "guest-user", ["orders"], Expiry) { RegisteredIdentity = new(Issuer, "usr-registered", true, true) };
    [Fact] public void GuestNamesCannotReplaceVerifiedRegisteredProvenance()
    {
        var valid = Record(); Assert.True(RegisteredAccountSessionStore.IsRegistered(valid, Identity(), Issuer));
        foreach (var invalid in new[] { valid with { RegisteredIdentity = null }, valid with { RegisteredIdentity = new(Issuer, "usr-registered", false, true) }, valid with { RegisteredIdentity = new(Issuer, "usr-registered", true, false) }, valid with { RegisteredIdentity = new("https://other.example.org", "usr-registered", true, true) }, valid with { RegisteredIdentity = new(Issuer, "", true, true) }, valid with { RegisteredIdentity = new(Issuer, null!, true, true) }, valid with { UserId = "other-user" }, valid with { Revoked = true } })
            Assert.False(RegisteredAccountSessionStore.IsRegistered(invalid, Identity(), Issuer));
    }
    [Fact] public async Task RequiredRegistrationRejectsTicketOnlyAndRevokedAccountBypasses()
    {
        var options = new GatewayRegistrationOptions(true, new Uri("https://accounts.example.org/internal/product/registration"), "installation-fixture", new string('x',32), Path.Combine(Path.GetTempPath(), "unused-test-cache"), TimeSpan.FromMinutes(1)) { AccountIssuer = Issuer };
        var ticket = new Tickets { Identity = Identity(session: null) }; var sessions = new Sessions { Identity = Identity() }; var store = new RegisteredAccountTicketStore(ticket, sessions, options);
        Assert.Null(await store.ConsumeAsync("ticket", "audience", default));
        ticket.Identity = Identity(); Assert.NotNull(await store.ConsumeAsync("ticket", "audience", default));
        sessions.Identity = null; Assert.Null(await store.ConsumeAsync("ticket", "audience", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.IssueAsync(Identity(), "audience", TimeSpan.FromSeconds(30), default).AsTask());
        var unrestricted = new RegisteredAccountTicketStore(ticket, sessions, options with { Required = false }); Assert.NotNull(await unrestricted.ConsumeAsync("ticket", "audience", default));
    }
    private sealed class Sessions : IRealtimeStackSessionStore { public RealtimeStackIdentity? Identity { get; set; } public ValueTask<RealtimeStackIdentity?> ValidateAsync(string sessionId, CancellationToken cancellationToken) => ValueTask.FromResult(Identity); }
    private sealed class Tickets : IConnectionTicketStore { public RealtimeStackIdentity? Identity { get; set; } public ValueTask<string> IssueAsync(RealtimeStackIdentity identity, string audience, TimeSpan lifetime, CancellationToken cancellationToken) => ValueTask.FromResult("ticket"); public ValueTask<RealtimeStackIdentity?> ConsumeAsync(string ticket, string audience, CancellationToken cancellationToken) => ValueTask.FromResult(Identity); }
}
