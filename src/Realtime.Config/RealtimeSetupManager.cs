using System.Data.Common;
using Realtime.Plugins.Persistence;

namespace Realtime.Config;

public enum RealtimeSetupState { Configured, Ready, Unavailable }
public sealed record RealtimeSetupStatus(string Id, RealtimeSetupState State, string Message);

/// <summary>Custom plugins can participate in registration, inspection, and explicit first-time setup.</summary>
public interface IRealtimeSetupStep
{
    string Id { get; }
    Task<RealtimeSetupStatus> InspectAsync(CancellationToken cancellationToken = default);
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class RealtimeSetupManager(IEnumerable<IRealtimeSetupStep> steps)
{
    private readonly IRealtimeSetupStep[] steps = ValidateSteps(steps);

    public async Task<IReadOnlyList<RealtimeSetupStatus>> InspectAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RealtimeSetupStatus>();
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await step.InspectAsync(cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>Explicit operator action. Never invoked automatically during registration or application startup.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await step.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static IRealtimeSetupStep[] ValidateSteps(IEnumerable<IRealtimeSetupStep> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToArray();
        if (result.Any(step => string.IsNullOrWhiteSpace(step.Id)) || result.Select(step => step.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new ArgumentException("Setup step IDs must be nonempty and unique.", nameof(values));
        return result;
    }
}

internal sealed class PersistenceSetupStep(RelationalPersistenceStore store, Func<DbConnection> connections,
    Action<DbCommand> configureCommand) : IRealtimeSetupStep
{
    private static readonly DurableStreamKey Probe = new("setup", "setup", "schema-probe", DurableRecordKind.Message);
    public string Id => "persistence";

    public async Task<RealtimeSetupStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await store.ReadLatestAsync(Probe, cancellationToken).ConfigureAwait(false);
            return new(Id, RealtimeSetupState.Ready, "Persistence connection and record query succeeded.");
        }
        catch (DbException)
        {
            // Provider exceptions may contain connection strings or credentials. Do not return them to a setup UI.
            return new(Id, RealtimeSetupState.Unavailable, "Persistence is unavailable. Check connectivity, credentials, permissions, and schema.");
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if ((await InspectAsync(cancellationToken).ConfigureAwait(false)).State == RealtimeSetupState.Ready) return;
        await using var connection = connections();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = store.CreateTableSql;
        configureCommand(command);
        try { await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbException)
        {
            // Another setup process may have created the table concurrently. Verify before accepting the outcome.
            if ((await InspectAsync(cancellationToken).ConfigureAwait(false)).State != RealtimeSetupState.Ready) throw;
        }
        if ((await InspectAsync(cancellationToken).ConfigureAwait(false)).State != RealtimeSetupState.Ready)
            throw new InvalidOperationException("Persistence schema could not be verified after setup.");
    }
}

internal sealed class ConfiguredSetupStep(string id, string message) : IRealtimeSetupStep
{
    public string Id => id;
    public Task<RealtimeSetupStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RealtimeSetupStatus(Id, RealtimeSetupState.Configured, message));
    }
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
