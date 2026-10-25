using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Startup;

/// <summary>What the root navigation should show after the database step.</summary>
public abstract record StartupOutcome
{
    private StartupOutcome()
    {
    }

    public sealed record NeedsRecovery(DatabaseOpenResult Result, IReadOnlyList<BackupInfo> Backups) : StartupOutcome;

    public sealed record NeedsFirstWorkspace : StartupOutcome;

    public sealed record ReadyToWork(IReadOnlyList<Workspace> Workspaces, PageLocation? LastLocation) : StartupOutcome;
}

/// <summary>
/// Runs after the single-instance lock is held: open/migrate the database, then decide between
/// the recovery page, first-run onboarding and the workbench. Nothing here starts AI work.
/// </summary>
public sealed partial class StartupCoordinator(
    IDatabaseLifecycle database,
    IWorkspaceStore workspaces,
    IConversationStore conversations,
    IClock clock,
    ILocationStore locations,
    ILogger<StartupCoordinator> logger)
{
    public async Task<StartupOutcome> StartAsync(CancellationToken cancellationToken)
    {
        var result = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ContinueAfterOpenAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StartupOutcome> RestoreAsync(BackupInfo backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        LogRestoring(logger, backup.Path);
        var result = await database.RestoreAsync(backup, cancellationToken).ConfigureAwait(false);
        return await ContinueAfterOpenAsync(result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StartupOutcome> ContinueAfterOpenAsync(DatabaseOpenResult result, CancellationToken cancellationToken)
    {
        if (!result.IsReady)
        {
            LogNotReady(logger, result.Status, result.DatabasePath, result.ErrorDetail);
            return new StartupOutcome.NeedsRecovery(result, database.ListBackups());
        }

        // Before anything can start new AI work: whatever the previous process left unfinished is
        // interrupted, never resumed or re-sent.
        var recovered = await conversations.RecoverUnfinishedAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (recovered.InterruptedAnswers + recovered.InterruptedTasks > 0)
        {
            LogRecovered(logger, recovered.InterruptedAnswers, recovered.InterruptedTasks);
        }

        var all = await workspaces.ListAsync(cancellationToken).ConfigureAwait(false);
        if (all.Count == 0)
        {
            return new StartupOutcome.NeedsFirstWorkspace();
        }

        PageLocation? last;
        try
        {
            last = await locations.GetLastAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Last page is a preference: any failure degrades to the default page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLastLocationUnavailable(logger, ex);
            last = null;
        }

        if (last is not null && all.All(w => w.Id != last.WorkspaceId))
        {
            last = null;
        }

        return new StartupOutcome.ReadyToWork(all, last);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marked {Answers} unfinished answers and {Tasks} tasks from the previous run as interrupted")]
    private static partial void LogRecovered(ILogger logger, int answers, int tasks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database not ready: {Status} at {Path}. {Detail}")]
    private static partial void LogNotReady(ILogger logger, DatabaseOpenStatus status, string path, string? detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restoring database from backup {Path}")]
    private static partial void LogRestoring(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Last page could not be read; falling back to the default page")]
    private static partial void LogLastLocationUnavailable(ILogger logger, Exception exception);
}
