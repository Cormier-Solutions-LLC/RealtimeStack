using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace RealtimeStack.Accounts;

/// <summary>Browser cookies contain a protected handle; OAuth tokens stay encrypted in Redis.</summary>
public sealed class RedisTicketStore : ITicketStore, IDisposable
{
    private readonly ConnectionMultiplexer connection;
    private readonly IDataProtector protector;
    private readonly IDataProtector registrationProtector;
    private readonly string prefix;
    public bool IsConnected => connection.IsConnected;
    public RedisTicketStore(IConfiguration configuration, IDataProtectionProvider protection)
    {
        var file = configuration["Sso:RedisConnectionFile"];
        var redis = !string.IsNullOrWhiteSpace(file) ? File.ReadAllText(file).Trim() : configuration["Sso:RedisConnection"];
        prefix = configuration["Sso:RedisPrefix"] ?? "";
        if (string.IsNullOrWhiteSpace(redis) || prefix.Length is < 8 or > 100 || !prefix.EndsWith(':') || prefix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_')))
            throw new InvalidOperationException("SSO requires a Secret-backed Redis connection and an isolated Sso:RedisPrefix ending in a colon.");
        var options = ConfigurationOptions.Parse(redis); options.AbortOnConnectFail = false; options.ConnectTimeout = 5000; options.AsyncTimeout = 5000;
        connection = ConnectionMultiplexer.Connect(options);
        protector = protection.CreateProtector("RealtimeStack.Accounts.Tickets.v1");
        registrationProtector = protection.CreateProtector("RealtimeStack.Accounts.Registration.v1");
    }
    private string Key(string value) => prefix + "ticket:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(handle, ticket).ConfigureAwait(false); return handle;
    }
    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var ttl = (ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(8)) - DateTimeOffset.UtcNow;
        return connection.GetDatabase().StringSetAsync(Key(key), protector.Protect(TicketSerializer.Default.Serialize(ticket)), ttl > TimeSpan.Zero ? ttl : TimeSpan.FromSeconds(1));
    }
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var value = await connection.GetDatabase().StringGetAsync(Key(key)).ConfigureAwait(false);
        if (!value.HasValue) return null;
        try { return TicketSerializer.Default.Deserialize(protector.Unprotect((byte[])value!)); }
        catch (CryptographicException) { return null; }
    }
    public Task RemoveAsync(string key) => connection.GetDatabase().KeyDeleteAsync(Key(key));
    private string RegistrationKey(string installationId) => prefix + "registration:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installationId)));
    public Task WriteRegistrationAsync(string installationId, string receipt) => connection.GetDatabase().StringSetAsync(RegistrationKey(installationId), registrationProtector.Protect(Encoding.UTF8.GetBytes(receipt)));
    public async Task<string?> ReadRegistrationAsync(string installationId)
    {
        var value = await connection.GetDatabase().StringGetAsync(RegistrationKey(installationId)).ConfigureAwait(false);
        if (!value.HasValue) return null;
        try { return Encoding.UTF8.GetString(registrationProtector.Unprotect((byte[])value!)); }
        catch (CryptographicException) { return null; }
    }
    public void Dispose() => connection.Dispose();
}
