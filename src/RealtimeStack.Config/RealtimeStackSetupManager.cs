using System.Data.Common;
using RealtimeStack.Plugins.Persistence;

namespace RealtimeStack.Config;

public enum RealtimeStackSetupState { Configured, Ready, Unavailable }
public sealed record RealtimeStackSetupStatus(string Id, RealtimeStackSetupState State, string Message);

/// <summary>Custom plugins can participate in registration, inspection, and explicit first-time setup.</summary>
public interface IRealtimeStackSetupStep
{
    string Id { get; }
    Task<RealtimeStackSetupStatus> InspectAsync(CancellationToken cancellationToken = default);
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class RealtimeStackSetupManager(IEnumerable<IRealtimeStackSetupStep> steps)
{
    private readonly IRealtimeStackSetupStep[] steps = ValidateSteps(steps);

    public async Task<IReadOnlyList<RealtimeStackSetupStatus>> InspectAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RealtimeStackSetupStatus>();
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

    private static IRealtimeStackSetupStep[] ValidateSteps(IEnumerable<IRealtimeStackSetupStep> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToArray();
        if (result.Any(step => string.IsNullOrWhiteSpace(step.Id)) || result.Select(step => step.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new ArgumentException("Setup step IDs must be nonempty and unique.", nameof(values));
        return result;
    }
}

internal sealed class PersistenceSetupStep(RelationalPersistenceStore store, Func<DbConnection> connections,
    Action<DbCommand> configureCommand) : IRealtimeStackSetupStep
{
    private static readonly DurableStreamKey Probe = new("setup", "setup", "schema-probe", DurableRecordKind.Message);
    public string Id => "persistence";

    public async Task<RealtimeStackSetupStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await store.ReadLatestAsync(Probe, cancellationToken).ConfigureAwait(false);
            return new(Id, RealtimeStackSetupState.Ready, "Persistence connection and record query succeeded.");
        }
        catch (DbException)
        {
            // Provider exceptions may contain connection strings or credentials. Do not return them to a setup UI.
            return new(Id, RealtimeStackSetupState.Unavailable, "Persistence is unavailable. Check connectivity, credentials, permissions, and schema.");
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if ((await InspectAsync(cancellationToken).ConfigureAwait(false)).State == RealtimeStackSetupState.Ready) return;
        await using var connection = connections();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = store.CreateTableSql;
        configureCommand(command);
        try { await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbException)
        {
            // Another setup process may have created the table concurrently. Verify before accepting the outcome.
            if ((await InspectAsync(cancellationToken).ConfigureAwait(false)).State != RealtimeStackSetupState.Ready) throw;
        }
        if ((await InspectAsync(cancellationToken).ConfigureAwait(false)).State != RealtimeStackSetupState.Ready)
            throw new InvalidOperationException("Persistence schema could not be verified after setup.");
    }
}

internal sealed class ConfiguredSetupStep(string id, string message) : IRealtimeStackSetupStep
{
    public string Id => id;
    public Task<RealtimeStackSetupStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RealtimeStackSetupStatus(Id, RealtimeStackSetupState.Configured, message));
    }
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
