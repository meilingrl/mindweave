using System.Collections.Immutable;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

public interface ISummaryVersionStore
{
    Task<ImmutableArray<SummaryNodeSnapshot>> ListSummaryNodesAsync(Guid workspaceId);
    Task<SummaryNodeSnapshot?> LoadSummaryNodeAsync(Guid nodeId);
    Task<SummaryNodeSnapshot?> FindConversationSummaryAsync(Guid conversationId);
    Task<ImmutableArray<SummaryVersionHeader>> ListSummaryVersionsAsync(Guid nodeId);
    Task<SummaryVersion?> LoadSummaryVersionAsync(Guid versionId);
    Task<SummaryEditDraft?> LoadSummaryDraftAsync(Guid nodeId);
    // Current only. Begin renews epoch and preserves an existing dirty draft. Save/discard retain epoch/revision tombstones; old autosaves cannot resurrect text.
    Task<SummaryOutcome<SummaryEditDraft>> BeginSummaryEditAsync(SummaryNodeToken expected);
    Task<SummaryOutcome<SummaryEditDraft>> SaveSummaryDraftAsync(SummaryDraftToken expected, SummaryContent content);
    Task<SummaryOutcome<SummaryEditDraft>> DiscardSummaryDraftAsync(SummaryDraftToken expected);
    Task<SummaryOutcome<SummaryVersion>> CommitSummaryDraftAsync(SummaryNodeToken expectedNode, SummaryDraftToken expectedDraft);
    Task<SummaryReadingSnapshot?> LoadSummaryReadingAsync(Guid versionId);
    // expectedRevision=0 for absent state; creates next epoch atomically, resets manual-unread suppression only for the new session.
    Task<SummaryOutcome<SummaryReadingSnapshot>> BeginSummaryReadingAsync(Guid versionId, long expectedRevision, string readableText);
    Task<SummaryOutcome<SummaryReadingSnapshot>> SaveSummaryReadingAsync(Guid versionId, SummaryReadObservation observation, SummaryReadLocation? location);
    Task<SummaryOutcome<SummaryReadingSnapshot>> SetSummaryReadAsync(Guid versionId, long expectedEpoch, long expectedRevision, bool isRead);
    Task<SummaryOutcome<SummaryNodeSnapshot>> AdoptSummaryCandidateAsync(SummaryNodeToken expected, SummaryReplacementConfirmation? confirmation = null);
    Task<SummaryOutcome<SummaryNodeSnapshot>> DiscardSummaryCandidateAsync(SummaryNodeToken expected);
    Task<SummaryOutcome<SummaryNodeSnapshot>> SetCurrentSummaryVersionAsync(SummaryNodeToken expected, Guid versionId);
    // Commit recomputes exact selection/fallback/token inside writer transaction; candidate is a separate explicit operation.
    Task<SummaryOutcome<SummaryDeletionPreview>> PreviewDeleteSummaryVersionsAsync(Guid nodeId, ImmutableArray<Guid> versionIds);
    Task<SummaryOutcome<SummaryDeletionPreview>> DeleteSummaryVersionsAsync(SummaryDeletionPreview confirmedPreview);
    Task<SummaryOutcome<SummaryNode>> RenameSummaryNodeAsync(Guid nodeId, long expectedNameRevision, string displayName);
    Task<SummarySelectionState> LoadSummarySelectionAsync();
    // Alongside legacy Overview selection in one transaction; null clears identity but retains revision tombstone. Never put node IDs into conversation FKs.
    Task<SummaryOutcome<SummarySelectionState>> SaveSummarySelectionAsync(SummarySelection? selection, long expectedRevision);
}
