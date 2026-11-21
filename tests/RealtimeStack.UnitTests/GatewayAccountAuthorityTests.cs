using System.Net;
using System.Text.Json;
using RealtimeStack.Gateway;

namespace RealtimeStack.UnitTests;

public class GatewayAccountAuthorityTests
{
    private const string Subject = "usr-fixture";
    private static readonly string Stamp = new('a',64);
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; public void Advance(int seconds) => Now=Now.AddSeconds(seconds); }
    private sealed class Transport(Clock clock) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls; public HttpStatusCode Status = HttpStatusCode.OK; public bool Authorized = true; public string? Body;
        public HttpClient CreateClient(string name) => new(this,disposeHandler:false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        { Calls++; Assert.Equal("https://accounts.example.test/internal/account-state",request.RequestUri?.AbsoluteUri); Assert.Equal("Bearer "+new string('x',32),request.Headers.Authorization?.ToString()); return Task.FromResult(new HttpResponseMessage(Status){Content=new StringContent(Body??JsonSerializer.Serialize(new{subject=Subject,authorized=Authorized,checkedAt=clock.Now}))}); }
    }
    private static GatewayAccountAuthorityClient Client(Clock clock, Transport transport) => new(new(true,new Uri("https://accounts.example.test/internal/product/registration"),"installation-1",new string('x',32),Path.GetFullPath("unused-cache"),TimeSpan.FromMinutes(1)),transport,clock);
    [Fact] public async Task CachedAuthorityIsBoundToOriginalStampAndExpiresAtThirtySeconds()
    {
        var clock=new Clock();using var transport=new Transport(clock);var client=Client(clock,transport);
        Assert.True(await client.ValidateAsync(Subject,Stamp,default));clock.Advance(29);Assert.True(await client.ValidateAsync(Subject,Stamp,default));Assert.Equal(1,transport.Calls);
        clock.Advance(1);transport.Authorized=false;Assert.False(await client.ValidateAsync(Subject,Stamp,default));Assert.Equal(2,transport.Calls);
        transport.Status=HttpStatusCode.ServiceUnavailable;Assert.False(await client.ValidateAsync(Subject,Stamp,default));
    }
    [Fact] public async Task OutageRetainsLastGoodOnlyThroughOneHundredTwentySeconds()
    {
        var clock=new Clock();using var transport=new Transport(clock);var client=Client(clock,transport);
        Assert.True(await client.ValidateAsync(Subject,Stamp,default));clock.Advance(30);transport.Status=HttpStatusCode.ServiceUnavailable;
        Assert.True(await client.ValidateAsync(Subject,Stamp,default));clock.Advance(90);Assert.True(await client.ValidateAsync(Subject,Stamp,default));clock.Advance(1);Assert.False(await client.ValidateAsync(Subject,Stamp,default));
    }
    [Theory] [InlineData("null")] [InlineData("{}")] [InlineData("invalid-json")] [InlineData("{\"subject\":\"another\",\"authorized\":true,\"checkedAt\":\"2026-01-01T00:00:00Z\"}")]
    public async Task InvalidPayloadImmediatelyClearsEarlierPositiveAuthority(string body)
    {
        var clock=new Clock();using var transport=new Transport(clock);var client=Client(clock,transport);Assert.True(await client.ValidateAsync(Subject,Stamp,default));clock.Advance(30);transport.Body=body;Assert.False(await client.ValidateAsync(Subject,Stamp,default));transport.Status=HttpStatusCode.ServiceUnavailable;Assert.False(await client.ValidateAsync(Subject,Stamp,default));
    }
    [Theory] [InlineData(302)] [InlineData(400)] [InlineData(401)] [InlineData(403)]
    public async Task RedirectAndClientRejectionCannotUseStaleAuthority(int status)
    {
        var clock=new Clock();using var transport=new Transport(clock);var client=Client(clock,transport);Assert.True(await client.ValidateAsync(Subject,Stamp,default));clock.Advance(30);transport.Status=(HttpStatusCode)status;Assert.False(await client.ValidateAsync(Subject,Stamp,default));transport.Status=HttpStatusCode.ServiceUnavailable;Assert.False(await client.ValidateAsync(Subject,Stamp,default));
    }
}
