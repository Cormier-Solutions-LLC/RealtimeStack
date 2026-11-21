using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;
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
    private readonly bool requireGod;
    private readonly string issuer;
    public bool IsConnected => connection.IsConnected;
    public RedisTicketStore(IConfiguration configuration, IDataProtectionProvider protection)
    {
        requireGod = configuration.GetValue<bool>("Registration:RequireGod");
        issuer = configuration["Sso:Authority"]?.TrimEnd('/') ?? "";
        if (requireGod && !AccountsAccessOptions.SecureOrigin(issuer)) throw new InvalidOperationException("Owner-managed registration requires an exact HTTPS Access issuer.");
        prefix = configuration["Sso:RedisPrefix"] ?? "";
        if (prefix.Length is < 8 or > 100 || !prefix.EndsWith(':') || prefix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_')))
            throw new InvalidOperationException("SSO requires a Secret-backed Redis connection and an isolated Sso:RedisPrefix ending in a colon.");
        connection = ConnectionMultiplexer.Connect(ReadRedisOptions(configuration));
        protector = protection.CreateProtector("RealtimeStack.Accounts.Tickets.v1");
        registrationProtector = protection.CreateProtector("RealtimeStack.Accounts.Registration.v1");
    }
    public static ConfigurationOptions ReadRedisOptions(IConfiguration configuration)
    {
        var connection = ReadSecret(configuration, "Sso:RedisConnection") ?? configuration["Sso:RedisEndpoint"];
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("SSO requires a configured Redis endpoint or Secret-backed connection.");
        var options = ConfigurationOptions.Parse(connection);
        if (configuration["Sso:RedisUser"] is { } user) options.User = user;
        if (ReadSecret(configuration, "Sso:RedisPassword") is { } password) options.Password = password;
        if (configuration["Sso:RedisSentinelServiceName"] is { } service) options.ServiceName = service;
        if (ReadSecret(configuration, "Sso:RedisSentinelPassword") is { } sentinelPassword) options.SentinelPassword = sentinelPassword;
        options.AbortOnConnectFail = false; options.ConnectTimeout = 5000; options.AsyncTimeout = 5000;
        return options;
    }
    private static string? ReadSecret(IConfiguration configuration, string key)
    {
        var file = configuration[key + "File"];
        return !string.IsNullOrWhiteSpace(file) ? File.ReadAllText(file).Trim() : configuration[key];
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
    public Task WriteRegistrationAsync(string installationId, string receipt, ClaimsPrincipal? authenticatedAccount = null)
    {
        if (requireGod && !AccountsAccess.CanRegisterInstallation(authenticatedAccount, issuer, requireGod)) throw new UnauthorizedAccessException("The signed central owner is required to manage this installation registration.");
        return connection.GetDatabase().StringSetAsync(RegistrationKey(installationId), registrationProtector.Protect(Encoding.UTF8.GetBytes(receipt)));
    }
    public async Task<string?> ReadRegistrationAsync(string installationId)
    {
        var value = await connection.GetDatabase().StringGetAsync(RegistrationKey(installationId)).ConfigureAwait(false);
        if (!value.HasValue) return null;
        try { return Encoding.UTF8.GetString(registrationProtector.Unprotect((byte[])value!)); }
        catch (CryptographicException) { return null; }
    }
    public void Dispose() => connection.Dispose();
}
