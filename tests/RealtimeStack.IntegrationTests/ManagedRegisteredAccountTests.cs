using System.Text.Json;
using RealtimeStack.Contracts;
using RealtimeStack.Gateway;
using RealtimeStack.Redis;
using StackExchange.Redis;

namespace RealtimeStack.IntegrationTests;

public class ManagedRegisteredAccountTests
{
    [Fact]
    public async Task RealRedisTicketsRetainOriginalStampAndCannotBorrowReauthenticatedOrDisabledSessions()
    {
        var endpoint=Environment.GetEnvironmentVariable("REDIS_TEST_ENDPOINT")??"host.docker.internal:16379";
        var redis=new RedisOptions{Endpoint=endpoint,InstancePrefix="managed-account-test:"+Guid.NewGuid().ToString("N")};
        await using var provider=new RedisConnectionProvider(redis);
        var database=(await provider.GetConnectionAsync(default)).GetDatabase();
        var authority=new Authority();
        var options=new GatewayRegistrationOptions(true,new Uri("https://accounts.example.test/internal/product/registration"),"fixture-installation",new string('x',32),Path.GetFullPath("unused-managed-cache"),TimeSpan.FromMinutes(1)){AccountIssuer="https://access.example.test"};
        var sessions=new RegisteredAccountSessionStore(new RedisSessionStore(provider,redis),provider,redis,options,authority);
        var tickets=new RegisteredAccountTicketStore(new RedisConnectionTicketStore(provider,redis),sessions,options);
        const string sessionId="managed-session-fixture-123456";
        var key=(RedisKey)$"{redis.InstancePrefix}:{redis.SessionKeyPrefix}:{sessionId}";
        var keys=new List<RedisKey>{key};
        var provenance=new RegisteredAccountIdentity(options.AccountIssuer,"usr-registered",true,true){CredentialStamp=new string('a',64)};
        var record=new RedisSessionRecord("tenant","guest",["orders"],DateTimeOffset.UtcNow.AddMinutes(5)){RegisteredIdentity=provenance};
        async Task Store(RedisSessionRecord value)=>await database.StringSetAsync(key,JsonSerializer.Serialize(value,RealtimeStackJsonSerializerContext.Default.RedisSessionRecord));
        async Task<string> Issue(RealtimeStackIdentity value){var ticket=await tickets.IssueAsync(value,"gateway.example",TimeSpan.FromSeconds(30),default);keys.Add($"{redis.InstancePrefix}:{redis.TicketKeyPrefix}:{ticket}");return ticket;}
        try
        {
            await Store(record);var identity=(await sessions.ValidateAsync(sessionId,default))! with {SessionId=sessionId};Assert.Equal(provenance,identity.RegisteredIdentity);
            var valid=await Issue(identity);var consumed=await tickets.ConsumeAsync(valid,"gateway.example",default);Assert.Equal(provenance,consumed?.RegisteredIdentity);Assert.Null(await tickets.ConsumeAsync(valid,"gateway.example",default));
            var old=await Issue(identity);await Store(record with {RegisteredIdentity=provenance with {CredentialStamp=new string('b',64)}});Assert.Null(await tickets.ConsumeAsync(old,"gateway.example",default));
            await Store(record);var revoked=await Issue(identity);authority.Enabled=false;Assert.Null(await sessions.ValidateAsync(sessionId,default));Assert.Null(await tickets.ConsumeAsync(revoked,"gateway.example",default));await Assert.ThrowsAsync<InvalidOperationException>(()=>Issue(identity));
            authority.Enabled=true;await Store(record with {RegisteredIdentity=null});Assert.Null(await sessions.ValidateAsync(sessionId,default));
            await Store(record with {RegisteredIdentity=provenance with {CredentialStamp=null}});Assert.Null(await sessions.ValidateAsync(sessionId,default));
        }
        finally{await database.KeyDeleteAsync(keys.ToArray());}
    }
    private sealed class Authority:IRegisteredAccountAuthority{public bool Enabled=true;public ValueTask<bool> ValidateAsync(string subject,string stamp,CancellationToken cancellationToken)=>ValueTask.FromResult(Enabled);}
}
