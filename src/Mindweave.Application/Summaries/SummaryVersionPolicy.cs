using System.Collections.Immutable;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

public static class SummaryVersionPolicy
{
    public static SummaryOutcome<SummaryDeletionPreview> PreviewDelete(SummaryVersionState state, ImmutableArray<Guid> selected)
    {
        if (selected.IsDefaultOrEmpty || selected.Distinct().Count() != selected.Length ||
            selected.Any(id => !OwnVersion(state, id))) return new(SummaryOutcomeKind.Rejected, Code: "InvalidVersionSelection");
        if (state.Node.CandidateVersionId is { } candidate && selected.Contains(candidate))
            return new(SummaryOutcomeKind.Blocked, Code: "DiscardCandidateSeparately");
        var changesCurrent = selected.Contains(state.Node.CurrentVersionId);
        var impact = new SummaryDeletionImpact(changesCurrent, changesCurrent && state.Node.CandidateVersionId is not null,
            changesCurrent && state.HasDirtyDraft, changesCurrent ? state.ActiveSummaryTaskIds : []);
        var remaining = state.Versions.Where(v => !selected.Contains(v.Identity.Id) && v.Identity.Id != state.Node.CandidateVersionId).ToArray();
        Guid? fallback = state.Node.CurrentVersionId;
        if (selected.Contains(state.Node.CurrentVersionId))
        {
            var ordinal = state.Versions.Single(v => v.Identity.Id == state.Node.CurrentVersionId).Identity.Ordinal;
            fallback = remaining.Where(v => v.Identity.Ordinal < ordinal).MaxBy(v => v.Identity.Ordinal)?.Identity.Id
                ?? remaining.MaxBy(v => v.Identity.Ordinal)?.Identity.Id;
        }
        var preview = new SummaryDeletionPreview(state.Token, selected.Order().ToImmutableArray(), fallback, remaining.Length == 0, impact);
        // Deleting an unrelated historical body retains referenced baseline identity; it does not modify any pending state.
        var guard = changesCurrent ? StructuralGuard(state) : null;
        return new(guard is null ? SummaryOutcomeKind.Applied : SummaryOutcomeKind.Blocked, preview, guard);
    }

    public static SummaryOutcome<SummaryPointerChange> AdoptCandidate(SummaryVersionState state, SummaryNodeToken expected, SummaryReplacementConfirmation? confirmation)
    {
        if (state.Token != expected) return new(SummaryOutcomeKind.Conflict, Code: "NodeChanged");
        if (state.HasDirtyDraft || !state.ActiveSummaryTaskIds.IsEmpty) return new(SummaryOutcomeKind.Blocked, Code: "ResolveDraftOrActiveTask");
        if (state.Node.CandidateVersionId is not { } candidate || !OwnVersion(state, candidate)) return new(SummaryOutcomeKind.NotFound, Code: "NoCandidate");
        var header = state.Versions.Single(v => v.Identity.Id == candidate);
        if (header.BaselineVersionId != state.Node.CurrentVersionId &&
            confirmation != new SummaryReplacementConfirmation(state.Token, candidate, state.Node.CurrentVersionId))
            return new(SummaryOutcomeKind.Conflict, Code: "CandidateBaselineChanged", AdoptionConflict:
                new(state.Token, state.Versions.Single(v => v.Identity.Id == state.Node.CurrentVersionId), header));
        return new(SummaryOutcomeKind.Applied, new(candidate, null));
    }

    public static SummaryOutcome<SummaryPointerChange> SetCurrent(SummaryVersionState state, Guid versionId, SummaryNodeToken expected)
    {
        if (state.Token != expected) return new(SummaryOutcomeKind.Conflict, Code: "NodeChanged");
        var guard = StructuralGuard(state);
        if (guard is not null) return new(SummaryOutcomeKind.Blocked, Code: guard);
        if (!OwnVersion(state, versionId)) return new(SummaryOutcomeKind.NotFound, Code: "VersionMissing");
        return new(versionId == state.Node.CurrentVersionId ? SummaryOutcomeKind.NoChange : SummaryOutcomeKind.Applied, new(versionId, null));
    }

