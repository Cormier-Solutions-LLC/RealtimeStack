using System.Text;
using Microsoft.Extensions.Configuration;
using RealtimeStack.Plugins.Persistence;

namespace RealtimeStack.Config;

/// <summary>First-run settings read explicitly, without reflection-based configuration binding.</summary>
public sealed record RealtimeStackSetupOptions
{
    public const string SectionName = "RealtimeStackSetup";
    public bool GatewayEnabled { get; init; }
    public bool HttpFallbackEnabled { get; init; }
    public bool ClientEnabled { get; init; }
    public bool PersistenceEnabled { get; init; }
    public RelationalDialect? Provider { get; init; }
    public string TableName { get; init; } = "realtimestack_records";
    public string ConnectionStringName { get; init; } = "RealtimeStackPersistence";
    public int CommandTimeoutSeconds { get; init; } = 30;

    public static RealtimeStackSetupOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var providerText = section["Persistence:Provider"];
        RelationalDialect? provider = null;
        if (!string.IsNullOrWhiteSpace(providerText))
        {
            if (!Enum.TryParse<RelationalDialect>(providerText, true, out var parsed) ||
                !Enum.IsDefined(parsed) || !Enum.GetNames<RelationalDialect>().Contains(providerText.Trim(), StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("RealtimeStackSetup:Persistence:Provider must name a supported database provider.", nameof(configuration));
            provider = parsed;
        }
        return new RealtimeStackSetupOptions
        {
            GatewayEnabled = SetupValue.Boolean(section, "GatewayEnabled"),
            HttpFallbackEnabled = SetupValue.Boolean(section, "HttpFallbackEnabled"),
            ClientEnabled = SetupValue.Boolean(section, "ClientEnabled"),
            PersistenceEnabled = SetupValue.Boolean(section, "Persistence:Enabled"),
            Provider = provider,
            TableName = section["Persistence:TableName"] ?? "realtimestack_records",
            ConnectionStringName = section["Persistence:ConnectionStringName"] ?? "RealtimeStackPersistence",
            CommandTimeoutSeconds = SetupValue.Integer(section, "Persistence:CommandTimeoutSeconds", 30)
        };
    }

    public IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        if (HttpFallbackEnabled && !GatewayEnabled) errors.Add("HTTP fallback requires GatewayEnabled.");
        if (GatewayEnabled && string.IsNullOrWhiteSpace(configuration["Redis:Endpoint"])) errors.Add("Redis:Endpoint is required for the gateway.");
        if (ClientEnabled && (!Uri.TryCreate(configuration["RealtimeStackClient:Endpoint"], UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("ws" or "wss"))) errors.Add("RealtimeStackClient:Endpoint must be an absolute ws or wss URI.");
        if (PersistenceEnabled)
        {
            if (Provider is null || !Enum.IsDefined(Provider.Value)) errors.Add("Select a supported persistence Provider.");
            if (string.IsNullOrWhiteSpace(ConnectionStringName) || string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionStringName)))
                errors.Add("The selected persistence connection string is required through host configuration.");
            if (TableName.Length is < 1 or > 30 || !char.IsAsciiLetter(TableName[0]) || TableName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                errors.Add("Persistence TableName must be a simple ASCII identifier of 1 to 30 characters.");
            if (CommandTimeoutSeconds is < 1 or > 3600) errors.Add("Persistence CommandTimeoutSeconds must be between 1 and 3600.");
        }
        return errors;
    }
}

public static class RealtimeStackFirstRun
{
    /// <summary>All systems start disabled. Supply network identities and secrets through host configuration.</summary>
    public const string Template = """
        {
          "RealtimeStackSetup": {
            "GatewayEnabled": false,
            "HttpFallbackEnabled": false,
            "ClientEnabled": false,
            "Persistence": {
              "Enabled": false,
              "Provider": "Sqlite",
              "ConnectionStringName": "RealtimeStackPersistence",
              "TableName": "realtimestack_records",
              "CommandTimeoutSeconds": 30
            }
          }
        }
        """;

    /// <summary>Creates a new config file. Existing configuration is never overwritten.</summary>
    public static async Task WriteTemplateAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await file.WriteAsync(Encoding.UTF8.GetBytes(Template), cancellationToken).ConfigureAwait(false);
    }
}

internal static class SetupValue
{
    internal static bool Boolean(IConfiguration configuration, string key)
    {
        var text = configuration[key];
        if (text is null) return false;
        return bool.TryParse(text, out var value) ? value : throw new ArgumentException($"Configuration key {key} must be a boolean.", nameof(configuration));
    }
    internal static int Integer(IConfiguration configuration, string key, int defaultValue)
    {
        var text = configuration[key];
        if (text is null) return defaultValue;
        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException($"Configuration key {key} must be an integer.", nameof(configuration));
    }
}
