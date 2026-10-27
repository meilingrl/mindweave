using System.Collections.Immutable;

namespace Mindweave.Core.Summaries;

public enum SummaryVersionOrigin { Generated, Manual }
// Identity deliberately contains no title/body: referenced identities may outlive deleted content.
public sealed record SummaryVersionIdentity(Guid Id, Guid NodeId, long Ordinal, SummaryVersionOrigin Origin, DateTimeOffset CreatedAt);
public sealed record SummaryVersionHeader(SummaryVersionIdentity Identity, string Title, Guid? BaselineVersionId, string? RequestedModelId, string? ActualModelId);
public sealed record SummaryContent(string Title, string Markdown);
public sealed record SummaryVersion(SummaryVersionHeader Header, string Markdown, KnowledgeSummaryCoverage Coverage);
public sealed record SummaryNode(Guid Id, Guid WorkspaceId, Guid ConversationId, Guid CurrentVersionId, Guid? CandidateVersionId,
    long Revision, string BaseTitle, string DisplayName, bool NameIsManual, long NameRevision, DateTimeOffset CreatedAt);
public sealed record SummaryNodeToken(Guid NodeId, long Revision, Guid CurrentVersionId, Guid? CandidateVersionId, long DraftEpoch, long DraftRevision);
public sealed record SummaryVersionState(SummaryNode Node, ImmutableArray<SummaryVersionHeader> Versions, bool HasDirtyDraft,
    long DraftEpoch, long DraftRevision, ImmutableArray<Guid> ActiveSummaryTaskIds)
{
    public SummaryNodeToken Token => new(Node.Id, Node.Revision, Node.CurrentVersionId, Node.CandidateVersionId, DraftEpoch, DraftRevision);
}
public sealed record SummaryNodeSnapshot(SummaryVersionState State, bool CurrentIsRead, bool HasLaterContent);
public sealed record SummaryPointerChange(Guid CurrentVersionId, Guid? CandidateVersionId);
public sealed record SummaryReplacementConfirmation(SummaryNodeToken Token, Guid CandidateVersionId, Guid ReplacedCurrentVersionId);
public sealed record SummaryAdoptionConflict(SummaryNodeToken Token, SummaryVersionHeader Current, SummaryVersionHeader Candidate);
public sealed record SummaryDeletionImpact(bool ChangesCurrent, bool AffectsCandidate, bool AffectsDirtyDraft, ImmutableArray<Guid> AffectedActiveTaskIds);
public sealed record SummaryDeletionPreview(SummaryNodeToken Token, ImmutableArray<Guid> VersionIds, Guid? FallbackVersionId, bool DeletesNode, SummaryDeletionImpact Impact);
public sealed record SummarySelection(Guid WorkspaceId, Guid NodeId, Guid VersionId);
public sealed record SummarySelectionState(SummarySelection? Selection, long Revision);
public enum SummaryOutcomeKind { Applied, NoChange, Conflict, Blocked, Obsolete, NotFound, Rejected }
public sealed record SummaryOutcome<T>(SummaryOutcomeKind Kind, T? Value = default, string? Code = null, SummaryAdoptionConflict? AdoptionConflict = null);
