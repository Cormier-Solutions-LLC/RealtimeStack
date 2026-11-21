using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RealtimeStack.Gateway;

public interface IRegisteredAccountAuthority
{
    ValueTask<bool> ValidateAsync(string subject, string stamp, CancellationToken cancellationToken);
}

/// <summary>Uses only the existing scoped Accounts service credential.</summary>
public sealed class GatewayAccountAuthorityClient(GatewayRegistrationOptions options, IHttpClientFactory clients, TimeProvider? clock = null) : IRegisteredAccountAuthority
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, GatewayAccountAuthorityResponse> cache = new(StringComparer.Ordinal);
    public async ValueTask<bool> ValidateAsync(string subject, string stamp, CancellationToken cancellationToken)
    {
        if (!options.Required) return true;
        if (!ValidSubject(subject) || !ValidStamp(stamp)) return false;
        var key = subject + ":" + stamp;
        cache.TryGetValue(key, out var previous);
        var now = time.GetUtcNow();
        if (previous is { Authorized: true } && now - previous.CheckedAt < TimeSpan.FromSeconds(30)) return true;
        try
        {
            var endpoint = new UriBuilder(options.StatusUrl!) { Path = "/internal/account-state" }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceToken);
            request.Content = JsonContent.Create(new GatewayAccountAuthorityRequest(subject, stamp), GatewayRegistrationJsonContext.Default.GatewayAccountAuthorityRequest);
            using var response = await clients.CreateClient("registration-state").SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) throw new HttpRequestException();
            if (!response.IsSuccessStatusCode) { cache.TryRemove(key, out _); return false; }
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            now = time.GetUtcNow();
            if (body.Length > 32 * 1024) { cache.TryRemove(key, out _); return false; }
            var value = JsonSerializer.Deserialize(body, GatewayRegistrationJsonContext.Default.GatewayAccountAuthorityResponse);
            if (value is null || value.Subject != subject || value.CheckedAt > now.AddSeconds(30) || now - value.CheckedAt > TimeSpan.FromSeconds(120)) { cache.TryRemove(key, out _); return false; }
            if (!value.Authorized) { cache.TryRemove(key, out _); return false; }
            if (cache.Count >= 1000) cache.Clear();
            cache[key] = value; return true;
        }
        catch (JsonException) { cache.TryRemove(key, out _); return false; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        { return previous is { Authorized: true } && time.GetUtcNow() - previous.CheckedAt <= TimeSpan.FromSeconds(120); }
    }
    public static bool ValidSubject(string? value) => value is { Length: > 0 and <= 128 } && !value.Any(char.IsControl);
    public static bool ValidStamp(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record GatewayAccountAuthorityRequest(string Subject, string CredentialStamp);
public sealed record GatewayAccountAuthorityResponse(string Subject, bool Authorized, DateTimeOffset CheckedAt);
