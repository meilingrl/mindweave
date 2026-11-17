using Microsoft.Extensions.Logging;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

/// <summary>
/// Outcome of <see cref="IConversationDeletion.DeleteAsync"/>. <paramref name="Scope"/> is the scope as last computed:
/// what was deleted, or, for a refusal, what deleting would take now (a changed scope is shown again for confirming).
/// <paramref name="BackupPath"/> is the backup a cascading delete made first.
/// </summary>
public sealed record ConversationDeleteOutcome(ConversationDeleteStatus Status, ConversationDeleteScope Scope, string? BackupPath = null)
{
    public bool IsDeleted => Status == ConversationDeleteStatus.Deleted;
}

/// <summary>
/// Deleting conversations with a stated scope (requirements v0.12 §8.2, §9.5; architecture §9.1): one conversation or
/// a sidebar selection, each with all its branches and the summaries of all of them.
/// </summary>
public interface IConversationDeletion
{
    /// <summary>What deleting <paramref name="selected"/> would take, and what in it still runs.</summary>
    Task<ConversationDeleteScope> InspectAsync(IReadOnlyCollection<ConversationId> selected, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the scope the user confirmed. Refused while storage is paused (a <see cref="StorageWriteException"/>,
    /// nothing changed), while a user-started answer or summary in the scope runs or is queued, or when the scope is no
    /// longer the confirmed one. A delete that takes branches or summaries along is backed up first and does nothing
    /// when the backup fails; a plain delete of conversations without either needs no backup.
    /// </summary>
    Task<ConversationDeleteOutcome> DeleteAsync(ConversationDeleteScope confirmed, CancellationToken cancellationToken);
}

public sealed partial class ConversationDeletionService(
    IConversationArchiveStore store,
    IDatabaseBackup backup,
    StorageHealth health,
    GenerationCoordinator generation,
    ConversationService conversations,
    ILogger<ConversationDeletionService> logger,
    IResourceBlobStore? blobs = null) : IConversationDeletion
{
    public Task<ConversationDeleteScope> InspectAsync(IReadOnlyCollection<ConversationId> selected, CancellationToken cancellationToken) =>
        store.InspectDeleteAsync(selected, cancellationToken);

    public async Task<ConversationDeleteOutcome> DeleteAsync(ConversationDeleteScope confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        health.ThrowIfBlocked();

        // Looked at first so a refusal costs no backup; the transaction looks again.
        var scope = await store.InspectDeleteAsync(confirmed.Selected, cancellationToken).ConfigureAwait(false);
        if (scope.IsEmpty)
        {
            return new ConversationDeleteOutcome(ConversationDeleteStatus.Missing, scope);
        }

        if (scope.Busy.Count > 0)
        {
            return new ConversationDeleteOutcome(ConversationDeleteStatus.Busy, scope);
        }

        if (!scope.SameContents(confirmed))
        {
            return new ConversationDeleteOutcome(ConversationDeleteStatus.ScopeChanged, scope);
        }

        string? path = null;
        if (scope.IsCascade)
        {
            try
            {
                path = await backup.BackupAsync(BackupReason.BeforeConversationDelete, cancellationToken).ConfigureAwait(false);
                if (blobs is not null && scope.ResourceHashes.Count > 0)
                {
                    await CopyOriginalsAsync(path, scope.ResourceHashes, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Any backup failure means: delete nothing and say so.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogBackupFailed(logger, ex, scope.Conversations.Count);
                return new ConversationDeleteOutcome(ConversationDeleteStatus.BackupFailed, scope);
            }
        }

        // Automatic background work (titles, naming) never blocks a delete (MW-C14): it is cancelled before the
        // transaction, which then removes every task row of the scope. Answers and summaries were checked above and are
        // checked again inside it.
        var members = scope.Conversations.ToHashSet();
        await generation.CancelBackgroundAsync(members).ConfigureAwait(false);

        ConversationDeleteResult result;
        try
        {
            result = await store.DeleteAsync(confirmed, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageWriteException ex)
        {
            Discard(path);
            health.Report(ex);
            throw;
        }

        if (result.Status != ConversationDeleteStatus.Deleted)
        {
            // Nothing was deleted: a backup kept for it would only be a misleading entry on the storage page.
            Discard(path);
            return new ConversationDeleteOutcome(result.Status, result.Scope);
        }

        // A title queued between the cancel and the commit lost its row with the commit; its late write finds nothing.
        await generation.CancelBackgroundAsync(result.Scope.Conversations.ToHashSet()).ConfigureAwait(false);
        conversations.NotifyDeleted(result.Scope);

        // Their resources went with them; originals no backup refers to are removed (docs/design/resources.md).
        await Resources.ResourceService.SweepQuietlyAsync(blobs, logger).ConfigureAwait(false);
        LogDeleted(logger, result.Scope.Roots.Count, result.Scope.Branches, result.Scope.Summaries.Count, path);
        return new ConversationDeleteOutcome(ConversationDeleteStatus.Deleted, result.Scope, path);
    }

    private void Discard(string? path)
    {
        if (path is not null)
        {
            backup.Discard(path);
        }
    }

    /// <summary>The backup also keeps the originals the delete removes (owner's Q4 decision); a backup without them is dropped.</summary>
    private async Task CopyOriginalsAsync(string path, IReadOnlyList<Core.Resources.ContentHash> hashes, CancellationToken cancellationToken)
    {
        try
        {
            await blobs!.CopyToBackupAsync(path, hashes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            backup.Discard(path);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleted {Conversations} conversations with {Branches} branches and {Summaries} summaries; backup {BackupPath}")]
    private static partial void LogDeleted(ILogger logger, int conversations, int branches, int summaries, string? backupPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup before deleting {Count} conversations failed; nothing deleted")]
    private static partial void LogBackupFailed(ILogger logger, Exception exception, int count);
}
