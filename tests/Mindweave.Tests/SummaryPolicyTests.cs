using System.Collections.Immutable;

using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Xunit;

namespace Mindweave.Tests;

public sealed class SummaryPolicyTests
{
    [Fact]
    public void Manual_unread_rejects_older_progress_epoch()
    {
        var state = SummaryReadPolicy.Open(Guid.NewGuid(), "body", null);
        var unread = SummaryReadPolicy.SetManual(state, false);
        var old = SummaryReadPolicy.Observe(unread, new(state.Epoch, state.Revision, 1, TimeSpan.FromSeconds(4), true, true, true, state.TextHash, [new(0, 4)]));
        Assert.Equal(SummaryOutcomeKind.Obsolete, old.Kind);
        var current = SummaryReadPolicy.Observe(unread, new(unread.Epoch, unread.Revision, 1, TimeSpan.FromSeconds(4), true, true, true, unread.TextHash, [new(0, 4)]));
        Assert.False(current.Value!.IsRead);
        var reopened = SummaryReadPolicy.Open(state.VersionId, "body", current.Value);
        Assert.True(reopened.Epoch > unread.Epoch);
        var start = SummaryReadPolicy.Observe(reopened, new(reopened.Epoch, reopened.Revision, 1, TimeSpan.Zero, true, true, true, reopened.TextHash, [new(0, 4)])).Value!;
        Assert.True(SummaryReadPolicy.Observe(start, new(start.Epoch, start.Revision, 2, TimeSpan.FromSeconds(3), true, true, true, start.TextHash, [new(0, 4)])).Value!.IsRead);
    }

    [Fact]
    public void Read_union_does_not_double_count_or_accrue_hidden_time()
    {
        var state = SummaryReadPolicy.Open(Guid.NewGuid(), new string('a', 100), null);
        state = Observe(state, 1, 0, false, [new(0, 50), new(20, 60)]);
        Assert.False(state.IsRead);
        state = Observe(state, 2, 1, false, [new(60, 84)]);
        Assert.False(state.IsRead);
        state = Observe(state, 3, 2, false, [new(84, 85)]);
        Assert.True(state.IsRead);
        var shortBody = SummaryReadPolicy.Open(Guid.NewGuid(), "a", null);
        shortBody = Observe(shortBody, 1, 0, true, [new(0, 1)]);
        Assert.False(shortBody.IsRead);
        shortBody = SummaryReadPolicy.Observe(shortBody, new(shortBody.Epoch, shortBody.Revision, 2, TimeSpan.FromSeconds(2), false, false, true, shortBody.TextHash, [])).Value!;
        shortBody = Observe(shortBody, 3, 30, true, [new(0, 1)]);
        Assert.False(shortBody.IsRead);
        shortBody = Observe(shortBody, 4, 33, true, [new(0, 1)]);
        Assert.True(shortBody.IsRead);
    }

    private static SummaryReadState Observe(SummaryReadState s, long seq, int seconds, bool shortBody, System.Collections.Immutable.ImmutableArray<ReadableInterval> intervals) =>
        SummaryReadPolicy.Observe(s, new(s.Epoch, s.Revision, seq, TimeSpan.FromSeconds(seconds), true, true, shortBody, s.TextHash, intervals)).Value!;

    [Fact]
    public void Current_deletion_chooses_previous_else_latest_retained()
    {
        var node = Guid.NewGuid();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var state = State(node, ids, 2);
        var result = SummaryVersionPolicy.PreviewDelete(state, [ids[1], ids[2]]);
        Assert.Equal(ids[0], result.Value!.FallbackVersionId);
        result = SummaryVersionPolicy.PreviewDelete(state, [ids[0], ids[1], ids[2]]);
        Assert.Equal(ids[3], result.Value!.FallbackVersionId);
        Assert.True(SummaryVersionPolicy.PreviewDelete(state, ids.ToImmutableArray()).Value!.DeletesNode);
        Assert.Equal(SummaryOutcomeKind.Blocked, SummaryVersionPolicy.PreviewDelete(state with { Node = state.Node with { CandidateVersionId = ids[3] } }, [ids[2]]).Kind);
    }

