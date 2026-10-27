using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;
namespace Mindweave.Tests;

public sealed class SummaryGenerationStorageTests
{
    [Fact]
    public async Task Complete_first_creates_unread_current_atomically_and_idempotently()
    {
        using var dir = new TestDirectory(); var p = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(p);
        var summaries = Assert.IsAssignableFrom<IKnowledgeSummaryStore>(store);
        var c = await store.CreateConversationAsync((await store.CreateAsync("synthetic")).Id);
        await BranchStorageTests.Answer(store, c.Id, "q", "answer");
        var accepted = await summaries.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), c.Id, BranchStorageTests.Config(32768));
        Assert.Equal(SummaryOutcomeKind.Applied, accepted.Kind); var job = accepted.Value!;
        Assert.Equal(0L, ContextStorageTests.Sql(p, "SELECT count(*) FROM summary_node"));
        Assert.Equal(SummaryOutcomeKind.Applied, (await summaries.PrepareKnowledgeSummaryRequestAsync(job.Id, job.Revision)).Kind);
        var completion = new SummaryGenerationCompletion(job.Id, job.Revision, new("Title", "# Body\ntext"), "actual-model");
        var result = await summaries.CompleteKnowledgeSummaryAsync(completion);
        Assert.Equal(SummaryOutcomeKind.Applied, result.Kind); Assert.False(result.Value!.IsCandidate);
        Assert.Equal(0L, ContextStorageTests.Sql(p, "SELECT is_read FROM summary_read_state WHERE version_id=$id", ("$id", result.Value.VersionId)));
        Assert.Equal(result.Value, (await summaries.CompleteKnowledgeSummaryAsync(completion)).Value);
        Assert.Equal(1L, ContextStorageTests.Sql(p, "SELECT count(*) FROM summary_version"));
        Assert.Equal(0L, ContextStorageTests.Sql(p, "SELECT count(*) FROM pragma_foreign_key_check"));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestDirectory dir = new();
        public AppPaths Paths { get; }
        public SqliteWorkspaceStore Store { get; private set; } = null!;
        public Guid Conversation { get; private set; }
        public AiConfiguration Config { get; } = BranchStorageTests.Config(32768);
        public Fixture() { Paths = AppPaths.ForTrack("codex-steady", dir.Path); }
        public static async Task<Fixture> Open(bool history = true)
        {
            var f = new Fixture(); f.Store = await SqliteWorkspaceStore.OpenAsync(f.Paths);
            f.Conversation = (await f.Store.CreateConversationAsync((await f.Store.CreateAsync("synthetic summary")).Id)).Id;
            if (history) await BranchStorageTests.Answer(f.Store, f.Conversation, "own question", "own answer"); return f;
        }
        public async Task<KnowledgeSummaryJob> Accept(Guid? conversation = null)
        {
            var outcome = await Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), conversation ?? Conversation, Config);
            Assert.Equal(SummaryOutcomeKind.Applied, outcome.Kind); return outcome.Value!;
        }
        public async Task<SummaryGenerationResult> Finish(KnowledgeSummaryJob job, string title = "Title")
        {
            Assert.Equal(SummaryOutcomeKind.Applied, (await Store.PrepareKnowledgeSummaryRequestAsync(job.Id, job.Revision)).Kind);
            var result = await Store.CompleteKnowledgeSummaryAsync(new(job.Id, job.Revision, new(title, "# complete body"), "actual"));
            Assert.Equal(SummaryOutcomeKind.Applied, result.Kind); return result.Value!;
        }
        public object? Sql(string sql, params (string, object?)[] args) => ContextStorageTests.Sql(Paths, sql, args);
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); dir.Dispose(); }
    }
    [Fact]
    public async Task Operation_replay_is_bound_to_exact_configuration_before_current_source_refresh()
    {
        await using var f = await Fixture.Open(); var job = await f.Accept();
        await f.Store.SaveConfigurationAsync(f.Config with { ModelId = "new-active" });
        var newer = await f.Store.AcceptReplyAsync(f.Conversation, "new queued question", f.Config);
        var replay = await f.Store.AcceptKnowledgeSummaryAsync(job.OperationId, f.Conversation, f.Config);
        Assert.Equal(SummaryOutcomeKind.NoChange, replay.Kind); Assert.Equal(JsonSerializer.Serialize(job), JsonSerializer.Serialize(replay.Value));
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Store.AcceptKnowledgeSummaryAsync(job.OperationId, f.Conversation, f.Config with { MaxOutputTokens = f.Config.MaxOutputTokens + 1 })).Kind);
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Store.AcceptKnowledgeSummaryAsync(job.OperationId, Guid.NewGuid(), f.Config)).Kind);
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM knowledge_summary_job"));
        var prepared = await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 0);
        Assert.Equal(SummaryOutcomeKind.Applied, prepared.Kind); Assert.Equal(f.Config, prepared.Value!.Request.Configuration);
        Assert.DoesNotContain("new queued question", JsonSerializer.Serialize(prepared.Value.Request));
        Assert.NotNull(await f.Store.FindKnowledgeSummaryOperationAsync(job.OperationId));
        Assert.Equal(job.Id, Assert.Single(await f.Store.ListActiveKnowledgeSummaryJobsAsync()).Id);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Active_reply_guard_checks_both_rows_and_jobs_independently(bool rowOnly)
    {
        await using var f = await Fixture.Open(); var work = await f.Store.AcceptReplyAsync(f.Conversation, "active", f.Config);
        f.Sql(rowOnly ? "UPDATE generation_task SET status='Stopped',revision=1 WHERE id=$id" : "UPDATE chat_message SET status='Stopped',revision=1 WHERE id=$id",
            ("$id", rowOnly ? work.Task.Id : work.Task.AnswerMessageId));
        var before = SummaryMigrationTests.Snapshot(f.Paths.DatabasePath);
        Assert.Equal(SummaryOutcomeKind.Blocked, (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), f.Conversation, f.Config)).Kind);
        Assert.Equal(before, SummaryMigrationTests.Snapshot(f.Paths.DatabasePath));
    }
    [Fact]
    public async Task Active_summary_and_candidate_block_new_operations_but_not_exact_replay()
    {
        await using var f = await Fixture.Open(); var first = await f.Accept();
        Assert.Equal(SummaryOutcomeKind.Blocked, (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), f.Conversation, f.Config)).Kind);
        await f.Finish(first); var second = await f.Accept(); var candidate = await f.Finish(second);
        Assert.True(candidate.IsCandidate);
        Assert.Equal(SummaryOutcomeKind.Blocked, (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), f.Conversation, f.Config)).Kind);
        Assert.Equal(SummaryOutcomeKind.NoChange, (await f.Store.AcceptKnowledgeSummaryAsync(second.OperationId, f.Conversation, f.Config)).Kind);
        Assert.Equal(2L, f.Sql("SELECT count(*) FROM knowledge_summary_job"));
    }
    [Fact]
    public async Task Empty_over_budget_and_invalid_configuration_reject_before_any_accepted_rows()
    {
        await using var f = await Fixture.Open(false); await f.Store.SaveDraftAsync(f.Conversation, "keep draft", 5);
        var before = SummaryMigrationTests.Snapshot(f.Paths.DatabasePath);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), f.Conversation, f.Config)).Kind);
        Assert.Equal(before, SummaryMigrationTests.Snapshot(f.Paths.DatabasePath));
        await BranchStorageTests.Answer(f.Store, f.Conversation, "own", "answer"); before = SummaryMigrationTests.Snapshot(f.Paths.DatabasePath);
        foreach (var config in new[] { f.Config with { ContextTokenBudget = 2, MaxOutputTokens = 1 }, f.Config with { BaseUrl = "http://remote.invalid" }, f.Config with { CredentialReference = "" }, f.Config with { SummaryThinking = (SummaryThinkingMode)99 } })
            Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), f.Conversation, config)).Kind);
        Assert.Equal(before, SummaryMigrationTests.Snapshot(f.Paths.DatabasePath));
        Assert.Equal("keep draft", (await f.Store.LoadDraftAsync(f.Conversation)).Text);
    }
    [Fact]
    public async Task Latest_own_attempt_and_highwater_are_frozen_while_ancestor_keeps_original_attempt()
    {
        await using var f = await Fixture.Open(); var original = (await f.Store.ReadMessagesAsync(f.Conversation))[^1];
        var child = await f.Store.CreateBranchAsync(f.Conversation, BranchStorageTests.Selection(original), Guid.NewGuid());
        var retry = await f.Store.AcceptReplyAsync(f.Conversation, null, f.Config); await f.Store.WriteGenerationAsync(retry.Task.Id, 1, "new ancestor attempt", GenerationStatus.Completed);
        await BranchStorageTests.Answer(f.Store, child.Id, "child own", "child old");
        var ownRetry = await f.Store.AcceptReplyAsync(child.Id, null, f.Config); await f.Store.WriteGenerationAsync(ownRetry.Task.Id, 1, "partial", GenerationStatus.Stopped);
        var job = await f.Accept(child.Id); var coverage = job.InputPlan.Coverage;
        Assert.Equal(3, coverage.OwnHighWaterSequence); Assert.Equal(2, coverage.OwnMessages.Length);
        Assert.Equal(ownRetry.Task.AnswerMessageId, coverage.OwnMessages[^1].MessageId); Assert.Equal(GenerationStatus.Stopped, coverage.OwnMessages[^1].Status);
        Assert.Contains(coverage.FrozenAncestors, m => m.MessageId == original.Id); Assert.DoesNotContain(coverage.FrozenAncestors, m => m.MessageId == retry.Task.AnswerMessageId);
        Assert.Equal(original.Id, coverage.DirectQuote!.Source.MessageId);
        var prepared = (await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 0)).Value!;
        await BranchStorageTests.Answer(f.Store, child.Id, "later child", "later answer");
        Assert.Equal(JsonSerializer.Serialize(prepared), JsonSerializer.Serialize(await f.Store.LoadPreparedKnowledgeSummaryRequestAsync(job.Id)));
        Assert.Equal(ChatRequestPurpose.Standard, prepared.Request.Purpose);
        Assert.Equal(JsonSerializer.Serialize(coverage), f.Sql("SELECT json_extract(input_plan_json,'$.Coverage') FROM knowledge_summary_job WHERE id=$id", ("$id", job.Id)));
        Assert.DoesNotContain("child old", (string)f.Sql("SELECT input_plan_json FROM knowledge_summary_job WHERE id=$id", ("$id", job.Id))!);
    }
    [Fact]
    public async Task Branch_authority_is_revalidated_inside_acceptance_transaction()
    {
        await using var f = await Fixture.Open(); var original = (await f.Store.ReadMessagesAsync(f.Conversation))[^1];
        var child = await f.Store.CreateBranchAsync(f.Conversation, BranchStorageTests.Selection(original), Guid.NewGuid());
        await BranchStorageTests.Answer(f.Store, child.Id, "child own", "child answer");
        f.Sql("DROP TRIGGER branch_source_immutable; UPDATE branch_source SET source_body_hash=$hash WHERE child_conversation_id=$id", ("$hash", new string('0', 64)), ("$id", child.Id));
        var before = SummaryMigrationTests.Snapshot(f.Paths.DatabasePath);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), child.Id, f.Config)).Kind);
        Assert.Equal(before, SummaryMigrationTests.Snapshot(f.Paths.DatabasePath));
    }
    [Theory]
    [InlineData("format")]
    [InlineData("plan-config")]
    [InlineData("plan-source")]
    [InlineData("prepared-hash")]
    [InlineData("prepared-material")]
    [InlineData("source-body")]
    public async Task Prepared_read_rejects_tampering_without_repair_writes(string change)
    {
        await using var f = await Fixture.Open(); var job = await f.Accept(); await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 0);
        switch (change)
        {
            case "format": f.Sql("DROP TRIGGER knowledge_summary_identity_immutable;UPDATE knowledge_summary_job SET input_plan_json=json_set(input_plan_json,'$.FormatVersion',99),revision=revision+1"); break;
            case "plan-config": f.Sql("DROP TRIGGER knowledge_summary_identity_immutable;UPDATE knowledge_summary_job SET input_plan_json=json_set(input_plan_json,'$.Configuration.ModelId','changed'),revision=revision+1"); break;
            case "plan-source": f.Sql("DROP TRIGGER knowledge_summary_identity_immutable;UPDATE knowledge_summary_job SET input_plan_json=json_set(input_plan_json,'$.Coverage.OwnMessages[0].Revision',99),revision=revision+1"); break;
            case "prepared-hash": f.Sql("DROP TRIGGER knowledge_summary_prepared_immutable;UPDATE knowledge_summary_prepared SET request_hash=$hash", ("$hash", new string('0', 64))); break;
            case "prepared-material": f.Sql("DELETE FROM knowledge_summary_material WHERE ordinal=1"); break;
            case "source-body": f.Sql("DROP TRIGGER terminal_message_immutable;UPDATE chat_message SET body='tampered' WHERE conversation_id=$id AND role='Assistant'", ("$id", f.Conversation)); break;
        }
        var before = SummaryMigrationTests.Snapshot(f.Paths.DatabasePath);
        await Assert.ThrowsAsync<KnowledgeSummaryInputException>(() => f.Store.LoadPreparedKnowledgeSummaryRequestAsync(job.Id));
        Assert.Equal(before, SummaryMigrationTests.Snapshot(f.Paths.DatabasePath));
    }
    [Theory]
    [InlineData(GenerationStatus.Stopped)]
    [InlineData(GenerationStatus.Failed)]
    [InlineData(GenerationStatus.Interrupted)]
    public async Task Terminal_metadata_creates_no_output_and_late_completion_cannot_revive(GenerationStatus status)
    {
        await using var f = await Fixture.Open(); var job = await f.Accept(); await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 0);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Store.UpdateKnowledgeSummaryMetadataAsync(new(job.Id, 0, GenerationStatus.Completed))).Kind);
        var terminal = await f.Store.UpdateKnowledgeSummaryMetadataAsync(new(job.Id, 0, status, "synthetic_failure", "model")); Assert.Equal(SummaryOutcomeKind.Applied, terminal.Kind);
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, 1, new("title", "body"), "model"))).Kind);
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Store.UpdateKnowledgeSummaryMetadataAsync(new(job.Id, 1, GenerationStatus.Running))).Kind);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version")); Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_node"));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE knowledge_summary_job SET error_code='changed',revision=revision+1"));
    }
    [Fact]
    public async Task Metadata_is_bounded_and_cannot_be_used_as_partial_body_storage()
    {
        await using var f = await Fixture.Open(); var job = await f.Accept();
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Store.UpdateKnowledgeSummaryMetadataAsync(new(job.Id, 0, GenerationStatus.Running, new string('x', 1000)))).Kind);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, 0, new("title", "body"), new string('x', 1000)))).Kind);
        Assert.Equal(0, (await f.Store.LoadKnowledgeSummaryJobAsync(job.Id))!.Revision);
    }
    [Fact]
    public async Task Recovery_interrupts_only_active_summary_metadata_and_never_creates_versions()
    {
        await using var f = await Fixture.Open(); var first = await f.Accept(); await f.Store.UpdateKnowledgeSummaryMetadataAsync(new(first.Id, 0, GenerationStatus.Failed, "failed"));
        var second = await f.Accept(); await f.Store.UpdateKnowledgeSummaryMetadataAsync(new(second.Id, 0, GenerationStatus.Running));
        await f.Store.RecoverInterruptedKnowledgeSummariesAsync(); await f.Store.RecoverInterruptedKnowledgeSummariesAsync();
        Assert.Empty(await f.Store.ListActiveKnowledgeSummaryJobsAsync()); var recovered = (await f.Store.LoadKnowledgeSummaryJobAsync(second.Id))!;
        Assert.Equal(GenerationStatus.Interrupted, recovered.Status); Assert.Equal(2, recovered.Revision);
        Assert.Equal(GenerationStatus.Failed, (await f.Store.LoadKnowledgeSummaryJobAsync(first.Id))!.Status);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version"));
    }
    [Fact]
    public async Task Candidate_retains_frozen_baseline_despite_concurrent_manual_current_edit()
    {
        await using var f = await Fixture.Open(); var first = await f.Finish(await f.Accept()); var next = await f.Accept();
        var manual = Guid.NewGuid();
        f.Sql("""
            BEGIN;
            INSERT INTO summary_version_identity SELECT $manual,node_id,2,'Manual',created_at FROM summary_version_identity WHERE id=$first;
            INSERT INTO summary_version SELECT $manual,node_id,'Manual title','manual body',id,NULL,NULL,$hash FROM summary_version WHERE id=$first;
            INSERT INTO summary_version_source SELECT $manual,coverage_json FROM summary_version_source WHERE version_id=$first;
            UPDATE summary_node SET current_version_id=$manual,revision=revision+1,base_title='Manual title',group_key='MANUAL TITLE' WHERE id=$node;
            COMMIT;
            """, ("$manual", manual), ("$first", first.VersionId), ("$node", first.NodeId), ("$hash", KnowledgeSummaryInputPlanner.Hash("synthetic manual")));
        var result = await f.Finish(next, "candidate title"); Assert.True(result.IsCandidate);
        Assert.Equal(manual.ToString("D"), f.Sql("SELECT current_version_id FROM summary_node"));
        Assert.Equal(first.VersionId.ToString("D"), f.Sql("SELECT baseline_version_id FROM summary_version WHERE id=$id", ("$id", result.VersionId)));
        Assert.Equal(3L, f.Sql("SELECT ordinal FROM summary_version_identity WHERE id=$id", ("$id", result.VersionId)));
        Assert.Equal(0L, f.Sql("SELECT is_read FROM summary_read_state WHERE version_id=$id", ("$id", result.VersionId)));
        Assert.Equal("Manual title", f.Sql("SELECT base_title FROM summary_node"));
    }
    [Fact]
    public async Task Completion_fault_rolls_back_all_rows_then_retries_without_new_generation()
    {
        await using var f = await Fixture.Open(); var job = await f.Accept(); await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 0);
        f.Sql("CREATE TRIGGER synthetic_fail BEFORE UPDATE ON knowledge_summary_job WHEN NEW.status='Completed' BEGIN SELECT RAISE(ABORT,'synthetic fault');END;");
        var before = SummaryMigrationTests.Snapshot(f.Paths.DatabasePath); var completion = new SummaryGenerationCompletion(job.Id, 0, new("title", "body"), "model");
        await Assert.ThrowsAsync<SqliteException>(() => f.Store.CompleteKnowledgeSummaryAsync(completion));
        Assert.Equal(before, SummaryMigrationTests.Snapshot(f.Paths.DatabasePath));
        f.Sql("DROP TRIGGER synthetic_fail"); var result = await f.Store.CompleteKnowledgeSummaryAsync(completion); Assert.Equal(SummaryOutcomeKind.Applied, result.Kind);
        Assert.Equal(SummaryOutcomeKind.NoChange, (await f.Store.CompleteKnowledgeSummaryAsync(completion)).Kind);
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Store.CompleteKnowledgeSummaryAsync(completion with { Content = new("different", "body") })).Kind);
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version"));
    }
    [Fact]
    public async Task Deletion_of_completed_output_does_not_erase_reconciliation_identity_or_resurrect_content()
    {
        await using var f = await Fixture.Open(); var job = await f.Accept(); var result = await f.Finish(job);
        f.Sql("BEGIN; DELETE FROM summary_version WHERE node_id=$id; DELETE FROM summary_node WHERE id=$id; COMMIT", ("$id", result.NodeId));
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version")); Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_read_state")); Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version_source"));
        var replay = await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, 0, new("Title", "# complete body"), "actual"));
        Assert.Equal(SummaryOutcomeKind.NoChange, replay.Kind); Assert.Equal(result, replay.Value);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_node"));
    }
    [Fact]
    public async Task Deleted_existing_target_rejects_late_completion_without_recreating_node()
    {
        await using var f = await Fixture.Open(); var result = await f.Finish(await f.Accept()); var job = await f.Accept(); await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 0);
        // Future Task 3 blocks this deletion while active; direct SQL models a stale external target.
        f.Sql("BEGIN;DELETE FROM summary_version WHERE node_id=$id;DELETE FROM summary_node WHERE id=$id;COMMIT", ("$id", result.NodeId));
        Assert.Equal(SummaryOutcomeKind.Obsolete, (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, 0, new("late", "body"), "actual"))).Kind);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_node"));
    }
    [Fact]
    public async Task Foreign_pointers_empty_nodes_and_mutating_full_versions_fail_at_SQL_boundary()
    {
        await using var f = await Fixture.Open(); var left = await f.Finish(await f.Accept());
        var other = await f.Store.CreateConversationAsync((await f.Store.LoadConversationAsync(f.Conversation))!.WorkspaceId); await BranchStorageTests.Answer(f.Store, other.Id, "q", "a");
        var right = await f.Finish(await f.Accept(other.Id));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE summary_node SET current_version_id=$version WHERE id=$node", ("$version", right.VersionId), ("$node", left.NodeId)));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE summary_node SET candidate_version_id=$version WHERE id=$node", ("$version", right.VersionId), ("$node", left.NodeId)));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE summary_node SET current_version_id=NULL WHERE id=$node", ("$node", left.NodeId)));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE summary_version SET markdown='mutated' WHERE id=$id", ("$id", left.VersionId)));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE summary_version_identity SET ordinal=5 WHERE id=$id", ("$id", left.VersionId)));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE summary_node SET revision=9223372036854775807+1 WHERE id=$id", ("$id", left.NodeId)));
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM pragma_foreign_key_check"));
    }
    [Fact]
    public async Task Removing_baseline_body_cascades_only_owned_read_and_source_and_keeps_candidate()
    {
        await using var f = await Fixture.Open(); var first = await f.Finish(await f.Accept()); var candidate = await f.Finish(await f.Accept());
        f.Sql("BEGIN;UPDATE summary_node SET current_version_id=$candidate,candidate_version_id=NULL;DELETE FROM summary_version WHERE id=$first;COMMIT", ("$candidate", candidate.VersionId), ("$first", first.VersionId));
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version")); Assert.Equal(2L, f.Sql("SELECT count(*) FROM summary_version_identity"));
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_read_state")); Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version_source"));
        Assert.Equal(first.VersionId.ToString("D"), f.Sql("SELECT baseline_version_id FROM summary_version"));
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task Referenced_original_messages_cannot_be_deleted_after_summary_acceptance()
    {
        await using var f = await Fixture.Open(); await f.Accept();
        f.Sql("DELETE FROM generation_task WHERE conversation_id=$id", ("$id", f.Conversation));
        Assert.Throws<SqliteException>(() => f.Sql("DELETE FROM chat_message WHERE conversation_id=$id AND role='Assistant'", ("$id", f.Conversation)));
    }
    [Fact]
    public async Task Deleting_live_node_is_explicit_and_retained_output_identity_outlives_it()
    {
        await using var f = await Fixture.Open(); var job = await f.Accept(); var output = await f.Finish(job);
        Assert.Throws<SqliteException>(() => f.Sql("DELETE FROM summary_node WHERE id=$id", ("$id", output.NodeId)));
        f.Sql("BEGIN;DELETE FROM summary_version WHERE node_id=$id;DELETE FROM summary_node WHERE id=$id;COMMIT", ("$id", output.NodeId));
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version_identity WHERE id=$id", ("$id", output.VersionId)));
        Assert.Throws<SqliteException>(() => f.Sql("DELETE FROM summary_version_identity WHERE id=$id", ("$id", output.VersionId)));
    }

    [Fact]
    public async Task Version_ordinal_never_reuses_a_pruned_manual_identity()
    {
        await using var f = await Fixture.Open(); var first = await f.Finish(await f.Accept()); var manual = Guid.NewGuid();
        f.Sql("""
            BEGIN;
            INSERT INTO summary_version_identity SELECT $manual,node_id,2,'Manual',created_at FROM summary_version_identity WHERE id=$first;
            INSERT INTO summary_version SELECT $manual,node_id,'manual','body',id,NULL,NULL,content_hash FROM summary_version WHERE id=$first;
            UPDATE summary_node SET current_version_id=$manual,revision=revision+1;
            UPDATE summary_node SET current_version_id=$first,revision=revision+1;
            DELETE FROM summary_version WHERE id=$manual;
            DELETE FROM summary_version_identity WHERE id=$manual;
            COMMIT;
            """, ("$manual", manual), ("$first", first.VersionId));
        var result = await f.Finish(await f.Accept());
        Assert.Equal(3L, f.Sql("SELECT ordinal FROM summary_version_identity WHERE id=$id", ("$id", result.VersionId)));
    }

    [Fact]
    public async Task Concurrent_accept_and_completion_have_exactly_one_durable_result()
    {
        await using var f = await Fixture.Open(); var op = Guid.NewGuid();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Store.AcceptKnowledgeSummaryAsync(op, f.Conversation, f.Config)));
        Assert.Single(outcomes, o => o.Kind == SummaryOutcomeKind.Applied); Assert.Equal(7, outcomes.Count(o => o.Kind == SummaryOutcomeKind.NoChange));
        var job = outcomes[0].Value!; await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 0);
        var completion = new SummaryGenerationCompletion(job.Id, 0, new("title", "complete"), "model");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Store.CompleteKnowledgeSummaryAsync(completion)));
        Assert.Single(results, o => o.Kind == SummaryOutcomeKind.Applied); Assert.Single(results.Select(o => o.Value).Distinct());
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version"));
    }
    [Theory]
    [InlineData("accept")]
    [InlineData("prepare")]
    public async Task Source_or_preparation_write_failure_rolls_back_the_whole_transaction(string phase)
    {
        await using var f = await Fixture.Open(); KnowledgeSummaryJob? job = phase == "prepare" ? await f.Accept() : null;
        var table = phase == "accept" ? "knowledge_summary_source" : "knowledge_summary_material";
        f.Sql($"CREATE TRIGGER synthetic_fault BEFORE INSERT ON {table} WHEN NEW.ordinal=1 BEGIN SELECT RAISE(ABORT,'injected');END;");
        var before = SummaryMigrationTests.Snapshot(f.Paths.DatabasePath);
        if (job is null) await Assert.ThrowsAsync<SqliteException>(() => f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), f.Conversation, f.Config));
        else await Assert.ThrowsAsync<SqliteException>(() => f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 0));
        Assert.Equal(before, SummaryMigrationTests.Snapshot(f.Paths.DatabasePath));
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM knowledge_summary_prepared"));
    }
    [Fact]
    public async Task Recovery_failure_is_atomic_across_all_active_summary_jobs()
    {
        await using var f = await Fixture.Open(); var left = await f.Accept();
        var other = await f.Store.CreateConversationAsync((await f.Store.LoadConversationAsync(f.Conversation))!.WorkspaceId); await BranchStorageTests.Answer(f.Store, other.Id, "q", "a");
        var right = await f.Accept(other.Id);
        f.Sql($"CREATE TRIGGER synthetic_recovery_fault BEFORE UPDATE ON knowledge_summary_job WHEN NEW.id='{right.Id:D}' BEGIN SELECT RAISE(ABORT,'injected');END;");
        var before = SummaryMigrationTests.Snapshot(f.Paths.DatabasePath);
        await Assert.ThrowsAsync<SqliteException>(() => f.Store.RecoverInterruptedKnowledgeSummariesAsync());
        Assert.Equal(before, SummaryMigrationTests.Snapshot(f.Paths.DatabasePath)); Assert.Equal(2, (await f.Store.ListActiveKnowledgeSummaryJobsAsync()).Length);
    }
    [Fact]
    public async Task Invalid_content_stale_revisions_and_unprepared_completion_have_typed_outcomes()
    {
        await using var f = await Fixture.Open(); var job = await f.Accept();
        Assert.Equal(SummaryOutcomeKind.Blocked, (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, 0, new("title", "body"), null))).Kind);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, 0, new(" ", "body"), null))).Kind);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, 0, new("title", " "), null))).Kind);
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 1)).Kind);
        var running = await f.Store.UpdateKnowledgeSummaryMetadataAsync(new(job.Id, 0, GenerationStatus.Running)); Assert.Equal(1, running.Value!.Revision);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Store.UpdateKnowledgeSummaryMetadataAsync(new(job.Id, 1, GenerationStatus.Queued))).Kind);
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, 0, new("title", "body"), null))).Kind);
        Assert.Equal(SummaryOutcomeKind.Applied, (await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 1)).Kind);
        Assert.Equal(SummaryOutcomeKind.NoChange, (await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 1)).Kind);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version"));
    }
    [Fact]
    public async Task Ordinal_overflow_cannot_create_a_version_or_change_the_task()
    {
        await using var f = await Fixture.Open(); var output = await f.Finish(await f.Accept()); var job = await f.Accept(); await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, 0);
        f.Sql("UPDATE summary_node SET last_version_ordinal=$ordinal WHERE id=$id", ("$ordinal", long.MaxValue), ("$id", output.NodeId));
        var before = SummaryMigrationTests.Snapshot(f.Paths.DatabasePath);
        await Assert.ThrowsAsync<OverflowException>(() => f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, 0, new("title", "body"), null)));
        Assert.Equal(before, SummaryMigrationTests.Snapshot(f.Paths.DatabasePath));
    }
}
