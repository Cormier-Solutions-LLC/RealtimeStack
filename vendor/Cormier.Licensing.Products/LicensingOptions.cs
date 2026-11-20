namespace Cormier.Licensing.Products;

/// <summary>Licensing client settings. The license key is never logged.</summary>
public sealed class LicensingOptions
{
    /// <summary>License key; sent only in the activation request.</summary>
    public string? LicenseKey { get; init; }
    /// <summary>LicensingServer address (HTTPS; plain HTTP only for localhost).</summary>
    public Uri ServerUrl { get; init; } = new(LicensingProtocol.DefaultServerUrl);
    /// <summary>Lease verification keys by key ID: raw 32-byte Ed25519 keys, base64 or base64url.</summary>
    public IReadOnlyDictionary<string, string> PublicKeys { get; init; } = new Dictionary<string, string>();
    /// <summary>Licensed application name, for example ProofStudio.Server.</summary>
    public required string Application { get; init; }
    /// <summary>Application version.</summary>
    public required string ApplicationVersion { get; init; }
    /// <summary>kubernetes, docker, linux, windows, hosted or embedded.</summary>
    public required string DeploymentType { get; init; }
    /// <summary>Stable instance identifier (pod name, container ID); generated when omitted.</summary>
    public string? InstanceId { get; init; }
    /// <summary>Host name reported at activation.</summary>
    public string? Hostname { get; init; }
    /// <summary>Slots of the work gate when unthrottled.</summary>
    public int GateSlots { get; init; } = 8;
    /// <summary>Run renewal, sampling and usage timers (default true). Tests drive the client by hand.</summary>
    public bool Schedule { get; init; } = true;
    /// <summary>How often usage is sampled and the rules applied.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>How often usage is reported.</summary>
    public TimeSpan UsageInterval { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Longest wait for each shutdown step.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Per-request timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Settings from environment variables: PROOF_LICENSE_KEY or PROOF_LICENSE_KEY_FILE,
    /// PROOF_LICENSE_SERVER_URL, PROOF_LICENSE_PUBLIC_KEYS (keyId=key, comma separated) or
    /// PROOF_LICENSE_PUBLIC_KEYS_FILE, PROOF_LICENSE_DEPLOYMENT (detected when unset) and
    /// PROOF_INSTANCE_ID (default HOSTNAME).
    /// </summary>
    public static LicensingOptions FromEnvironment(string application, string applicationVersion, Func<string, string?>? getVariable = null, Func<string, string>? readFile = null)
    {
        var original = getVariable ?? Environment.GetEnvironmentVariable;
        string? env(string name) => original(name.Replace("PROOF_LICENSE_", "CORMIER_LICENSE_").Replace("PROOF_INSTANCE_ID", "CORMIER_INSTANCE_ID")) ?? original(name);
        var read = readFile ?? File.ReadAllText;
        string? Value(string name) => env(name) is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        var key = Value("PROOF_LICENSE_KEY");
        if (key is null && Value("PROOF_LICENSE_KEY_FILE") is { } keyFile)
        {
            try { key = read(keyFile).Trim(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("PROOF_LICENSE_KEY_FILE cannot be read");
            }
        }
        var keysText = Value("PROOF_LICENSE_PUBLIC_KEYS") ?? (Value("PROOF_LICENSE_PUBLIC_KEYS_FILE") is { } keysFile ? read(keysFile) : null);
        var deployment = Value("PROOF_LICENSE_DEPLOYMENT")?.ToLowerInvariant();
        if (deployment is not null && !LicensingProtocol.DeploymentTypes.Contains(deployment))
            throw new InvalidOperationException($"PROOF_LICENSE_DEPLOYMENT must be one of {string.Join(", ", LicensingProtocol.DeploymentTypes)}");
        return new()
        {
            LicenseKey = string.IsNullOrEmpty(key) ? null : key,
            ServerUrl = new Uri(Value("PROOF_LICENSE_SERVER_URL") ?? LicensingProtocol.DefaultServerUrl),
            PublicKeys = ParsePublicKeys(keysText),
            Application = application,
            ApplicationVersion = applicationVersion,
            DeploymentType = deployment ?? DetectDeploymentType(env),
            InstanceId = Value("PROOF_INSTANCE_ID") ?? Value("HOSTNAME"),
        };
    }

    /// <summary>Parse <c>keyId=key</c> pairs separated by commas, semicolons or whitespace.</summary>
    public static IReadOnlyDictionary<string, string> ParsePublicKeys(string? text)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in (text ?? "").Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var at = entry.IndexOf('=');
            if (at <= 0 || at == entry.Length - 1) throw new InvalidOperationException("PROOF_LICENSE_PUBLIC_KEYS entries must be keyId=publicKey");
            keys[entry[..at]] = entry[(at + 1)..];
        }
        return keys;
    }

    /// <summary>kubernetes when KUBERNETES_SERVICE_HOST is set, docker inside a Linux container, otherwise the OS.</summary>
    public static string DetectDeploymentType(Func<string, string?>? getVariable = null)
    {
        var env = getVariable ?? Environment.GetEnvironmentVariable;
        if (!string.IsNullOrEmpty(env("KUBERNETES_SERVICE_HOST"))) return "kubernetes";
        if (OperatingSystem.IsLinux() && (File.Exists("/.dockerenv") || File.Exists("/run/.containerenv") || env("DOTNET_RUNNING_IN_CONTAINER") == "true"))
            return "docker";
        return OperatingSystem.IsWindows() ? "windows" : "linux";
    }
}
