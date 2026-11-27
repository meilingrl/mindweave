using Microsoft.Extensions.Logging;
using Mindweave.Application.Conversations;
using Mindweave.Application.Import;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.ManualBackup;

/// <summary>Importing a manual backup, as the import dialog uses it (docs/design/sharing-backup.md §2, §5).</summary>
public interface IManualBackupImports
{
    /// <summary>The package's own extension, without the dot.</summary>
    string Extension { get; }

    Task<ManualBackupReading> ReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>What importing it would do now.</summary>
    Task<ManualBackupPlan> PlanAsync(ManualBackupFile file, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the originals that are not here first, as attaching a file does, then everything else in one transaction.
    /// No backup of the database is made before, because an import only adds (§3). Refused while storage is paused (a
    /// <see cref="StorageWriteException"/>, nothing changed).
    /// </summary>
    Task<ManualBackupImported> ImportAsync(ManualBackupFile file, CancellationToken cancellationToken);
}

public sealed partial class ManualBackupImportService(
    IManualBackupReader reader,
    IManualBackupImportStore store,
    IResourceBlobStore blobs,
    ConversationService conversations,
    Reading.ReadingStateService reading,
    StorageHealth health,
    IClock clock,
    ILogger<ManualBackupImportService> logger,
    Summaries.SummaryService? summaries = null,
    SummaryLinks.SummaryLinkService? links = null) : IManualBackupImports
{
    public string Extension => reader.Extension;

    public Task<ManualBackupReading> ReadAsync(string path, CancellationToken cancellationToken) => reader.ReadAsync(path, cancellationToken);

    public async Task<ManualBackupPlan> PlanAsync(ManualBackupFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        return ManualBackupImportRules.Plan(file, await store.StoredAsync(file, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ManualBackupImported> ImportAsync(ManualBackupFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        health.ThrowIfBlocked();
        var pins = new List<BlobWrite>();
        var missing = 0;
        ManualBackupImported result;
        try
        {
            // The originals first, pinned until the rows that name them are committed; one already here is not written again.
            var stored = await store.StoredAsync(file, cancellationToken).ConfigureAwait(false);
            var plan = ManualBackupImportRules.Plan(file, stored);
            foreach (var hash in plan.ArrivingResources(stored).Select(r => r.Hash).Distinct())
            {
                if (blobs.Exists(hash))
                {
                    continue;
                }

                if (!file.Originals.Contains(hash))
                {
                    missing++;
                }
                else
                {
                    var bytes = await reader.ReadOriginalAsync(file.Path, hash, cancellationToken).ConfigureAwait(false);
                    if (bytes is null)
                    {
                        missing++;
                        continue;
                    }

                    var written = await blobs.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    pins.Add(written);
                    if (written.Hash != hash)
                    {
                        missing++;
                    }
                }
            }

            result = await store.ImportAsync(file, clock.UtcNow, cancellationToken).ConfigureAwait(false) with { MissingOriginals = missing };
        }
        catch (StorageWriteException ex)
        {
            health.Report(ex);
            throw;
        }
        finally
        {
            // An original no row names after a failure is removed by the existing cleanup (docs/design/resources.md).
            foreach (var pin in pins)
            {
                pin.Dispose();
            }
        }

        if (result.Status == ManualBackupImportStatus.Imported)
        {
            foreach (var workspace in result.Workspaces)
            {
                var created = workspace.Conversations.Where(c => c.Fit == ManualBackupFit.New).Select(c => c.Conversation.Id).ToList();
                var gained = workspace.Conversations.Where(c => c.Fit != ManualBackupFit.New && c.Writes).Select(c => c.Conversation.Id).ToList();
                reading.Forget(created);
                conversations.NotifyImported(new ImportResult(ImportStatus.Imported, created, [], created.Count > 0 ? created[0] : null) { Gained = gained });
                summaries?.NotifyImported([.. workspace.Conversations.Where(c => c.Versions.Count > 0).Select(c => c.Conversation.Id)]);
                if (workspace.Links.Count > 0)
                {
                    links?.RaiseChanged(workspace.Conversations.First(c => c.Conversation.Id == workspace.Links[0].A).Workspace);
                }
            }

            var (made, arrived, extended, joined) = (result.Workspaces.Count(w => w.Create), result.Workspaces.Sum(w => w.New), result.Workspaces.Sum(w => w.GainsMessages), result.Workspaces.Sum(w => w.Joined));
            LogImported(logger, made, arrived, extended, joined, missing);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported a manual backup: {Workspaces} workspaces and {Conversations} conversations are new, {Gained} gained messages, {Joined} were joined by time, {Missing} originals missing")]
    private static partial void LogImported(ILogger logger, int workspaces, int conversations, int gained, int joined, int missing);
}
