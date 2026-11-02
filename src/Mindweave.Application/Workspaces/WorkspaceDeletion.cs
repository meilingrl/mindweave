using Microsoft.Extensions.Logging;
using Mindweave.Application.Conversations;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Workspaces;

public enum WorkspaceDeleteStatus
{
    /// <summary>The workspace and everything in it are gone.</summary>
    Deleted,

    /// <summary>An answer or summary in it is queued or running; nothing was deleted.</summary>
    Busy,

    /// <summary>The backup made first did not succeed, so nothing was deleted.</summary>
    BackupFailed,

    /// <summary>It no longer existed.</summary>
    Missing,
}

/// <summary>
/// Outcome of <see cref="IWorkspaceDeletion.DeleteAsync"/>. <paramref name="Next"/> is the most recently used workspace
/// that remains (null when none does); <paramref name="BackupPath"/> is the backup made before deleting.
/// </summary>
public sealed record WorkspaceDeleteOutcome(
    WorkspaceDeleteStatus Status,
    IReadOnlyList<BusyConversation> Busy,
    WorkspaceId? Next = null,
    string? BackupPath = null)
{
    public static WorkspaceDeleteOutcome Refused(IReadOnlyList<BusyConversation> busy) => new(WorkspaceDeleteStatus.Busy, busy);
}

/// <summary>Deleting a whole topic workspace (S7 round 4, user decision of 2026-11-02; departs from requirements §5.1).</summary>
public interface IWorkspaceDeletion
{
    /// <summary>How many conversations and summaries would go, and what is still running.</summary>
    Task<WorkspaceContents> InspectAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// Refused while storage is paused (a <see cref="StorageWriteException"/>, nothing changed) or while work in the
    /// workspace runs. Otherwise a verified backup is made first; only when it succeeded is everything deleted, in one
    /// transaction, and the background titles and naming of its conversations are stopped.
    /// </summary>
    Task<WorkspaceDeleteOutcome> DeleteAsync(WorkspaceId workspace, CancellationToken cancellationToken);
}

public sealed partial class WorkspaceDeletionService(
    IWorkspaceStore store,
    IDatabaseBackup backup,
    StorageHealth health,
    GenerationCoordinator generation,
    ILogger<WorkspaceDeletionService> logger) : IWorkspaceDeletion
{
    public Task<WorkspaceContents> InspectAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        store.InspectAsync(workspace, cancellationToken);

    public async Task<WorkspaceDeleteOutcome> DeleteAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        health.ThrowIfBlocked();

        // Checked before the backup so a refusal costs nothing; the delete checks again inside its transaction.
        var contents = await store.InspectAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (!contents.Exists)
        {
            return new WorkspaceDeleteOutcome(WorkspaceDeleteStatus.Missing, []);
        }

        if (contents.Busy.Count > 0)
        {
            return WorkspaceDeleteOutcome.Refused(contents.Busy);
        }

        string path;
        try
        {
            path = await backup.BackupAsync(BackupReason.BeforeWorkspaceDelete, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Any backup failure means: delete nothing and say so.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackupFailed(logger, ex, workspace);
            return new WorkspaceDeleteOutcome(WorkspaceDeleteStatus.BackupFailed, []);
        }

        WorkspaceDeleteResult result;
        try
        {
            result = await store.DeleteAsync(workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageWriteException ex)
        {
            health.Report(ex);
            throw;
        }

        // Refused inside the transaction (work started after the first look, or it went meanwhile): nothing was
        // deleted, so the backup made for it would only be a misleading "删除工作区前" entry on the storage page.
        if (result.Missing)
        {
            backup.Discard(path);
            return new WorkspaceDeleteOutcome(WorkspaceDeleteStatus.Missing, []);
        }

        if (!result.IsDeleted)
        {
            backup.Discard(path);
            return new WorkspaceDeleteOutcome(WorkspaceDeleteStatus.Busy, result.Busy);
        }

        // Their task rows are gone with the commit; a title or naming still running is stopped and its late write finds nothing.
        await generation.CancelBackgroundAsync(result.Deleted.ToHashSet()).ConfigureAwait(false);
        LogDeleted(logger, workspace, result.Deleted.Count, path);
        return new WorkspaceDeleteOutcome(WorkspaceDeleteStatus.Deleted, [], result.Next, path);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleted workspace {WorkspaceId} with {Conversations} conversations; backup {BackupPath}")]
    private static partial void LogDeleted(ILogger logger, WorkspaceId workspaceId, int conversations, string backupPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup before deleting workspace {WorkspaceId} failed; nothing deleted")]
    private static partial void LogBackupFailed(ILogger logger, Exception exception, WorkspaceId workspaceId);
}
