using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

/// <summary>
/// The summary use cases the pages use (course requirement C3). Implemented by <see cref="SummaryService"/>; only what
/// pages and the exit call is here.
/// </summary>
public interface ISummaries
{
    /// <inheritdoc cref="SummaryService.Changed"/>
    event EventHandler<ConversationId>? Changed;

    Task<SummaryEntry?> ForConversationAsync(ConversationId conversation, CancellationToken cancellationToken);

    Task<SummaryDetail?> GetAsync(SummaryNodeId node, CancellationToken cancellationToken);

    Task<SummaryTaskStatus?> LatestTaskAsync(ConversationId conversation, CancellationToken cancellationToken);

    Task<SummarizeOutcome> SummarizeAsync(ConversationId conversation, CancellationToken cancellationToken);

    Task StopAsync(GenerationTaskId task);

    Task<AdoptCheck?> CheckAdoptAsync(SummaryNodeId node, CancellationToken cancellationToken);

    Task AdoptAsync(SummaryNodeId node, SummaryVersionId candidate, CancellationToken cancellationToken);

    Task DiscardAsync(SummaryNodeId node, SummaryVersionId candidate, CancellationToken cancellationToken);

    Task SetCurrentAsync(SummaryNodeId node, SummaryVersionId version, CancellationToken cancellationToken);

    Task<SummaryVersionId?> DeleteVersionsAsync(SummaryNodeId node, IReadOnlySet<SummaryVersionId> versions, CancellationToken cancellationToken);

    Task<DraftRead> OpenDraftAsync(SummaryNodeId node, SummaryVersionId current, CancellationToken cancellationToken);

    long? SetDraft(SummaryNodeId node, long generation, SummaryVersionId baseline, string title, string body);

    Task DiscardDraftAsync(SummaryNodeId node, long generation, long revision, CancellationToken cancellationToken);

    Task<SummaryVersionId?> SaveEditAsync(
        SummaryNodeId node, SummaryVersionId baseline, string title, string body, long generation, long revision, CancellationToken cancellationToken);

    Task SetReadAsync(SummaryNodeId node, SummaryVersionId version, bool read, CancellationToken cancellationToken);

    Task<bool> RecordProgressAsync(SummaryNodeId node, SummaryVersionId version, double progress, string? locator, TimeSpan visible, CancellationToken cancellationToken);

    Task RenameAsync(SummaryNodeId node, string displayName, CancellationToken cancellationToken);

    /// <summary>Writes every unsaved summary edit draft now (the exit); false when one is left unsaved.</summary>
    Task<bool> FlushDraftsAsync();

    /// <summary>The names of the summaries holding edits that can be neither saved nor stored as a draft (the exit names them).</summary>
    Task<IReadOnlyList<string>> UnsaveableDraftsAsync(CancellationToken cancellationToken);
}