    public static SummaryOutcome<SummaryGenerationBaseline> Admit(SummaryVersionState? state, Guid plannedNodeId, bool hasActiveReply, bool hasActiveSummary)
    {
        if (hasActiveReply || hasActiveSummary || state is { ActiveSummaryTaskIds.IsEmpty: false })
            return new(SummaryOutcomeKind.Blocked, Code: "ActiveConversationGeneration");
        if (state?.Node.CandidateVersionId is not null) return new(SummaryOutcomeKind.Blocked, Code: "ResolveCandidate");
        if (plannedNodeId == Guid.Empty || (state is not null && state.Node.Id != plannedNodeId))
            return new(SummaryOutcomeKind.Rejected, Code: "InvalidNodeIdentity");
        return new(SummaryOutcomeKind.Applied, new(plannedNodeId, state?.Node.CurrentVersionId, state?.Node.Revision ?? 0, state is null));
    }

    // Store separately verifies the task is active/revision-matched and the new identity is unused. An edit after acceptance does not change the frozen baseline.
    public static SummaryOutcome<SummaryPointerChange> Complete(SummaryVersionState? state, SummaryGenerationBaseline baseline, Guid versionId)
    {
        if (versionId == Guid.Empty || baseline.NodeId == Guid.Empty) return new(SummaryOutcomeKind.Rejected, Code: "InvalidVersionIdentity");
        if (baseline.CreatesNode)
        {
            if (state is not null || baseline.CurrentVersionId is not null) return new(SummaryOutcomeKind.Obsolete, Code: "FirstNodeAlreadyExists");
            return new(SummaryOutcomeKind.Applied, new(versionId, null));
        }
        if (state is null || state.Node.Id != baseline.NodeId) return new(SummaryOutcomeKind.Obsolete, Code: "TargetNodeDeleted");
        if (state.Node.CandidateVersionId is not null) return new(SummaryOutcomeKind.Blocked, Code: "ResolveCandidate");
        if (OwnVersion(state, versionId)) return new(SummaryOutcomeKind.Rejected, Code: "VersionIdentityAlreadyUsed");
        return new(SummaryOutcomeKind.Applied, new(state.Node.CurrentVersionId, versionId));
    }

    public static SummaryOutcome<SummaryDraftToken> BeginEdit(SummaryVersionState state, Guid versionId, SummaryNodeToken expected)
    {
        if (state.Token != expected || versionId != state.Node.CurrentVersionId) return new(SummaryOutcomeKind.Conflict, Code: "OnlyCurrentIsEditable");
        if (!OwnVersion(state, versionId)) return new(SummaryOutcomeKind.NotFound, Code: "VersionMissing");
        // Store preserves any existing dirty content while renewing this lease; old queued autosaves lose their epoch.
        return new(SummaryOutcomeKind.Applied, new(state.Node.Id, versionId, checked(state.DraftEpoch + 1), 0));
    }

    public static SummaryOutcome<SummaryDraftCommit> CommitDraft(SummaryVersionState state, SummaryVersion current, SummaryEditDraft draft, SummaryNodeToken expected)
    {
        if (state.Token != expected) return new(SummaryOutcomeKind.Conflict, Code: "NodeChanged");
        if (draft.Token.NodeId != state.Node.Id || draft.Token.Epoch != state.DraftEpoch || draft.Token.Revision != state.DraftRevision)
            return new(SummaryOutcomeKind.Obsolete, Code: "StaleDraftEpochOrRevision");
        if (current.Header.Identity.NodeId != state.Node.Id || current.Header.Identity.Id != state.Node.CurrentVersionId ||
            draft.Token.BaseVersionId != state.Node.CurrentVersionId) return new(SummaryOutcomeKind.Conflict, Code: "DraftBaselineChanged");
        if (draft.Title is null || draft.Markdown is null) return new(SummaryOutcomeKind.Obsolete, Code: "DraftCleared");
        SummaryContent content;
        try { content = KnowledgeSummaryProtocol.ParseComplete(KnowledgeSummaryProtocol.Serialize(new(draft.Title, draft.Markdown))); }
        catch (SummaryProtocolException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidSummaryContent"); }
        var changed = content.Title != current.Header.Title || content.Markdown != current.Markdown;
        return new(changed ? SummaryOutcomeKind.Applied : SummaryOutcomeKind.NoChange, new(changed, content, current.Header.Identity.Id));
    }

    private static bool OwnVersion(SummaryVersionState state, Guid id) => state.Versions.Any(v => v.Identity.Id == id && v.Identity.NodeId == state.Node.Id);
    private static string? StructuralGuard(SummaryVersionState state) => state.HasDirtyDraft ? "ResolveDirtyDraft" :
        !state.ActiveSummaryTaskIds.IsEmpty ? "ResolveActiveSummary" : state.Node.CandidateVersionId is not null ? "ResolveCandidate" : null;
}
