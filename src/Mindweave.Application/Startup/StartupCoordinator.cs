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

/// <summary>What an in-app restore led to: the page to show, and whether the backup is the data now.</summary>
public sealed record InPlaceRestoreOutcome(StartupOutcome Outcome, InPlaceRestoreResult Result);

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
    ILogger<StartupCoordinator> logger,
    Resources.IResources resources)
{
    public async Task<StartupOutcome> StartAsync(CancellationToken cancellationToken)
    {
        var result = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ContinueAfterOpenAsync(result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The backups the recovery page offers.</summary>
    public IReadOnlyList<BackupInfo> ListBackups() => database.ListBackups();

    public async Task<StartupOutcome> RestoreAsync(BackupInfo backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        LogRestoring(logger, backup.Path);
        var result = await database.RestoreAsync(backup, cancellationToken).ConfigureAwait(false);
        return await ContinueAfterOpenAsync(result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restoring from inside the running application (2026-11-12): the database is replaced (or, after a failure, the
    /// previous one put back) and then continues exactly as a start would.
    /// </summary>
    public async Task<InPlaceRestoreOutcome> RestoreInPlaceAsync(BackupInfo backup, string keptCopy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        LogRestoring(logger, backup.Path);
        var result = await database.RestoreInPlaceAsync(backup, keptCopy, cancellationToken).ConfigureAwait(false);
        return new InPlaceRestoreOutcome(await ContinueAfterOpenAsync(result.Open, cancellationToken).ConfigureAwait(false), result);
    }

    private async Task<StartupOutcome> ContinueAfterOpenAsync(DatabaseOpenResult result, CancellationToken cancellationToken)
    {
        if (!result.IsReady)
        {
            LogNotReady(logger, result.Status, result.DatabasePath, result.ErrorDetail);
            return new StartupOutcome.NeedsRecovery(result, database.ListBackups());
        }

        try
        {
            return await LoadAfterOpenAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Health check 2026-11-12: a database that opened but then refuses the first write or read
        // (a full disk, a file made read-only meanwhile) leads to the recovery page, never to a start that throws with
        // no window to explain it. Cancellation still propagates.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailedAfterOpen(logger, ex, result.DatabasePath);
            var failed = result with { Status = DatabaseOpenStatus.IoError, ErrorDetail = ex.Message };
            return new StartupOutcome.NeedsRecovery(failed, database.ListBackups());
        }
    }

    private async Task<StartupOutcome> LoadAfterOpenAsync(CancellationToken cancellationToken)
    {
        // Before anything can start new AI work: whatever the previous process left unfinished is
        // interrupted, never resumed or re-sent.
        var recovered = await conversations.RecoverUnfinishedAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (recovered.InterruptedAnswers + recovered.InterruptedTasks > 0)
        {
            LogRecovered(logger, recovered.InterruptedAnswers, recovered.InterruptedTasks);
        }

        // Unsent attachments no draft holds and originals nothing names go, as do leftover temporary files (architecture
        // §15.2). In the background: it never delays the window and its failure is only logged.
        _ = Task.Run(() => resources.CleanUpAsync(CancellationToken.None), CancellationToken.None);

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

    [LoggerMessage(Level = LogLevel.Error, Message = "Database {Path} opened but could not be used; showing the recovery page")]
    private static partial void LogFailedAfterOpen(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restoring database from backup {Path}")]
    private static partial void LogRestoring(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Last page could not be read; falling back to the default page")]
    private static partial void LogLastLocationUnavailable(ILogger logger, Exception exception);
}
