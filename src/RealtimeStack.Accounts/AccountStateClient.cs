using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cormier.Access;

public sealed record CentralAccountState(string Subject, bool Enabled, string? Email, string? Name,
    bool EmailVerified, string? CredentialStamp, DateTimeOffset FetchedAt, bool Stale = false);

public sealed class AccountStateUnavailableException : Exception
{
    public AccountStateUnavailableException() : base("Central account state is unavailable; sign in again when Access is reachable.") { }
}

/// <summary>Server-only confidential-client authority checks. Never supplies roles or God privileges.</summary>
public sealed class AccountStateClient : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string authorization;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, CentralAccountState> cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<CentralAccountState>>> pending = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumStaleAge = TimeSpan.FromSeconds(120);
    private sealed class HardFailure : Exception;

    public AccountStateClient(string issuer, string clientId, string clientSecret, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || issuer.TrimEnd('/') != issuer ||
            string.IsNullOrWhiteSpace(clientId) || clientId.Contains(':') || string.IsNullOrWhiteSpace(clientSecret))
            throw new ArgumentException("An HTTPS issuer origin and confidential client configuration are required.");
        endpoint = new Uri(origin, "/api/sso/account-state");
        authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + clientSecret));
        this.clock = clock ?? TimeProvider.System;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 64 * 1024 };
    }

    public void Clear(string subject) => cache.TryRemove(subject, out _);

    public async Task LogoutAsync(string subject, string idToken, CancellationToken cancellationToken)
    {
        var logoutEndpoint = new Uri(endpoint, "/api/sso/logout");
        using var request = new HttpRequestMessage(HttpMethod.Post, logoutEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
        request.Content = new StringContent(JsonSerializer.Serialize(new { idToken }), Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent || response.RequestMessage?.RequestUri != logoutEndpoint)
                throw new AccountStateUnavailableException();
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && (error is HttpRequestException or OperationCanceledException or IOException))
        {
            throw new AccountStateUnavailableException();
        }
        Clear(subject);
    }

    public async Task<CentralAccountState> GetAsync(string subject, bool fresh = false, CancellationToken cancellationToken = default)
    {
        if (subject.Length is < 1 or > 128) throw new ArgumentException("Invalid central subject.");
        if (!fresh && cache.TryGetValue(subject, out var entry) && Age(entry) < CacheAge) return entry;
        var operation = pending.GetOrAdd(subject, value => new Lazy<Task<CentralAccountState>>(() => RefreshAsync(value), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var state = await operation.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (fresh && state.Stale) throw new AccountStateUnavailableException();
            return state;
        }
        finally { if (operation.IsValueCreated && operation.Value.IsCompleted) ((ICollection<KeyValuePair<string, Lazy<Task<CentralAccountState>>>>)pending).Remove(new(subject, operation)); }
    }

    public async Task<CentralAccountState?> ValidateAsync(string subject, string? storedStamp, CancellationToken cancellationToken = default)
    {
        var state = await GetAsync(subject, cancellationToken: cancellationToken).ConfigureAwait(false);
        return state.Enabled && state.EmailVerified && IsStamp(storedStamp) && state.CredentialStamp is not null &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedStamp!), Encoding.ASCII.GetBytes(state.CredentialStamp)) ? state : null;
    }

    private TimeSpan Age(CentralAccountState state) => clock.GetUtcNow() - state.FetchedAt;
    private static bool IsStamp(string? value) => value?.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task<CentralAccountState> RefreshAsync(string subject)
    {
        cache.TryGetValue(subject, out var previous);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
            request.Content = new StringContent(JsonSerializer.Serialize(new { subjects = new[] { subject } }), Encoding.UTF8, "application/json");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (((int)response.StatusCode is >= 300 and < 500 && response.StatusCode != HttpStatusCode.TooManyRequests) || response.RequestMessage?.RequestUri != endpoint)
                throw new HardFailure();
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Central account state unavailable.");
            if (response.Content.Headers.ContentLength > 64 * 1024) throw new HardFailure();
            using var payload = new MemoryStream();
            await using (var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            {
                var buffer = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (payload.Length + read > 64 * 1024) throw new HardFailure();
                    payload.Write(buffer, 0, read);
                }
            }
            CentralAccountState state;
            try
            {
                using var document = JsonDocument.Parse(payload.ToArray());
                var accounts = document.RootElement.GetProperty("accounts");
                if (accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() != 1) throw new HardFailure();
                var item = accounts[0];
                if (item.GetProperty("subject").GetString() != subject || item.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new HardFailure();
                var enabled = item.GetProperty("enabled").GetBoolean();
                if (!enabled) state = new(subject, false, null, null, false, null, clock.GetUtcNow());
                else
                {
                    var email = item.GetProperty("email").GetString(); var name = item.GetProperty("name").GetString(); var stamp = item.GetProperty("credentialStamp").GetString();
                    if (item.GetProperty("emailVerified").ValueKind != JsonValueKind.True || email?.Length is not (> 0 and <= 254) || name?.Length is not (<= 120) || !IsStamp(stamp)) throw new HardFailure();
                    state = new(subject, true, email, name, true, stamp, clock.GetUtcNow());
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { throw new HardFailure(); }
            if (cache.Count >= 1000 && cache.Keys.FirstOrDefault() is { } oldest) cache.TryRemove(oldest, out _);
            cache[subject] = state;
            return state;
        }
        catch (HardFailure) { Clear(subject); throw new AccountStateUnavailableException(); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            if (previous is not null && Age(previous) >= TimeSpan.Zero && Age(previous) <= MaximumStaleAge) return previous with { Stale = true };
            throw new AccountStateUnavailableException();
        }
    }

    public void Dispose() => client.Dispose();
}
