using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Navigation;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryVersionStorageTests
{
    [Fact]
    public async Task Queries_return_full_versions_and_own_later_terminal_flag_without_marking_read()
    {
        await using var f = await SummaryStorageFixture.Open(); var v = await f.Versions.LoadSummaryVersionAsync(f.First);
        Assert.Equal("First", v!.Header.Title); Assert.Equal("# body\nFirst", v.Markdown);
        Assert.False((await f.Snapshot()).CurrentIsRead); Assert.False((await f.Snapshot()).HasLaterContent);
        Assert.Equal(f.Node, (await f.Versions.FindConversationSummaryAsync(f.Conversation))!.State.Node.Id);
        Assert.Single(await f.Versions.ListSummaryNodesAsync(f.Workspace)); Assert.Empty(await f.Versions.ListSummaryNodesAsync(Guid.NewGuid()));
        await BranchStorageTests.Answer(f.Store, f.Conversation, "later", "later answer");
        Assert.True((await f.Snapshot()).HasLaterContent); Assert.False((await f.Snapshot()).CurrentIsRead);
    }
    [Fact]
    public async Task Renew_preserves_dirty_and_empty_drafts_and_old_autosaves_cannot_revive_cleared_text()
    {
        await using var f = await SummaryStorageFixture.Open(); var draft = await f.Edit("", "");
        var before = f.DatabaseSnapshot();
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Versions.CommitSummaryDraftAsync((await f.Snapshot()).State.Token, draft.Token)).Kind);
        Assert.Equal(before, f.DatabaseSnapshot()); Assert.Equal("", (await f.Versions.LoadSummaryDraftAsync(f.Node))!.Markdown);
        var renewed = (await f.Versions.BeginSummaryEditAsync((await f.Snapshot()).State.Token)).Value!;
        Assert.Equal("", renewed.Title); Assert.True(renewed.IsDirty); Assert.True(renewed.Token.Epoch > draft.Token.Epoch);
        Assert.Equal(SummaryOutcomeKind.Obsolete, (await f.Versions.SaveSummaryDraftAsync(draft.Token, new("late", "late"))).Kind);
        var cleared = await f.Versions.DiscardSummaryDraftAsync(renewed.Token); Assert.Equal(SummaryOutcomeKind.Applied, cleared.Kind);
        Assert.Null(cleared.Value!.Title); Assert.False(cleared.Value.IsDirty);
        Assert.Equal(SummaryOutcomeKind.Obsolete, (await f.Versions.SaveSummaryDraftAsync(renewed.Token, new("late", "late"))).Kind);
    }
    [Fact]
    public async Task No_change_clears_draft_without_new_version_and_manual_commit_copies_exact_coverage_and_sources()
    {
        await using var f = await SummaryStorageFixture.Open(); var first = (await f.Versions.LoadSummaryVersionAsync(f.First))!;
        var draft = await f.Edit(" First ", first.Markdown);
        var result = await f.Versions.CommitSummaryDraftAsync((await f.Snapshot()).State.Token, draft.Token);
        Assert.Equal(SummaryOutcomeKind.NoChange, result.Kind); Assert.Single(await f.Versions.ListSummaryVersionsAsync(f.Node));
        Assert.Null((await f.Versions.LoadSummaryDraftAsync(f.Node))!.Markdown);
        Assert.Equal(SummaryOutcomeKind.Obsolete, (await f.Versions.SaveSummaryDraftAsync(draft.Token, new("late", "late"))).Kind);
        var manual = await f.Commit("Manual", "# preserved\n\nexact  ");
        Assert.Equal(SummaryVersionOrigin.Manual, manual.Header.Identity.Origin); Assert.Equal(2, manual.Header.Identity.Ordinal);
        Assert.Equal(f.First, manual.Header.BaselineVersionId); Assert.Equal(JsonSerializer.Serialize(first.Coverage), JsonSerializer.Serialize(manual.Coverage));
        Assert.Equal(f.Sql("SELECT coverage_json FROM summary_version_source WHERE version_id=$id", ("$id", f.First)), f.Sql("SELECT coverage_json FROM summary_version_source WHERE version_id=$id", ("$id", manual.Header.Identity.Id)));
        Assert.Equal(2L, f.Sql("SELECT count(*) FROM summary_version_message WHERE version_id=$id", ("$id", manual.Header.Identity.Id)));
        Assert.True((await f.Snapshot()).CurrentIsRead);
    }
    [Fact]
    public async Task Concurrent_generation_preserves_accepted_baseline_and_confirmation_is_bound_to_new_current()
    {
        await using var f = await SummaryStorageFixture.Open();
        var job = (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), f.Conversation, BranchStorageTests.Config(32768))).Value!;
        var manual = await f.Commit("Manual");
        await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, job.Revision);
        var candidate = (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, job.Revision, new("Candidate", "candidate"), null))).Value!;
        var token = (await f.Snapshot()).State.Token;
        var conflict = await f.Versions.AdoptSummaryCandidateAsync(token);
        Assert.Equal("CandidateBaselineChanged", conflict.Code); Assert.Equal(manual.Header.Identity.Id, conflict.AdoptionConflict!.Current.Identity.Id);
        Assert.Equal(f.First, conflict.AdoptionConflict.Candidate.BaselineVersionId);
        var confirmation = new SummaryReplacementConfirmation(token, candidate.VersionId, manual.Header.Identity.Id);
        var another = await f.Commit("Another");
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Versions.AdoptSummaryCandidateAsync(token, confirmation)).Kind);
        var latest = (await f.Snapshot()).State.Token;
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Versions.AdoptSummaryCandidateAsync(latest, confirmation)).Kind);
        var adopted = await f.Versions.AdoptSummaryCandidateAsync(latest, new(latest, candidate.VersionId, another.Header.Identity.Id));
        Assert.Equal(SummaryOutcomeKind.Applied, adopted.Kind); Assert.Equal(candidate.VersionId, adopted.Value!.State.Node.CurrentVersionId);
        Assert.Null(adopted.Value.State.Node.CandidateVersionId); Assert.False(adopted.Value.CurrentIsRead);
    }
    [Fact]
    public async Task History_current_preserves_versions_and_read_and_manual_node_name()
    {
        await using var f = await SummaryStorageFixture.Open(); var manual = await f.Commit("Second");
        var node = (await f.Snapshot()).State.Node;
        var renamed = await f.Versions.RenameSummaryNodeAsync(f.Node, node.NameRevision, "My display");
        Assert.Equal(SummaryOutcomeKind.Applied, renamed.Kind);
        var changed = await f.Versions.SetCurrentSummaryVersionAsync((await f.Snapshot()).State.Token, f.First);
        Assert.Equal(SummaryOutcomeKind.Applied, changed.Kind); Assert.False(changed.Value!.CurrentIsRead);
        Assert.Equal("First", changed.Value.State.Node.BaseTitle); Assert.Equal("My display", changed.Value.State.Node.DisplayName);
        Assert.Equal(2, (await f.Versions.ListSummaryVersionsAsync(f.Node)).Length);
        Assert.True((await f.Versions.LoadSummaryReadingAsync(manual.Header.Identity.Id))!.State.IsRead);
        Assert.Equal("Second", (await f.Versions.LoadSummaryVersionAsync(manual.Header.Identity.Id))!.Header.Title);
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Versions.RenameSummaryNodeAsync(f.Node, node.NameRevision, "late")).Kind);
    }
    [Fact]
    public async Task Historical_delete_ignores_unaffected_dirty_and_active_then_candidate_state_and_keeps_baseline_identity()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await f.Commit("Second");
        var job = (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), f.Conversation, BranchStorageTests.Config(32768))).Value!;
        var draft = await f.Edit("unfinished", "draft");
        var preview = await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [f.First]);
        Assert.Equal(SummaryOutcomeKind.Applied, preview.Kind); Assert.False(preview.Value!.Impact.AffectsDirtyDraft); Assert.Empty(preview.Value.Impact.AffectedActiveTaskIds);
        var reconstructed = JsonSerializer.Deserialize<SummaryDeletionPreview>(JsonSerializer.Serialize(preview.Value))!;
        Assert.Equal(SummaryOutcomeKind.Applied, (await f.Versions.DeleteSummaryVersionsAsync(reconstructed)).Kind);
        Assert.Null(await f.Versions.LoadSummaryVersionAsync(f.First)); Assert.Null(await f.Versions.LoadSummaryReadingAsync(f.First));
        Assert.Equal(draft, await f.Versions.LoadSummaryDraftAsync(f.Node)); Assert.Single(await f.Store.ListActiveKnowledgeSummaryJobsAsync());
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version_identity WHERE id=$id", ("$id", f.First)));
        await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, job.Revision);
        var candidate = (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, job.Revision, new("Candidate", "body"), null))).Value!;
        Assert.Equal(SummaryOutcomeKind.Blocked, (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [second.Header.Identity.Id])).Kind);
        Assert.Equal("DiscardCandidateSeparately", (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [candidate.VersionId])).Code);
    }
    [Fact]
    public async Task Multi_delete_uses_previous_then_latest_and_last_node_preserves_chat_and_invalidates_late_writes()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await f.Commit("Second"); var third = await f.Commit("Third"); var fourth = await f.Commit("Fourth");
        await f.Versions.SetCurrentSummaryVersionAsync((await f.Snapshot()).State.Token, third.Header.Identity.Id);
        var p = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [third.Header.Identity.Id, second.Header.Identity.Id])).Value!;
        Assert.Equal(f.First, p.FallbackVersionId); Assert.Equal(SummaryOutcomeKind.Applied, (await f.Versions.DeleteSummaryVersionsAsync(p)).Kind);
        p = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [f.First])).Value!;
        Assert.Equal(fourth.Header.Identity.Id, p.FallbackVersionId); await f.Versions.DeleteSummaryVersionsAsync(p);
        var lease = (await f.Versions.BeginSummaryEditAsync((await f.Snapshot()).State.Token)).Value!.Token;
        await f.Versions.DiscardSummaryDraftAsync(lease);
        var sel = await f.Versions.LoadSummarySelectionAsync();
        await f.Versions.SaveSummarySelectionAsync(new(f.Workspace, f.Node, fourth.Header.Identity.Id), sel.Revision);
        p = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [fourth.Header.Identity.Id])).Value!;
        Assert.True(p.DeletesNode); Assert.Null(p.FallbackVersionId); await f.Versions.DeleteSummaryVersionsAsync(p);
        Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node)); Assert.Empty(await f.Versions.ListSummaryVersionsAsync(f.Node));
        Assert.Null((await f.Versions.LoadSummarySelectionAsync()).Selection);
        Assert.Equal(MainArea.Network, (await f.Store.LoadSelectionAsync())!.Area);
        Assert.Equal(2, (await f.Store.ReadMessagesAsync(f.Conversation)).Count);
        Assert.Equal(SummaryOutcomeKind.NotFound, (await f.Versions.SaveSummaryDraftAsync(lease, new("late", "late"))).Kind);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version")); Assert.Equal(0L, f.Sql("SELECT count(*) FROM pragma_foreign_key_check"));
    }
    [Fact]
    public async Task Delete_rechecks_exact_preview_and_repairs_selected_version_to_current()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await f.Commit("Second");
        var selection = await f.Versions.LoadSummarySelectionAsync();
        await f.Versions.SaveSummarySelectionAsync(new(f.Workspace, f.Node, f.First), selection.Revision);
        var preview = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [f.First])).Value!;
        var forged = preview with { FallbackVersionId = f.First };
        var before = f.DatabaseSnapshot(); Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Versions.DeleteSummaryVersionsAsync(forged)).Kind); Assert.Equal(before, f.DatabaseSnapshot());
        Assert.Equal(SummaryOutcomeKind.Applied, (await f.Versions.DeleteSummaryVersionsAsync(preview)).Kind);
        Assert.Equal(second.Header.Identity.Id, (await f.Versions.LoadSummarySelectionAsync()).Selection!.VersionId);
    }
    [Fact]
    public async Task Selection_CAS_survives_clear_and_ordinary_navigation_and_rejects_cross_workspace()
    {
        await using var f = await SummaryStorageFixture.Open(); var state = await f.Versions.LoadSummarySelectionAsync();
        var selected = await f.Versions.SaveSummarySelectionAsync(new(f.Workspace, f.Node, f.First), state.Revision);
        Assert.Equal(SummaryOutcomeKind.Applied, selected.Kind); var legacy = (await f.Store.LoadSelectionAsync())!;
        Assert.Equal(ContentKind.Overview, legacy.Kind); Assert.Equal(MainArea.Network, legacy.Area); Assert.Null(legacy.TargetId);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Versions.SaveSummarySelectionAsync(new(Guid.NewGuid(), f.Node, f.First), selected.Value!.Revision)).Kind);
        await f.Store.SaveSelectionAsync(new(f.Workspace, MainArea.Explore, ContentKind.Conversation, f.Conversation));
        var cleared = await f.Versions.LoadSummarySelectionAsync(); Assert.Null(cleared.Selection); Assert.True(cleared.Revision > selected.Value.Revision);
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Versions.SaveSummarySelectionAsync(new(f.Workspace, f.Node, f.First), selected.Value.Revision)).Kind);
        var explicitClear = await f.Versions.SaveSummarySelectionAsync(null, cleared.Revision); Assert.True(explicitClear.Value!.Revision > cleared.Revision);
    }
    [Theory]
    [InlineData("commit")]
    [InlineData("delete")]
    [InlineData("selection")]
    [InlineData("adopt")]
    public async Task Late_SQL_failure_rolls_back_entire_database(string operation)
    {
        await using var f = await SummaryStorageFixture.Open(); SummaryEditDraft? draft = null; SummaryDeletionPreview? preview = null;
        if (operation == "commit") draft = await f.Edit("changed", "body");
        if (operation == "delete") { await f.Commit("Second"); preview = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [f.First])).Value!; }
        if (operation == "adopt") await f.Generate("Candidate");
        var target = operation switch { "commit" => "BEFORE UPDATE ON summary_edit_draft", "delete" => "AFTER DELETE ON summary_version", "selection" => "BEFORE INSERT ON app_selection", _ => "BEFORE UPDATE ON summary_node" };
        f.Sql($"CREATE TRIGGER task3_fault {target} BEGIN SELECT RAISE(ABORT,'injected'); END"); var before = f.DatabaseSnapshot();
        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            switch (operation)
            {
                case "commit": await f.Versions.CommitSummaryDraftAsync((await f.Snapshot()).State.Token, draft!.Token); break;
                case "delete": await f.Versions.DeleteSummaryVersionsAsync(preview!); break;
                case "selection": await f.Versions.SaveSummarySelectionAsync(new(f.Workspace, f.Node, f.First), (await f.Versions.LoadSummarySelectionAsync()).Revision); break;
                default: await f.Versions.AdoptSummaryCandidateAsync((await f.Snapshot()).State.Token); break;
            }
        });
        Assert.Equal(before, f.DatabaseSnapshot());
    }
}
