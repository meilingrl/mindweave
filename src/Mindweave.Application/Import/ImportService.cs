using Microsoft.Extensions.Logging;
using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Import;

/// <summary>Bringing a shared file in, as the import dialog and the pages use it.</summary>
public interface IImports
{
    /// <summary>The workspace of each of the file's conversations that the store has, and of the parents outside it.</summary>
    Task<IReadOnlyDictionary<ConversationId, WorkspaceId>> HoldingsAsync(ExchangeDocument document, CancellationToken cancellationToken);

    /// <summary>What of the file's contents the store has.</summary>
    Task<StoredContents> ContentsAsync(ExchangeDocument document, CancellationToken cancellationToken);

    /// <summary>
    /// Imports in one transaction. Refused while storage is paused (a <see cref="StorageWriteException"/>, nothing changed).
    /// </summary>
    Task<ImportResult> ImportAsync(ExchangeDocument document, ImportTarget target, CancellationToken cancellationToken);
}

public sealed partial class ImportService(
    IImportStore store,
    ConversationService conversations,
    Reading.ReadingStateService reading,
    StorageHealth health,
    IClock clock,
    ILogger<ImportService> logger,
    Summaries.SummaryService summaries,
    SummaryLinks.SummaryLinkService links) : IImports
{
    public Task<IReadOnlyDictionary<ConversationId, WorkspaceId>> HoldingsAsync(ExchangeDocument document, CancellationToken cancellationToken) =>
        store.HoldingsAsync([.. ImportRules.Compared(document)], cancellationToken);

    public Task<StoredContents> ContentsAsync(ExchangeDocument document, CancellationToken cancellationToken) =>
        store.ContentsAsync(document, cancellationToken);

    public async Task<ImportResult> ImportAsync(ExchangeDocument document, ImportTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        health.ThrowIfBlocked();
        ImportResult result;
        try
        {
            result = await store.ImportAsync(document, target, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageWriteException ex)
        {
            health.Report(ex);
            throw;
        }

        if (result.Status == ImportStatus.Imported)
        {
            reading.Forget(result.Created);
            LogImported(logger, document.Kind, result.Created.Count, result.Gained.Count);
            conversations.NotifyImported(result);
            summaries.NotifyImported(result.Summarized);
            if (result.Linked is { } workspace)
            {
                links.RaiseChanged(workspace);
            }
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported a share ({Kind}): {Conversations} conversations are new, {Gained} gained something")]
    private static partial void LogImported(ILogger logger, string kind, int conversations, int gained);
}
