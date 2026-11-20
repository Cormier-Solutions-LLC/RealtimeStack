using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cormier.Licensing.Products;

/// <summary>A licensing server request failed.</summary>
public sealed class LicensingHttpException(int status, string? code, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    /// <summary>HTTP status, or 0 when the server was unreachable.</summary>
    public int Status { get; } = status;
    /// <summary>Protocol error code.</summary>
    public string? Code { get; } = code;
    /// <summary>Retry-After from the server.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Cormier.Licensing.Products cloud client (licensing protocol v1): activates with the license key,
/// renews signed leases, keeps the last valid lease through the offline grace, exposes the
/// license state for watermarks and applies the resource rules through <see cref="Gate"/>.
/// Licensing never stops a running site; it only changes the watermark and throttles.
/// </summary>
public sealed class LicensingClient : IAsyncDisposable
{
    /// <summary>Client version sent in the User-Agent.</summary>
    public const string ClientVersion = "0.5.4-beta";
    private static readonly HashSet<string> RefusedCodes = ["invalid_key", "revoked", "license_expired", "not_entitled", "instance_limit"];

    private readonly HttpClient http;
    private readonly LicensingOptions options;
    private readonly LeaseVerifier verifier;
    private readonly TimeProvider time;
    private readonly IResourceProbe probe;
    private readonly ResourcePolicy policy = new();
    private readonly SemaphoreSlim exclusive = new(1, 1);
    private readonly string serverUrl;
    private (LeasePayload Lease, long ReceivedAt)? current;
    private string? activationId, token;
    private int failures;
    private ITimer? renewTimer, sampleTimer, usageTimer;
    private bool stopped;
    private ResourceAmount? lastUsage;
    private readonly List<UsageReport> pendingReports = [];
    private readonly HashSet<UsageReport> attempted = new(ReferenceEqualityComparer.Instance);
    private readonly long origin;
    /// <summary>Timestamp before which usage reports wait (Retry-After).</summary>
    private long usageRetryAt = long.MinValue;
    /// <summary>Timestamp that ends a report drain during shutdown.</summary>
    private long drainUntil = long.MaxValue;
    private int generation;

    private sealed class StaleResponseException : Exception;

    /// <summary>
    /// Responses to requests sent before the activation was invalidated (a refusal, a lost
    /// activation or shutdown) are discarded, so a late renewal cannot restore a refused lease.
    /// </summary>
    private void Invalidate()
    {
        Interlocked.Increment(ref generation);
        activationId = token = null;
        // Queued reports belong to the old activation; replaying them under a new one would
        // change the server's idempotency key and could charge the same usage twice.
        lock (pendingReports)
        {
            pendingReports.Clear();
            attempted.Clear();
        }
    }

    /// <summary>Raised when usage reaches the upgrade threshold (80% of licensed cores or memory).</summary>
    public event Action<UsageEvent>? UpgradeNotice;
    /// <summary>Raised for every usage event.</summary>
    public event Action<UsageEvent>? UsageEvent;
    /// <summary>Log sink; messages never contain the license key or activation token.</summary>
    public Action<string, string>? Log { get; set; }

    /// <summary>Creates a client. <paramref name="http"/> may be shared.</summary>
    public LicensingClient(HttpClient http, LicensingOptions options, TimeProvider? time = null, IResourceProbe? probe = null, WorkGate? gate = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        var url = options.ServerUrl;
        var local = url.IsLoopback || url.Host is "localhost";
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && local))
            throw new ArgumentException("The licensing server URL must use HTTPS", nameof(options));
        serverUrl = url.AbsoluteUri.TrimEnd('/');
        verifier = new LeaseVerifier(options.PublicKeys);
        this.time = time ?? TimeProvider.System;
        this.probe = probe ?? new ResourceProbe(this.time);
        Gate = gate ?? new WorkGate(options.GateSlots, this.time);
        origin = this.time.GetTimestamp();
        InstanceId = string.IsNullOrWhiteSpace(options.InstanceId) ? Guid.NewGuid().ToString() : options.InstanceId;
        policy.Event += e =>
        {
            UsageEvent?.Invoke(e);
            if (e.Type == "upgrade-threshold")
            {
                Write("info", $"Licensed {e.Resource} usage reached {e.Ratio:P0} of the license; consider upgrading");
                UpgradeNotice?.Invoke(e);
            }
            if (options.Schedule && e.Type is "upgrade-threshold" or "overage-exhausted")
                _ = Task.Run(ReportUsageAsync);
        };
    }

    /// <summary>The gate render and job paths run work through.</summary>
    public WorkGate Gate { get; }
    /// <summary>This instance's identifier.</summary>
    public string InstanceId { get; }
    /// <summary>True when a license key is configured.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(options.LicenseKey);
    /// <summary>The last request failure, without secrets.</summary>
    public string? LastError { get; private set; }
    /// <summary>The last resource-rule step.</summary>
    public PolicyStep? LastStep { get; private set; }
    /// <summary>Remaining overage balance as this instance sees it.</summary>
    public OverageBalance? OverageBalance => policy.Balance;

    private void Write(string level, string message) => Log?.Invoke(level, message);

    /// <summary>The last valid lease, or null once it expired (offline grace over) or none was issued.</summary>
    public LeasePayload? Lease
    {
        get
        {
            if (current is not { } c) return null;
            var lifetime = c.Lease.ExpiresAt - c.Lease.IssuedAt;
            if (time.GetUtcNow() >= c.Lease.ExpiresAt || time.GetElapsedTime(c.ReceivedAt) >= lifetime) return null;
            return c.Lease;
        }
    }

    /// <summary>License state for the watermark seam.</summary>
    public LicenseState State => LicenseState.FromLease(Lease, time.GetUtcNow());

    /// <summary>Watermark text for the current state: DEMO, TRIAL, DEVELOPMENT or null.</summary>
    public string? Watermark => State.Watermark;

    /// <summary>Activate and start the heartbeat. Never throws: failures leave the state at None and retry.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        stopped = false;
        if (!Configured)
        {
            Write("info", "No license key is configured; license state is none (DEMO)");
            return;
        }
        if (verifier.Count == 0)
        {
            Write("warn", "A license key is configured but no licensing public key is; license state is none (DEMO)");
            return;
        }
        await ActivateAsync(cancellationToken).ConfigureAwait(false);
        // Start the accounting baseline now, so the first interval is billed too.
        if (Lease is not null) Sample();
        if (options.Schedule && !stopped)
        {
            sampleTimer = time.CreateTimer(_ => Sample(), null, options.SampleInterval, options.SampleInterval);
            usageTimer = time.CreateTimer(_ => _ = ReportUsageAsync(), null, options.UsageInterval, options.UsageInterval);
        }
    }

    private static string Nonce() => Base64Url.Encode(RandomNumberGenerator.GetBytes(16));

    private async Task<T?> PostAsync<T>(string path, object body, string? bearer, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl + path)
        {
            Content = JsonContent.Create(body, LicensingJson.Default.GetTypeInfo(body.GetType())!),
        };
        request.Headers.UserAgent.ParseAdd($"Cormier.Licensing.Products/{ClientVersion}");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(dotnet)"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? options.RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new LicensingHttpException(0, null, $"Licensing server is unreachable: {error.GetType().Name}");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ErrorBody? error = null;
                try { error = string.IsNullOrEmpty(text) ? null : JsonSerializer.Deserialize(text, LicensingJson.Default.ErrorBody); }
                catch (JsonException) { }
                throw new LicensingHttpException((int)response.StatusCode, error?.Error?.Code,
                    error?.Error?.Message ?? $"Licensing server answered {(int)response.StatusCode}", RetryDelay(response.Headers.RetryAfter));
            }
            if (response.StatusCode == HttpStatusCode.NoContent || string.IsNullOrEmpty(text)) return default;
            try { return (T?)JsonSerializer.Deserialize(text, LicensingJson.Default.GetTypeInfo(typeof(T))!); }
            catch (JsonException) { throw new LeaseException("lease_rejected", "Licensing response is not valid JSON"); }
        }
    }

    /// <summary>Retry-After as a delay: delta-seconds or an HTTP-date (against the client's wall clock).</summary>
    internal TimeSpan? RetryDelay(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta) return delta > TimeSpan.Zero ? delta : null;
        if (header?.Date is { } date)
        {
            var wait = date - time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : null;
        }
        return null;
    }

    private LeasePayload Accept(SignedLease? envelope, string nonce, string activation, int requestGeneration)
    {
        if (requestGeneration != Volatile.Read(ref generation)) throw new StaleResponseException();
        var lease = verifier.Verify(envelope, activation, InstanceId, options.Application, nonce, time.GetUtcNow());
        current = (lease, time.GetTimestamp());
        policy.SyncBalance(lease.Overage, PendingOverage());
        failures = 0;
        LastError = null;
        return lease;
    }

    /// <summary>Activate with the license key.</summary>
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await exclusive.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ActivateCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { exclusive.Release(); }
    }

    private async Task ActivateCoreAsync(CancellationToken cancellationToken)
    {
        if (!Configured || stopped) return;
        var nonce = Nonce();
        var requestGeneration = Volatile.Read(ref generation);
        try
        {
            var response = await PostAsync<ActivationResponse>("/v1/activations", new ActivationRequest
            {
                protocol = LicensingProtocol.Version,
                licenseKey = options.LicenseKey,
                application = options.Application,
                applicationVersion = options.ApplicationVersion,
                client = new ClientIdentity("Cormier.Licensing.Products", ClientVersion),
                deploymentType = options.DeploymentType,
                instanceId = InstanceId,
                hostname = options.Hostname ?? Environment.MachineName,
                resources = probe.Resources(),
                nonce = nonce,
            }, null, cancellationToken).ConfigureAwait(false);
            if (response?.ActivationId is not { } id || response.ActivationToken is not { } activationToken)
                throw new LeaseException("lease_rejected", "Activation response is malformed");
            var lease = Accept(response.Lease, nonce, id, requestGeneration);
            activationId = id;
            token = activationToken;
            Write("info", $"License activated: {lease.LicenseId} ({lease.Kind}) until {lease.ExpiresAt:O}");
            ScheduleRenewal(lease);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (requestGeneration != Volatile.Read(ref generation)) return;
            // Any failure keeps the last valid lease and schedules a retry, so the heartbeat never stops.
            HandleFailure(error, "activation");
        }
    }

    /// <summary>Renew the lease (heartbeat).</summary>
    public async Task RenewAsync(CancellationToken cancellationToken = default)
    {
        await exclusive.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RenewCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { exclusive.Release(); }
    }

    private async Task RenewCoreAsync(CancellationToken cancellationToken)
    {
        if (stopped) return;
        if (activationId is not { } activation || token is null)
        {
            await ActivateCoreAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var nonce = Nonce();
        var requestGeneration = Volatile.Read(ref generation);
        try
        {
            var response = await PostAsync<LeaseResponse>($"/v1/activations/{Uri.EscapeDataString(activation)}/renew", new RenewalRequest
            {
                protocol = LicensingProtocol.Version,
                instanceId = InstanceId,
                nonce = nonce,
                resources = probe.Resources(),
                usage = lastUsage ?? probe.Usage(),
            }, token, cancellationToken).ConfigureAwait(false);
            ScheduleRenewal(Accept(response?.Lease, nonce, activation, requestGeneration));
        }
        catch (LicensingHttpException error) when (error.Status is 401 or 404 && requestGeneration == Volatile.Read(ref generation))
        {
            Invalidate();
            await ActivateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (requestGeneration != Volatile.Read(ref generation)) return;
            // Any failure keeps the last valid lease and schedules a retry, so the heartbeat never stops.
            HandleFailure(error, "renewal");
        }
    }

    /// <summary>A 403 refusal drops the lease at once (state None) and retries activation every 15 minutes.</summary>
    private bool Refused(Exception error)
    {
        if (error is not LicensingHttpException { Status: 403 } refused || (refused.Code is not null && !RefusedCodes.Contains(refused.Code)))
            return false;
        current = null;
        Invalidate();
        lock (pendingReports) pendingReports.Clear();
        Write("warn", $"The licensing server refused this license ({refused.Code}); license state is none (DEMO)");
        ScheduleRetry(TimeSpan.FromMinutes(15));
        return true;
    }

    private void HandleFailure(Exception error, string operation)
    {
        LastError = $"{operation}: {error.Message}";
        if (Refused(error)) return;
        failures++;
        var code = error switch
        {
            LeaseException lease => lease.Code,
            LicensingHttpException http => http.Code ?? http.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
        Write("warn", $"License {operation} failed ({code}); keeping the last valid lease: {error.Message}");
        var backoff = TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(4, failures - 1))) * (0.8 + Random.Shared.NextDouble() * 0.4));
        ScheduleRetry((error as LicensingHttpException)?.RetryAfter ?? backoff);
    }

    private void ScheduleRenewal(LeasePayload lease)
    {
        var due = lease.RenewAfter - time.GetUtcNow();
        ScheduleRetry(due < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : due);
    }

    private void ScheduleRetry(TimeSpan delay)
    {
        if (!options.Schedule || stopped) return;
        renewTimer?.Dispose();
        renewTimer = time.CreateTimer(_ => _ = RenewAsync(), null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Measure usage, apply the resource rules and resize the gate.</summary>
    public PolicyStep Sample()
    {
        var resources = probe.Resources();
        var usage = probe.Usage();
        lastUsage = usage;
        // Limits apply only while the state is licensed: an ended trial is None.
        var lease = Lease;
        var enforced = lease is not null && LicenseState.FromLease(lease, time.GetUtcNow()).Kind != LicenseKind.None ? lease : null;
        var step = policy.Step(time.GetUtcNow(), enforced, usage, resources, time.GetElapsedTime(origin));
        var slots = Math.Min(Gate.BaseSlots, step.CapCores is { } cap ? Math.Max(1, cap) : double.PositiveInfinity);
        Gate.SetCapacity(Math.Max(1, (int)Math.Floor(slots * step.Factor)));
        LastStep = step;
        return step;
    }

    internal static UsageReport Merge(UsageReport a, UsageReport b)
    {
        // Averages are weighted by how long each report's period is.
        var wa = Math.Max(0, (a.Period.To - a.Period.From).TotalMilliseconds);
        var wb = Math.Max(0, (b.Period.To - b.Period.From).TotalMilliseconds);
        double Weighted(double x, double y) => wa + wb > 0 ? (x * wa + y * wb) / (wa + wb) : (x + y) / 2;
        return MergeWith(a, b, Weighted);
    }

    private static UsageReport MergeWith(UsageReport a, UsageReport b, Func<double, double, double> weighted) => a with
    {
        LeaseId = b.LeaseId ?? a.LeaseId,
        Period = new(a.Period.From, b.Period.To),
        Usage = new(Math.Max(a.Usage.PeakCores, b.Usage.PeakCores), weighted(a.Usage.AverageCores, b.Usage.AverageCores),
            Math.Max(a.Usage.PeakMemoryBytes, b.Usage.PeakMemoryBytes), (long)Math.Round(weighted(a.Usage.AverageMemoryBytes, b.Usage.AverageMemoryBytes))),
        Burst = new(b.Burst.Active, b.Burst.StartedAt ?? a.Burst.StartedAt, a.Burst.Seconds + b.Burst.Seconds),
        Queue = new(a.Queue.Throttled || b.Queue.Throttled, b.Queue.QueuedTasks, Math.Max(a.Queue.MaxDepth, b.Queue.MaxDepth),
            a.Queue.WaitMilliseconds + b.Queue.WaitMilliseconds),
        Overage = new() { CoreSeconds = a.Overage.CoreSeconds + b.Overage.CoreSeconds, MemoryGibSeconds = a.Overage.MemoryGibSeconds + b.Overage.MemoryGibSeconds },
        Events = a.Events.Concat(b.Events).TakeLast(500).ToArray(),
    };

    /// <summary>
    /// Consumption the server has certainly not deducted yet: reports never sent. A sent
    /// report may already be deducted (its answer lost), so it is not subtracted again.
    /// </summary>
    private OverageBalance PendingOverage()
    {
        lock (pendingReports)
        {
            var unsent = pendingReports.Where(r => !attempted.Contains(r)).ToList();
            return new()
            {
                CoreSeconds = unsent.Sum(r => r.Overage.CoreSeconds),
                MemoryGibSeconds = unsent.Sum(r => r.Overage.MemoryGibSeconds),
            };
        }
    }

    /// <summary>
    /// Send the usage report for the period since the last one. A failed report is resent
    /// unchanged (the server deducts overage once per activation and period start); later
    /// usage goes into its own report.
    /// </summary>
    public async Task ReportUsageAsync()
    {
        // Reports never overlap activation or renewal, so a lease's overage balance
        // can never predate a report the server has acknowledged.
        await exclusive.WaitAsync().ConfigureAwait(false);
        try
        {
            var report = policy.TakeReport(time.GetUtcNow(), InstanceId, current?.Lease.LeaseId, Gate.TakeStats());
            if (activationId is null || token is null) return;
            lock (pendingReports)
            {
                // At most 288 queued reports without dropping usage: past that, fold the
                // new period into the newest never-sent report (sent ones keep their key).
                if (pendingReports.Count >= 288 && !attempted.Contains(pendingReports[^1]))
                    pendingReports[^1] = Merge(pendingReports[^1], report);
                else pendingReports.Add(report);
            }
            while (activationId is { } activation && token is { } bearer
                && time.GetTimestamp() >= Interlocked.Read(ref usageRetryAt) && time.GetTimestamp() < Interlocked.Read(ref drainUntil))
            {
                UsageReport next;
                lock (pendingReports)
                {
                    if (pendingReports.Count == 0) return;
                    next = pendingReports[0];
                    attempted.Add(next);
                }
                try
                {
                    var response = await PostAsync<UsageResponse>($"/v1/activations/{Uri.EscapeDataString(activation)}/usage", next, bearer, CancellationToken.None).ConfigureAwait(false);
                    // Only a protocol acknowledgement removes the report; anything else keeps it for an unchanged resend.
                    if (response is not { Accepted: true })
                        throw new LeaseException("lease_rejected", "Usage acknowledgement is malformed");
                    lock (pendingReports)
                    {
                        pendingReports.Remove(next);
                        attempted.Remove(next);
                    }
                    if (response?.Overage is { } balance) policy.SyncBalance(balance, PendingOverage());
                }
                catch (Exception error) when (error is LicensingHttpException or LeaseException)
                {
                    LastError = $"usage: {error.Message}";
                    if (Refused(error)) return;
                    if (error is LicensingHttpException { Status: 401 or 404 })
                    {
                        Invalidate();
                        lock (pendingReports) pendingReports.Clear();
                        ScheduleRetry(TimeSpan.Zero);
                        return;
                    }
                    if (error is LicensingHttpException { RetryAfter: { } wait })
                        Interlocked.Exchange(ref usageRetryAt, time.GetTimestamp() + (long)(wait.TotalSeconds * time.TimestampFrequency));
                    Write("warn", $"Usage report failed; it will be resent: {error.Message}");
                    return;
                }
            }
        }
        finally
        {
            exclusive.Release();
        }
    }

    /// <summary>Final usage report and deactivation; waits at most 5 seconds for each.</summary>
    public async Task StopAsync()
    {
        if (stopped) return;
        renewTimer?.Dispose();
        sampleTimer?.Dispose();
        usageTimer?.Dispose();
        // A backlog drain in progress stops after its current request; wait at most 5 seconds.
        Interlocked.Exchange(ref drainUntil, time.GetTimestamp());
        var acquired = await exclusive.WaitAsync(options.ShutdownTimeout).ConfigureAwait(false);
        stopped = true;
        if (acquired) exclusive.Release();
        if (activationId is not { } activation || token is null)
        {
            // An activation still in flight must not install a lease after StopAsync().
            Invalidate();
            return;
        }
        Interlocked.Exchange(ref drainUntil, time.GetTimestamp() + (long)(options.ShutdownTimeout.TotalSeconds * time.TimestampFrequency));
        // Account for the interval since the last sample before the final report.
        Sample();
        await ReportUsageAsync().WaitAsync(options.ShutdownTimeout).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        try
        {
            await PostAsync<object>($"/v1/activations/{Uri.EscapeDataString(activation)}/deactivate",
                new DeactivationRequest(LicensingProtocol.Version, InstanceId, "shutdown"),
                token, CancellationToken.None, options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception error) when (error is LicensingHttpException or LeaseException)
        {
            Write("warn", $"License deactivation failed: {error.Message}");
        }
        Invalidate();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
