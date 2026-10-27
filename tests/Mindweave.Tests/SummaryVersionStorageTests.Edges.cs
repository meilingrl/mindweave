using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryVersionStorageTests
{
    [Fact]
    public async Task Discard_candidate_preserves_dirty_current_and_read_then_repairs_candidate_selection()
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Candidate");
        var draft = await f.Edit("dirty", "keep all dirty text"); var token = (await f.Snapshot()).State.Token;
        var read = (await f.Versions.BeginSummaryReadingAsync(candidate.VersionId, 0, "candidate")).Value!.State;
        var selection = await f.Versions.LoadSummarySelectionAsync(); await f.Versions.SaveSummarySelectionAsync(new(f.Workspace, f.Node, candidate.VersionId), selection.Revision);
        Assert.Equal(SummaryOutcomeKind.Blocked, (await f.Versions.AdoptSummaryCandidateAsync(token)).Kind);
        Assert.Equal(SummaryOutcomeKind.Blocked, (await f.Versions.SetCurrentSummaryVersionAsync(token, candidate.VersionId)).Kind);
        var discarded = await f.Versions.DiscardSummaryCandidateAsync(token); Assert.Equal(SummaryOutcomeKind.Applied, discarded.Kind);
        Assert.Null(discarded.Value!.State.Node.CandidateVersionId); Assert.Equal(draft, await f.Versions.LoadSummaryDraftAsync(f.Node));
        Assert.Null(await f.Versions.LoadSummaryVersionAsync(candidate.VersionId)); Assert.Null(await f.Versions.LoadSummaryReadingAsync(candidate.VersionId));
        Assert.Equal(f.First, (await f.Versions.LoadSummarySelectionAsync()).Selection!.VersionId);
        Assert.Equal(SummaryOutcomeKind.NotFound, (await f.Versions.SetSummaryReadAsync(candidate.VersionId, read.Epoch, read.Revision, true)).Kind);
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Versions.DiscardSummaryCandidateAsync(token)).Kind);
        Assert.Equal(SummaryOutcomeKind.NotFound, (await f.Versions.DiscardSummaryCandidateAsync((await f.Snapshot()).State.Token)).Kind);
    }
    [Fact]
    public async Task Candidate_does_not_block_unrelated_history_delete_and_accepted_baseline_remains_minimal()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await f.Commit("Second"); var candidate = await f.Generate("Candidate");
        var third = await f.Commit("Third"); var draft = await f.Edit("uncommitted", "stay");
        var beforeCandidate = (await f.Versions.LoadSummaryVersionAsync(candidate.VersionId))!;
        var preview = await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [second.Header.Identity.Id]);
        Assert.Equal(SummaryOutcomeKind.Applied, preview.Kind); Assert.False(preview.Value!.Impact.AffectsCandidate);
        Assert.Equal(SummaryOutcomeKind.Applied, (await f.Versions.DeleteSummaryVersionsAsync(preview.Value)).Kind);
        Assert.Equal(JsonSerializer.Serialize(beforeCandidate), JsonSerializer.Serialize(await f.Versions.LoadSummaryVersionAsync(candidate.VersionId)));
        Assert.Equal(draft, await f.Versions.LoadSummaryDraftAsync(f.Node)); Assert.Equal(third.Header.Identity.Id, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Null(await f.Versions.LoadSummaryVersionAsync(second.Header.Identity.Id));
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version_identity WHERE id=$id", ("$id", second.Header.Identity.Id)));
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version_source WHERE version_id=$id", ("$id", second.Header.Identity.Id)));
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM pragma_foreign_key_check"));
    }
    [Fact]
    public async Task Pruning_unreferenced_manual_identity_does_not_reuse_ordinal_and_clears_old_clean_draft()
    {
        await using var f = await SummaryStorageFixture.Open(); var manual = await f.Commit("Manual");
        var lease = (await f.Versions.BeginSummaryEditAsync((await f.Snapshot()).State.Token)).Value!.Token;
        await f.Versions.SetCurrentSummaryVersionAsync((await f.Snapshot()).State.Token, f.First);
        Assert.Null((await f.Versions.LoadSummaryDraftAsync(f.Node))!.Markdown);
        var preview = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [manual.Header.Identity.Id])).Value!;
        await f.Versions.DeleteSummaryVersionsAsync(preview);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version_identity WHERE id=$id", ("$id", manual.Header.Identity.Id)));
        Assert.Equal(SummaryOutcomeKind.Obsolete, (await f.Versions.SaveSummaryDraftAsync(lease, new("late", "late"))).Kind);
        var third = await f.Commit("Third"); Assert.Equal(3, third.Header.Identity.Ordinal);
    }
    [Fact]
    public async Task Manual_coverage_copies_frozen_branch_quote_and_effective_source_rows_exactly()
    {
        await using var f = await SummaryStorageFixture.Open(); var original = (await f.Store.ReadMessagesAsync(f.Conversation))[^1];
        var child = await f.Store.CreateBranchAsync(f.Conversation, BranchStorageTests.Selection(original), Guid.NewGuid());
        await BranchStorageTests.Answer(f.Store, child.Id, "child", "old");
        var retry = await f.Store.AcceptReplyAsync(child.Id, null, BranchStorageTests.Config(32768)); await f.Store.WriteGenerationAsync(retry.Task.Id, 1, "", GenerationStatus.Stopped);
        var job = (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), child.Id, BranchStorageTests.Config(32768))).Value!;
        await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 0);
        var generated = (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, 0, new("Child", "child body"), null))).Value!;
        var state = (await f.Versions.LoadSummaryNodeAsync(generated.NodeId))!.State;
        var d = (await f.Versions.BeginSummaryEditAsync(state.Token)).Value!;
        d = (await f.Versions.SaveSummaryDraftAsync(d.Token, new("Child edited", "new body"))).Value!;
        var changed = (await f.Versions.CommitSummaryDraftAsync((await f.Versions.LoadSummaryNodeAsync(generated.NodeId))!.State.Token, d.Token)).Value!;
        Assert.Equal(3, changed.Coverage.OwnHighWaterSequence); Assert.Equal(2, changed.Coverage.OwnMessages.Length); Assert.Equal(original.Id, changed.Coverage.DirectQuote!.Source.MessageId);
        var originalCoverage = f.Sql("SELECT coverage_json FROM summary_version_source WHERE version_id=$id", ("$id", generated.VersionId));
        Assert.Equal(originalCoverage, f.Sql("SELECT coverage_json FROM summary_version_source WHERE version_id=$id", ("$id", changed.Header.Identity.Id)));
        Assert.Equal(0L, f.Sql("""
            SELECT count(*) FROM (SELECT scope,ordinal,message_id,conversation_id FROM summary_version_message WHERE version_id=$a
                EXCEPT SELECT scope,ordinal,message_id,conversation_id FROM summary_version_message WHERE version_id=$b)
            """, ("$a", generated.VersionId), ("$b", changed.Header.Identity.Id)));
        await BranchStorageTests.Answer(f.Store, f.Conversation, "ancestor later", "ancestor only");
        Assert.False((await f.Versions.LoadSummaryNodeAsync(generated.NodeId))!.HasLaterContent);
        await BranchStorageTests.Answer(f.Store, child.Id, "own later", "own only"); Assert.True((await f.Versions.LoadSummaryNodeAsync(generated.NodeId))!.HasLaterContent);
    }
    [Fact]
    public async Task Delete_stops_affected_naming_metadata_and_retains_only_referenced_identity_without_waiting()
    {
        await using var f = await SummaryStorageFixture.Open(); var node = (await f.Snapshot()).State.Node;
        var naming = Guid.NewGuid(); var group = (string)f.Sql("SELECT group_key FROM summary_node WHERE id=$id", ("$id", f.Node))!;
        var epoch = (long)f.Sql("SELECT epoch FROM node_naming_group WHERE workspace_id=$workspace AND group_key=$key", ("$workspace", f.Workspace), ("$key", group))!;
        var member = new NodeNamingMember(f.Node, f.First, node.NameRevision, false, (string)f.Sql("SELECT content_hash FROM summary_version WHERE id=$id", ("$id", f.First))!);
        var snapshot = new NodeNamingSnapshot(f.Workspace, group, epoch, [member], "synthetic-snapshot");
        f.Sql("""
            INSERT INTO node_naming_job(id,operation_id,workspace_id,group_key,group_epoch,snapshot_json,configuration_json,status,revision,accepted_at)
            VALUES($id,$operation,$workspace,$key,$epoch,$snapshot,$config,'Running',0,$now)
            """, ("$id", naming), ("$operation", Guid.NewGuid()), ("$workspace", f.Workspace), ("$key", group), ("$epoch", epoch),
            ("$snapshot", JsonSerializer.Serialize(snapshot)), ("$config", JsonSerializer.Serialize(BranchStorageTests.Config(32768))), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        f.Sql("INSERT INTO node_naming_member VALUES($job,0,$node,$version,0,0,$hash)", ("$job", naming), ("$node", f.Node), ("$version", f.First), ("$hash", member.ContentHash));
        var preview = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [f.First])).Value!;
        Assert.Equal(SummaryOutcomeKind.Applied, (await f.Versions.DeleteSummaryVersionsAsync(preview)).Kind);
        Assert.Equal("Stopped", f.Sql("SELECT status FROM node_naming_job WHERE id=$id", ("$id", naming))); Assert.Equal(1L, f.Sql("SELECT revision FROM node_naming_job WHERE id=$id", ("$id", naming)));
        Assert.Equal("SummaryVersionDeleted", f.Sql("SELECT error_code FROM node_naming_job WHERE id=$id", ("$id", naming)));
        Assert.Null(await f.Versions.LoadSummaryVersionAsync(f.First)); Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node));
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version_identity WHERE id=$id", ("$id", f.First)));
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM pragma_foreign_key_check"));
    }
    [Fact]
    public async Task Same_expected_tokens_allow_only_one_competing_autosave_and_selection_write()
    {
        await using var f = await SummaryStorageFixture.Open(); var draft = (await f.Versions.BeginSummaryEditAsync((await f.Snapshot()).State.Token)).Value!;
        var results = await Task.WhenAll(f.Versions.SaveSummaryDraftAsync(draft.Token, new("one", "one")), f.Versions.SaveSummaryDraftAsync(draft.Token, new("two", "two")));
        Assert.Single(results, r => r.Kind == SummaryOutcomeKind.Applied); Assert.Single(results, r => r.Kind == SummaryOutcomeKind.Obsolete);
        var selection = await f.Versions.LoadSummarySelectionAsync();
        var selected = await Task.WhenAll(f.Versions.SaveSummarySelectionAsync(new(f.Workspace, f.Node, f.First), selection.Revision), f.Versions.SaveSummarySelectionAsync(null, selection.Revision));
        Assert.Single(selected, r => r.Kind == SummaryOutcomeKind.Applied); Assert.Single(selected, r => r.Kind == SummaryOutcomeKind.Conflict);
    }
    [Theory]
    [InlineData("draft")]
    [InlineData("read")]
    [InlineData("rename")]
    [InlineData("ordinary-navigation")]
    public async Task Additional_late_SQL_failures_preserve_entire_database(string operation)
    {
        await using var f = await SummaryStorageFixture.Open(); var draft = (await f.Versions.BeginSummaryEditAsync((await f.Snapshot()).State.Token)).Value!;
        var selected = await f.Versions.LoadSummarySelectionAsync(); await f.Versions.SaveSummarySelectionAsync(new(f.Workspace, f.Node, f.First), selected.Revision);
        var target = operation switch { "draft" => "AFTER UPDATE ON summary_edit_draft", "read" => "AFTER UPDATE ON summary_read_state", "rename" => "AFTER UPDATE ON summary_node", _ => "AFTER UPDATE ON summary_selection" };
        f.Sql($"CREATE TRIGGER task3_edge_fault {target} BEGIN SELECT RAISE(ABORT,'injected'); END"); var before = f.DatabaseSnapshot();
        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            switch (operation)
            {
                case "draft": await f.Versions.SaveSummaryDraftAsync(draft.Token, new("draft", "text")); break;
                case "read": await f.Versions.BeginSummaryReadingAsync(f.First, 0, "body"); break;
                case "rename": await f.Versions.RenameSummaryNodeAsync(f.Node, (await f.Snapshot()).State.Node.NameRevision, "rename"); break;
                default: await f.Store.SaveSelectionAsync(new(f.Workspace, Mindweave.Application.Navigation.MainArea.Explore)); break;
            }
        });
        Assert.Equal(before, f.DatabaseSnapshot());
    }
    [Theory]
    [InlineData("ordinal")]
    [InlineData("node")]
    [InlineData("epoch")]
    [InlineData("draft-revision")]
    [InlineData("read-revision")]
    [InlineData("name")]
    [InlineData("selection")]
    public async Task Exhausted_counters_throw_without_any_partial_write(string counter)
    {
        await using var f = await SummaryStorageFixture.Open(); var draft = await f.Edit("changed", "changed body");
        var sql = counter switch
        {
            "ordinal" => "UPDATE summary_node SET last_version_ordinal=9223372036854775807",
            "node" => "UPDATE summary_node SET revision=9223372036854775807",
            "epoch" => "UPDATE summary_edit_draft SET epoch=9223372036854775807",
            "draft-revision" => "UPDATE summary_edit_draft SET revision=9223372036854775807",
            "read-revision" => "UPDATE summary_read_state SET revision=9223372036854775807",
            "name" => "UPDATE summary_node SET name_revision=9223372036854775807",
            _ => "UPDATE summary_selection SET revision=9223372036854775807"
        };
        f.Sql(sql); var before = f.DatabaseSnapshot();
        await Assert.ThrowsAsync<OverflowException>(async () =>
        {
            var state = (await f.Snapshot()).State;
            switch (counter)
            {
                case "epoch": await f.Versions.BeginSummaryEditAsync(state.Token); break;
                case "draft-revision": await f.Versions.SaveSummaryDraftAsync((await f.Versions.LoadSummaryDraftAsync(f.Node))!.Token, new("next", "next")); break;
                case "read-revision": await f.Versions.BeginSummaryReadingAsync(f.First, long.MaxValue, "body"); break;
                case "name": await f.Versions.RenameSummaryNodeAsync(f.Node, long.MaxValue, "next"); break;
                case "selection": await f.Versions.SaveSummarySelectionAsync(null, long.MaxValue); break;
                default: await f.Versions.CommitSummaryDraftAsync(state.Token, draft.Token); break;
            }
        });
        Assert.Equal(before, f.DatabaseSnapshot());
    }
}