    [Fact]
    public void Version_guards_preserve_candidate_baseline_and_dirty_edit()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var state = State(Guid.NewGuid(), ids, 1);
        state = state with { Node = state.Node with { CandidateVersionId = ids[2] }, Versions = state.Versions.SetItem(2, state.Versions[2] with { BaselineVersionId = ids[0] }) };
        Assert.Equal(SummaryOutcomeKind.Conflict, SummaryVersionPolicy.AdoptCandidate(state, state.Token, null).Kind);
        Assert.Equal(SummaryOutcomeKind.Applied, SummaryVersionPolicy.AdoptCandidate(state, state.Token, new(state.Token, ids[2], ids[1])).Kind);
        Assert.Equal(SummaryOutcomeKind.Blocked, SummaryVersionPolicy.AdoptCandidate(state with { HasDirtyDraft = true }, state.Token, null).Kind);
        Assert.Equal(SummaryOutcomeKind.Blocked, SummaryVersionPolicy.SetCurrent(state, ids[0], state.Token).Kind);
        Assert.Equal(SummaryOutcomeKind.Conflict, SummaryVersionPolicy.SetCurrent(state with { Node = state.Node with { CandidateVersionId = null } }, ids[0], state.Token with { Revision = 99 }).Kind);
    }

    [Theory]
    [InlineData(" ＡＢＣ\t蒸汽机 \n", "abc 蒸汽机")]
    [InlineData("Cafe\u0301", "CAFÉ")]
    public void Naming_uses_compatibility_whitespace_and_stable_case(string a, string b) => Assert.Equal(SummaryTitleNormalization.GroupKey(a), SummaryTitleNormalization.GroupKey(b));

    [Fact]
    public void Admission_and_completion_preserve_current_edit_and_never_recreate_deleted_node()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var state = State(Guid.NewGuid(), ids, 0);
        var admission = SummaryVersionPolicy.Admit(state, state.Node.Id, false, false);
        Assert.Equal(ids[0], admission.Value!.CurrentVersionId);
        Assert.Equal(SummaryOutcomeKind.Blocked, SummaryVersionPolicy.Admit(state, state.Node.Id, true, false).Kind);
        Assert.Equal(SummaryOutcomeKind.Blocked, SummaryVersionPolicy.Admit(state with { Node = state.Node with { CandidateVersionId = ids[1] } }, state.Node.Id, false, false).Kind);
        var next = Guid.NewGuid();
        var edited = state with { Node = state.Node with { CurrentVersionId = ids[1], Revision = 2 } };
        var complete = SummaryVersionPolicy.Complete(edited, admission.Value, next);
        Assert.Equal(ids[1], complete.Value!.CurrentVersionId);
        Assert.Equal(next, complete.Value.CandidateVersionId);
        Assert.Equal(SummaryOutcomeKind.Obsolete, SummaryVersionPolicy.Complete(null, admission.Value, next).Kind);
        var first = SummaryVersionPolicy.Admit(null, Guid.NewGuid(), false, false).Value!;
        Assert.Equal(new SummaryPointerChange(next, null), SummaryVersionPolicy.Complete(null, first, next).Value);
    }

    [Fact]
    public void Draft_commit_checks_epoch_baseline_and_exact_body_without_blocking_active_generation()
    {
        var state = State(Guid.NewGuid(), [Guid.NewGuid()], 0) with { DraftEpoch = 2, DraftRevision = 3, ActiveSummaryTaskIds = [Guid.NewGuid()] };
        var version = new SummaryVersion(state.Versions[0], "body\n", new(state.Node.ConversationId, 0, [], [], [], null));
        var token = new SummaryDraftToken(state.Node.Id, state.Node.CurrentVersionId, 2, 3);
        var unchanged = new SummaryEditDraft(token, "title", "body\n", false);
        Assert.False(SummaryVersionPolicy.CommitDraft(state, version, unchanged, state.Token).Value!.CreatesVersion);
        Assert.True(SummaryVersionPolicy.CommitDraft(state, version, unchanged with { Markdown = "body", IsDirty = true }, state.Token).Value!.CreatesVersion);
        Assert.Equal(SummaryOutcomeKind.Obsolete, SummaryVersionPolicy.CommitDraft(state, version, unchanged with { Token = token with { Epoch = 1 } }, state.Token).Kind);
        Assert.Equal(SummaryOutcomeKind.Conflict, SummaryVersionPolicy.BeginEdit(state, Guid.NewGuid(), state.Token).Kind);
    }

    [Fact]
    public void Read_normalization_offsets_and_old_new_revision_observations_are_guarded()
    {
        Assert.Equal("é 😀 b", SummaryReadPolicy.NormalizeReadableText("e\u0301\r\n 😀\t b"));
        var state = SummaryReadPolicy.Open(Guid.NewGuid(), "é 😀 b", null);
        Assert.Equal(5, state.TextLength);
        var invalid = new SummaryReadObservation(state.Epoch, state.Revision, 1, TimeSpan.Zero, true, true, false, state.TextHash, [new(0, 6)]);
        Assert.Equal(SummaryOutcomeKind.Rejected, SummaryReadPolicy.Observe(state, invalid).Kind);
        Assert.Equal(SummaryOutcomeKind.Obsolete, SummaryReadPolicy.Observe(state, invalid with { ExpectedRevision = state.Revision + 1 }).Kind);
        Assert.Equal(SummaryOutcomeKind.Obsolete, SummaryReadPolicy.Observe(state, invalid with { Epoch = state.Epoch + 1 }).Kind);
        Assert.False(SummaryReadPolicy.Observe(state, invalid with { Intervals = [new(0, 5)], IsVisible = false }).Value!.IsRead);
    }

    [Fact]
    public void Short_body_dwell_requires_actual_readable_ranges_currently_visible()
    {
        var state = SummaryReadPolicy.Open(Guid.NewGuid(), "a", null);
        state = Observe(state, 1, 0, true, [new(0, 1)]);
        state = Observe(state, 2, 10, true, []);
        Assert.False(state.IsRead);
        state = Observe(state, 3, 11, true, [new(0, 1)]);
        Assert.False(state.IsRead);
        state = Observe(state, 4, 14, true, [new(0, 1)]);
        Assert.True(state.IsRead);
    }

    [Fact]
    public void Unrelated_history_deletion_preserves_pending_candidate_dirty_draft_and_active_task()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var state = State(Guid.NewGuid(), ids, 2) with { HasDirtyDraft = true, ActiveSummaryTaskIds = [Guid.NewGuid()], DraftEpoch = 4, DraftRevision = 8 };
        state = state with { Node = state.Node with { CandidateVersionId = ids[3] } };
        var preview = SummaryVersionPolicy.PreviewDelete(state, [ids[0], ids[1]]);
        Assert.Equal(SummaryOutcomeKind.Applied, preview.Kind);
        Assert.Equal(ids[2], preview.Value!.FallbackVersionId);
        Assert.False(preview.Value.DeletesNode);
        Assert.Equal(state.Token, preview.Value.Token);
        Assert.Equal(SummaryOutcomeKind.Blocked, SummaryVersionPolicy.PreviewDelete(state, [ids[2]]).Kind);
        Assert.Equal(SummaryOutcomeKind.Blocked, SummaryVersionPolicy.PreviewDelete(state, [ids[3]]).Kind);
    }

    [Fact]
    public void Later_content_uses_only_terminal_own_rows_above_frozen_highwater()
    {
        var u = SummaryInputTests.User();
        var plan = KnowledgeSummaryInputPlanner.Plan(SummaryInputTests.Material(u), SummaryInputTests.Config);
        var later = u with { Id = Guid.NewGuid(), Sequence = 2 };
        Assert.True(KnowledgeSummaryInputPlanner.HasLaterContent(plan.Coverage, [later]));
        Assert.False(KnowledgeSummaryInputPlanner.HasLaterContent(plan.Coverage, [later with { Status = Mindweave.Core.Chat.GenerationStatus.Running }]));
        Assert.False(KnowledgeSummaryInputPlanner.HasLaterContent(plan.Coverage, [later with { ConversationId = Guid.NewGuid() }]));
        Assert.False(KnowledgeSummaryInputPlanner.HasLaterContent(plan.Coverage, [u]));
    }

    [Fact]
    public void Reopening_dirty_editor_renews_epoch_without_discarding_draft()
    {
        var state = State(Guid.NewGuid(), [Guid.NewGuid()], 0) with { HasDirtyDraft = true, DraftEpoch = 3, DraftRevision = 9 };
        var renewed = SummaryVersionPolicy.BeginEdit(state, state.Node.CurrentVersionId, state.Token);
        Assert.Equal(SummaryOutcomeKind.Applied, renewed.Kind);
        Assert.Equal(4, renewed.Value!.Epoch);
        Assert.Equal(state.Node.CurrentVersionId, renewed.Value.BaseVersionId);
        // Content stays in SummaryEditDraft; the policy only changes the session token.
        Assert.True(state.HasDirtyDraft);
    }

    internal static SummaryVersionState State(Guid node, Guid[] versions, int current) => new(
        new(node, Guid.NewGuid(), Guid.NewGuid(), versions[current], null, 1, "name", "name", false, 1, DateTimeOffset.UnixEpoch),
        versions.Select((id, i) => new SummaryVersionHeader(new(id, node, i + 1, SummaryVersionOrigin.Generated, DateTimeOffset.UnixEpoch), "title", null, null, null)).ToImmutableArray(), false, 0, 0, []);
}
