using System.Net;
using System.Text;
using System.Text.Json;
using Cormier.Access;

namespace RealtimeStack.Accounts.Tests;

public class AccountStateTests
{
    private const string Subject = "usr-fixture";
    private const string Stamp = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }
    private sealed class Transport : HttpMessageHandler
    {
        public int Calls;
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => Response();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("https://access.example.test/api/sso/account-state", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture:" + new string('x', 32), Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
            var response = Reply(request); response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
    private sealed class UnknownLengthContent(string body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private static HttpResponseMessage Response(bool enabled = true, string stamp = Stamp, string subject = Subject) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { accounts = new[] { new { subject, enabled, email = "owner@example.test", name = "Fixture owner", emailVerified = true, credentialStamp = stamp } } }), Encoding.UTF8, "application/json")
    };
    private static AccountStateClient Client(Transport transport, Clock clock) => new("https://access.example.test", "fixture", new string('x', 32), transport, clock);

    [Fact] public async Task CallbackFreshRequestStoresStampAndCacheExpiresWithinThirtySeconds()
    {
        var transport = new Transport(); var clock = new Clock(); using var client = Client(transport, clock);
        Assert.Equal(Stamp, (await client.GetAsync(Subject, fresh: true)).CredentialStamp);
        clock.Advance(29); Assert.NotNull(await client.ValidateAsync(Subject, Stamp)); Assert.Equal(1, transport.Calls);
        clock.Advance(1); Assert.NotNull(await client.ValidateAsync(Subject, Stamp)); Assert.Equal(2, transport.Calls);
        Assert.Null(await client.ValidateAsync(Subject, new string('b', 64)));
    }
    [Fact] public async Task LockAndCredentialChangesInvalidateAnExistingSession()
    {
        var transport = new Transport(); var clock = new Clock(); using var client = Client(transport, clock);
        await client.GetAsync(Subject); transport.Reply = _ => Response(enabled: false); clock.Advance(30);
        Assert.Null(await client.ValidateAsync(Subject, Stamp));
        transport.Reply = _ => Response(stamp: new string('b', 64)); clock.Advance(30);
        Assert.Null(await client.ValidateAsync(Subject, Stamp)); Assert.NotNull(await client.ValidateAsync(Subject, new string('b', 64)));
    }
    [Theory] [InlineData(503)] [InlineData(429)] public async Task OutageKeepsOnlyTheLastValidatedStateForAtMostTwoMinutes(int status)
    {
        var transport = new Transport(); var clock = new Clock(); using var client = Client(transport, clock);
        await client.GetAsync(Subject); transport.Reply = _ => new((HttpStatusCode)status); clock.Advance(120);
        Assert.True((await client.ValidateAsync(Subject, Stamp))!.Stale);
        clock.Advance(1); await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.ValidateAsync(Subject, Stamp));
    }
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(302)]
    public async Task ClientAuthenticationOrRedirectFailureDoesNotReuseAValidCachedState(int status)
    {
        var transport = new Transport(); var clock = new Clock(); using var client = Client(transport, clock);
        await client.GetAsync(Subject); transport.Reply = _ => new((HttpStatusCode)status); clock.Advance(30);
        await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.ValidateAsync(Subject, Stamp));
        transport.Reply = _ => new(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.ValidateAsync(Subject, Stamp));
    }
    [Fact] public async Task SubjectSwapFailsClosedWithoutOutageFallback()
    {
        var transport = new Transport(); var clock = new Clock(); using var client = Client(transport, clock);
        await client.GetAsync(Subject); transport.Reply = _ => Response(subject: "usr-other"); clock.Advance(30);
        await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.ValidateAsync(Subject, Stamp));
    }
    [Fact] public async Task FreshCallbackCannotUseStaleStateDuringAnOutage()
    {
        var transport = new Transport(); var clock = new Clock(); using var client = Client(transport, clock);
        await client.GetAsync(Subject); transport.Reply = _ => new(HttpStatusCode.ServiceUnavailable); clock.Advance(30);
        await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.GetAsync(Subject, fresh: true));
        Assert.True((await client.ValidateAsync(Subject, Stamp))!.Stale);
    }
    [Theory] [InlineData("null")] [InlineData("{")]
    public async Task MalformedPayloadClearsPreviouslyValidState(string body)
    {
        var transport = new Transport(); var clock = new Clock(); using var client = Client(transport, clock);
        await client.GetAsync(Subject); transport.Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent(body) }; clock.Advance(30);
        await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.ValidateAsync(Subject, Stamp));
        transport.Reply = _ => new(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.ValidateAsync(Subject, Stamp));
    }
    [Theory] [InlineData(true)] [InlineData(false)] public async Task OversizedPayloadDoesNotFallBackToCachedAuthority(bool knownLength)
    {
        var transport = new Transport(); var clock = new Clock(); using var client = Client(transport, clock);
        await client.GetAsync(Subject); transport.Reply = _ => new(HttpStatusCode.OK) { Content = knownLength ? new StringContent(new string('x', 64 * 1024 + 1)) : new UnknownLengthContent(new string('x', 64 * 1024 + 1)) }; clock.Advance(30);
        await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.ValidateAsync(Subject, Stamp));
        transport.Reply = _ => new(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.ValidateAsync(Subject, Stamp));
    }
    [Fact] public async Task EnabledProfileBeyondCentralNameLimitIsRejectedWithoutCachedFallback()
    {
        var transport = new Transport(); var clock = new Clock(); using var client = Client(transport, clock);
        await client.GetAsync(Subject);
        transport.Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { accounts = new[] { new { subject = Subject, enabled = true, email = "owner@example.test", name = new string('n', 121), emailVerified = true, credentialStamp = Stamp } } })) };
        clock.Advance(30); await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.ValidateAsync(Subject, Stamp));
        transport.Reply = _ => new(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<AccountStateUnavailableException>(() => client.ValidateAsync(Subject, Stamp));
    }
}
