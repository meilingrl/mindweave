using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.ManualBackup;
using Mindweave.Application.Reading;
using Mindweave.Application.Share;
using Mindweave.Application.Startup;
using Mindweave.Application.Summaries;

namespace Mindweave.Application.Storage;

/// <summary>Saves everything kept in memory before the data is closed (architecture §12.3 step 4).</summary>
public interface ISaveBeforeClose
{
    /// <summary>
    /// True when nothing the user wrote or received is left unsaved; page state alone may degrade. Idempotent: what is
    /// already saved is not written again.
    /// </summary>
    Task<bool> SaveAsync();
}

/// <summary>
/// The one list of what is saved before the data is closed (health check architecture-5, D-042). The exit sequence runs it
/// before quitting and before a restore keeps its copy of the current data; the end of a session runs it again as the
/// last resort, right before the database closes. A new owner of writes kept in memory is added here, and both use it.
/// The caller stops generation first, so no answer or summary writes during or after it.
/// </summary>
public sealed class SaveBeforeClose(
    ISummaryExport exports,
    IShares shares,
    IManualBackups manualBackups,
    ISummaries summaries,
    IReadingState reading,
    IConversationMarks marks,
    IGenerationExit generation,
    IDatabaseLifecycle database) : ISaveBeforeClose
{
    public async Task<bool> SaveAsync()
    {
        // A file still being written: an export or a share is finished, so it is not cut off and leaves no temporary
        // file; a manual backup can take long, so it is stopped, which removes its temporary file.
        await Task.WhenAll(exports.WhenIdleAsync(), shares.WhenIdleAsync(), manualBackups.StopAsync()).ConfigureAwait(false);

        // Summary edit drafts (also those still inside their autosave delay), message drafts and reading positions, the
        // new-answer marks, and the terminal writes of stopped answers that failed.
        var summaryDrafts = await summaries.FlushDraftsAsync().ConfigureAwait(false);
        var drafts = await reading.FlushAsync().ConfigureAwait(false);
        await marks.FlushAsync().ConfigureAwait(false);
        var answers = await generation.SaveUnsavedAsync().ConfigureAwait(false);

        // Then the write queue, and a best-effort checkpoint.
        await database.DrainAsync().ConfigureAwait(false);
        return summaryDrafts && drafts && answers;
    }
}
