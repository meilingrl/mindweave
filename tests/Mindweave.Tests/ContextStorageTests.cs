using Microsoft.Data.Sqlite;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class ContextStorageTests
{
    private static AiConfiguration Config() => BranchStorageTests.Config(7000);
    private static async Task<Conversation> Conversation(SqliteWorkspaceStore store) => await store.CreateConversationAsync((await store.CreateAsync("context")).Id);
    private static async Task History(SqliteWorkspaceStore store, Guid conversation)
    {
        for (var i = 0; i < 5; i++) await BranchStorageTests.Answer(store, conversation, "question" + i, new string((char)('a' + i), 1500));
    }
    internal static object? Sql(AppPaths paths, string sql, params (string, object?)[] parameters)
    {
        using var c = new SqliteConnection($"Data Source={paths.DatabasePath};Foreign Keys=True;Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }
    [Fact]
    public async Task V4_upgrade_keeps_backup_without_context_tables()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using (var old = await SqliteWorkspaceStore.OpenAsync(paths, DatabaseSchema.Migrations.Take(4).ToArray())) await old.CreateAsync("old");
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        Assert.Equal(5L, Sql(paths, "PRAGMA user_version"));
        Assert.Equal(1L, Sql(paths, "SELECT count(*) FROM sqlite_master WHERE name='reply_context_plan'"));
        using var c = new SqliteConnection($"Data Source={Assert.Single(Directory.GetFiles(paths.BackupDirectory, "*.db"))};Mode=ReadOnly;Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version"; Assert.Equal(4L, cmd.ExecuteScalar());
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='reply_context_plan'"; Assert.Equal(0L, cmd.ExecuteScalar());
    }
    [Fact]
    public async Task Over_budget_history_is_accepted_with_original_ids_and_plan_atomically()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store);
        await History(store, conversation.Id);
        await store.SaveDraftAsync(conversation.Id, "current", 1);
        var work = await store.AcceptReplyAsync(conversation.Id, "current", Config());
        Assert.Equal(11, work.Task.InputMessageIds.Count);
        Assert.Equal("", (await store.LoadDraftAsync(conversation.Id)).Text);
        Assert.Equal(1L, Sql(paths, "SELECT count(*) FROM reply_context_plan WHERE task_id=$id", ("$id", work.Task.Id)));
    }

    private const string Summary = """{"Background":"old facts","Requirements":"","Corrections":"","OpenQuestions":"","References":""}""";
    private static ContextChunk[] Chunks(GenerationWork work) => work.ContextPlan!.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk).ToArray();
    [Fact]
    public async Task Attempts_are_idempotent_monotonic_and_only_stop_completed_payloads_become_cache()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store); await History(store, conversation.Id);
        var work = await store.AcceptReplyAsync(conversation.Id, "current", Config()); var chunk = Chunks(work)[0];
        var attempt = await store.StartCompressionAsync(work.Task.Id, chunk.CacheKey);
        Assert.Equal(attempt.Id, (await store.StartCompressionAsync(work.Task.Id, chunk.CacheKey)).Id);
        Assert.True(await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, "partial", GenerationStatus.Running));
        Assert.False(await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, "old", GenerationStatus.Running));
        Assert.Null(await store.LoadCompletedSummaryAsync(work.Task.Id, chunk.CacheKey));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteCompressionAsync(work.Task.Id, attempt.Id, 2, Summary, GenerationStatus.Completed, "length"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteCompressionAsync(work.Task.Id, attempt.Id, 2, "{}", GenerationStatus.Completed, "stop"));
        Assert.True(await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 2, Summary, GenerationStatus.Completed, "stop", actualModelId: "actual"));
        var cached = (await store.LoadCompletedSummaryAsync(work.Task.Id, chunk.CacheKey))!;
        Assert.Equal(attempt.Id, cached.AttemptId); Assert.Equal("actual", cached.ActualModelId); Assert.Equal("old facts", cached.Content.Background);
        Assert.False(await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 3, "late", GenerationStatus.Running));
        Assert.Equal("old facts", (await store.LoadCompletedSummaryAsync(work.Task.Id, chunk.CacheKey))!.Content.Background);
    }
    [Theory]
    [InlineData("ABORT")][InlineData("ROLLBACK")]
    public async Task Late_summary_and_manifest_failures_roll_back_all_material(string action)
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store); await History(store, conversation.Id);
        var work = await store.AcceptReplyAsync(conversation.Id, "current", Config()); var chunks = Chunks(work);
        var attempt = await store.StartCompressionAsync(work.Task.Id, chunks[0].CacheKey);
        Sql(paths, $"CREATE TRIGGER fail_cache BEFORE INSERT ON context_summary_cache BEGIN SELECT RAISE({action},'fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, Summary, GenerationStatus.Completed, "stop"));
        Assert.Equal(GenerationStatus.Queued, (await store.LoadCompressionAttemptAsync(attempt.Id))!.Status);
        Assert.Null(await store.LoadCompletedSummaryAsync(work.Task.Id, chunks[0].CacheKey)); Sql(paths, "DROP TRIGGER fail_cache");
        foreach (var chunk in chunks)
        {
            var a = await store.StartCompressionAsync(work.Task.Id, chunk.CacheKey);
            await store.WriteCompressionAsync(work.Task.Id, a.Id, 1, Summary, GenerationStatus.Completed, "stop");
        }
        Sql(paths, $"CREATE TRIGGER fail_use BEFORE INSERT ON prepared_context_use WHEN NEW.input_index=1 BEGIN SELECT RAISE({action},'fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => store.PrepareReplyRequestAsync(work.Task.Id));
        Assert.Null(await store.LoadPreparedReplyRequestAsync(work.Task.Id));
        Assert.Equal(0L, Sql(paths, "SELECT count(*) FROM prepared_context_use")); Sql(paths, "DROP TRIGGER fail_use");
        var prepared = await store.PrepareReplyRequestAsync(work.Task.Id);
        Assert.Equal(prepared.PreparedAt, (await store.PrepareReplyRequestAsync(work.Task.Id)).PreparedAt);
        Assert.Equal("current", prepared.Request.Messages[^1].Content);
        Assert.Equal(chunks.Length, prepared.Uses.Count(u => u.SummaryId != null));
        Assert.True(prepared.Uses.Count < work.Task.InputMessageIds.Count);
    }
    [Fact]
    public async Task Recovery_interrupts_attempt_and_parent_but_preserves_completed_cache()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store); await History(store, conversation.Id);
        var config = Config() with { ContextTokenBudget = 4500 }; var work = await store.AcceptReplyAsync(conversation.Id, "current", config); var chunks = Chunks(work); Assert.True(chunks.Length >= 2);
        var done = await store.StartCompressionAsync(work.Task.Id, chunks[0].CacheKey); await store.WriteCompressionAsync(work.Task.Id, done.Id, 1, Summary, GenerationStatus.Completed, "stop");
        var pending = await store.StartCompressionAsync(work.Task.Id, chunks[1].CacheKey); await store.WriteCompressionAsync(work.Task.Id, pending.Id, 1, "partial", GenerationStatus.Running);
        await store.RecoverInterruptedAsync();
        Assert.Equal(GenerationStatus.Interrupted, (await store.LoadCompressionAttemptAsync(pending.Id))!.Status);
        Assert.Equal(GenerationStatus.Interrupted, (await store.LoadGenerationAsync(work.Task.Id))!.Status);
        Assert.NotNull(await store.LoadCompletedSummaryAsync(work.Task.Id, chunks[0].CacheKey));
        Assert.Null(await store.LoadCompletedSummaryAsync(work.Task.Id, chunks[1].CacheKey));
        Assert.False(await store.WriteCompressionAsync(work.Task.Id, pending.Id, 3, Summary, GenerationStatus.Completed, "stop"));
        var retry = await store.AcceptReplyAsync(conversation.Id, null, config);
        Assert.Equal(done.Id, (await store.LoadCompletedSummaryAsync(retry.Task.Id, chunks[0].CacheKey))!.AttemptId);
    }

    [Theory]
    [InlineData("ABORT")][InlineData("ROLLBACK")]
    public async Task Acceptance_plan_insert_fault_keeps_history_and_draft(string action)
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store);
        await store.SaveDraftAsync(conversation.Id, "current", 1);
        Sql(paths, $"CREATE TRIGGER fail_plan BEFORE INSERT ON reply_context_plan BEGIN SELECT RAISE({action},'fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => store.AcceptReplyAsync(conversation.Id, "current", Config()));
        Assert.Empty(await store.ReadMessagesAsync(conversation.Id)); Assert.Empty(await store.ListActiveGenerationsAsync());
        Assert.Equal(new ConversationDraft("current", 1), await store.LoadDraftAsync(conversation.Id));
        Sql(paths, "DROP TRIGGER fail_plan");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptReplyAsync(conversation.Id, new string('x', 8000), Config()));
        Assert.Empty(await store.ReadMessagesAsync(conversation.Id)); Assert.Equal("current", (await store.LoadDraftAsync(conversation.Id)).Text);
    }
    [Theory]
    [InlineData(GenerationStatus.Failed)][InlineData(GenerationStatus.Stopped)][InlineData(GenerationStatus.Interrupted)]
    public async Task Unsuccessful_attempts_never_supply_cache_and_new_attempt_is_allowed(GenerationStatus status)
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store); await History(store, conversation.Id);
        var work = await store.AcceptReplyAsync(conversation.Id, "current", Config()); var key = Chunks(work)[0].CacheKey;
        var first = await store.StartCompressionAsync(work.Task.Id, key);
        await store.WriteCompressionAsync(work.Task.Id, first.Id, 1, Summary, status, "stop");
        Assert.Null(await store.LoadCompletedSummaryAsync(work.Task.Id, key));
        Assert.False(await store.WriteCompressionAsync(work.Task.Id, first.Id, 2, Summary, GenerationStatus.Completed, "stop"));
        var next = await store.StartCompressionAsync(work.Task.Id, key); Assert.NotEqual(first.Id, next.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PrepareReplyRequestAsync(work.Task.Id));
        await store.WriteGenerationAsync(work.Task.Id, 1, "", GenerationStatus.Stopped);
        Assert.False(await store.WriteCompressionAsync(work.Task.Id, next.Id, 1, Summary, GenerationStatus.Completed, "stop"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StartCompressionAsync(work.Task.Id, key));
    }
    [Fact]
    public async Task Cached_payload_tampering_rejected_even_when_its_hash_is_recomputed()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store); await History(store, conversation.Id);
        var work = await store.AcceptReplyAsync(conversation.Id, "current", Config()); var key = Chunks(work)[0].CacheKey;
        var attempt = await store.StartCompressionAsync(work.Task.Id, key); await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, Summary, GenerationStatus.Completed, "stop");
        var forged = Summary.Replace("old facts", "forged facts");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(forged)));
        Sql(paths, "DROP TRIGGER context_cache_immutable; UPDATE context_summary_cache SET payload=$payload,payload_hash=$hash", ("$payload", forged), ("$hash", hash));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCompletedSummaryAsync(work.Task.Id, key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PrepareReplyRequestAsync(work.Task.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StartCompressionAsync(work.Task.Id, key));
    }
    [Fact]
    public async Task Sql_constraints_reject_terminal_parent_nonmonotonic_attempt_and_noncompleted_cache()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store); await History(store, conversation.Id);
        var work = await store.AcceptReplyAsync(conversation.Id, "current", Config()); var key = Chunks(work)[0].CacheKey;
        var attempt = await store.StartCompressionAsync(work.Task.Id, key);
        Assert.Throws<SqliteException>(() => Sql(paths, "UPDATE context_compression_attempt SET revision=0,status='Running' WHERE id=$id", ("$id", attempt.Id)));
        await store.WriteGenerationAsync(work.Task.Id, 1, "", GenerationStatus.Stopped);
        Assert.Throws<SqliteException>(() => Sql(paths, "UPDATE context_compression_attempt SET revision=1,status='Completed',finish_reason='stop' WHERE id=$id", ("$id", attempt.Id)));
        Assert.Throws<SqliteException>(() => Sql(paths, "INSERT INTO context_summary_cache VALUES($id,$key,$conversation,$attempt,'{}','{}','fake','hash',NULL,'time')",
            ("$id", Guid.NewGuid()), ("$key", key), ("$conversation", conversation.Id), ("$attempt", attempt.Id)));
    }
    [Fact]
    public async Task Prepared_material_reopens_with_exact_inputs_and_frozen_original_identity()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        PreparedReplyRequest prepared; Guid taskId; Guid[] ids;
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            var conversation = await Conversation(store); await History(store, conversation.Id);
            var work = await store.AcceptReplyAsync(conversation.Id, "current", Config()); taskId = work.Task.Id; ids = work.Task.InputMessageIds.ToArray();
            foreach (var chunk in Chunks(work))
            {
                var attempt = await store.StartCompressionAsync(taskId, chunk.CacheKey); await store.WriteCompressionAsync(taskId, attempt.Id, 1, Summary, GenerationStatus.Completed, "stop");
            }
            prepared = await store.PrepareReplyRequestAsync(taskId);
            await store.WriteGenerationAsync(taskId, 1, "answer", GenerationStatus.Completed);
            await BranchStorageTests.Answer(store, conversation.Id, null, "new attempt");
            Assert.Equal(prepared.Request.Messages, (await store.LoadPreparedReplyRequestAsync(taskId))!.Request.Messages);
        }
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(paths);
        var loaded = (await reopened.LoadPreparedReplyRequestAsync(taskId))!;
        Assert.Equal(prepared.Request.Messages, loaded.Request.Messages); Assert.Equal(prepared.Uses, loaded.Uses); Assert.Equal(prepared.PreparedAt, loaded.PreparedAt);
        Assert.Equal(ids, (await reopened.LoadContextPlanAsync(taskId))!.OriginalMessageIds);
        Assert.Equal(1L, Sql(paths, "SELECT count(*) FROM prepared_reply_request"));
    }
    [Fact]
    public async Task Final_actual_summary_budget_is_checked_without_truncating_or_recording_request()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store); await History(store, conversation.Id);
        var work = await store.AcceptReplyAsync(conversation.Id, "current", Config());
        foreach (var chunk in Chunks(work))
        {
            var attempt = await store.StartCompressionAsync(work.Task.Id, chunk.CacheKey);
            await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, Summary.Replace("old facts", new string('x', 7000)), GenerationStatus.Completed, "stop");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PrepareReplyRequestAsync(work.Task.Id));
        Assert.Null(await store.LoadPreparedReplyRequestAsync(work.Task.Id));
        Assert.Equal(12, (await store.ReadMessagesAsync(conversation.Id)).Count);
    }

    [Fact]
    public async Task Nested_branches_keep_old_ancestor_attempts_and_isolate_sibling_workspace_and_configuration()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var a = await Conversation(store); await History(store, a.Id);
        var sourceA = (await store.ReadMessagesAsync(a.Id))[^1];
        var b = await store.CreateBranchAsync(a.Id, BranchStorageTests.Selection(sourceA) with { Quote = "e" }, Guid.NewGuid());
        var sourceB = await BranchStorageTests.Answer(store, b.Id, "b question", "b source");
        var c = await store.CreateBranchAsync(b.Id, BranchStorageTests.Selection(sourceB), Guid.NewGuid());
        var sibling = await store.CreateBranchAsync(b.Id, BranchStorageTests.Selection(sourceB), Guid.NewGuid());
        var readState = new Mindweave.Core.Branches.ConversationReadState(c.Id, null, 1); await store.SaveReadStateAsync(readState);
        var config = Config(); var work = await store.AcceptReplyAsync(c.Id, "current", config); var key = Chunks(work)[0].CacheKey;
        Assert.Equal(sourceB.Id, work.ContextPlan!.DirectSourceMessageId);
        var before = (await store.ReadContextAsync(c.Id)).Messages.Where(m => m.ConversationId != c.Id).ToArray();
        var attempt = await store.StartCompressionAsync(work.Task.Id, key); await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, Summary, GenerationStatus.Completed, "stop");
        await BranchStorageTests.Answer(store, a.Id, null, "ancestor replacement");
        foreach (var chunk in Chunks(work).Skip(1))
        {
            var next = await store.StartCompressionAsync(work.Task.Id, chunk.CacheKey); await store.WriteCompressionAsync(work.Task.Id, next.Id, 1, Summary, GenerationStatus.Completed, "stop");
        }
        var prepared = await store.PrepareReplyRequestAsync(work.Task.Id);
        Assert.Equal(sourceB.Id, prepared.Uses[0].MessageId); Assert.Equal(ContextUseKind.SourceQuote, prepared.Uses[0].Kind);
        Assert.Contains(sourceA.Id, (await store.LoadContextPlanAsync(work.Task.Id))!.OriginalMessageIds);
        Assert.Equal(before, (await store.ReadContextAsync(c.Id)).Messages.Where(m => m.ConversationId != c.Id));
        Assert.Equal(readState, await store.LoadReadStateAsync(c.Id));
        var other = await store.AcceptReplyAsync(sibling.Id, "current", config);
        var shared = (await store.LoadCompletedSummaryAsync(other.Task.Id, key))!;
        Assert.Equal(attempt.Id, shared.AttemptId);
        Assert.Equal(a.Id, shared.SourceConversationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StartCompressionAsync(other.Task.Id, key));
        var otherWorkspace = await Conversation(store); await History(store, otherWorkspace.Id);
        var outside = await store.AcceptReplyAsync(otherWorkspace.Id, "current", config);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StartCompressionAsync(outside.Task.Id, key));
        await store.WriteGenerationAsync(work.Task.Id, 1, "", GenerationStatus.Failed);
        var changed = await store.AcceptReplyAsync(c.Id, null, config with { CredentialReference = "new-version" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCompletedSummaryAsync(changed.Task.Id, key));
        Assert.Null(await store.LoadCompletedSummaryAsync(changed.Task.Id, Chunks(changed)[0].CacheKey));
    }
    [Theory]
    [InlineData("source")][InlineData("current")][InlineData("chunk")]
    public async Task Persisted_plan_tampering_cannot_change_protected_source_current_question_or_chunk(string field)
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var a = await Conversation(store); await History(store, a.Id);
        var originals = await store.ReadMessagesAsync(a.Id); var source = originals[^1]; var olderAssistant = originals[1];
        var branch = await store.CreateBranchAsync(a.Id, BranchStorageTests.Selection(source) with { Quote = "e" }, Guid.NewGuid());
        var work = await store.AcceptReplyAsync(branch.Id, "current", Config()); var key = Chunks(work)[0].CacheKey;
        if (field == "chunk") Sql(paths, "DROP TRIGGER context_chunk_immutable; UPDATE context_plan_chunk SET chunk_json='{}' WHERE task_id=$id", ("$id", work.Task.Id));
        else
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse((string)Sql(paths, "SELECT plan_json FROM reply_context_plan WHERE task_id=$id", ("$id", work.Task.Id))!)!;
            if (field == "source") json["DirectSourceMessageId"] = olderAssistant.Id;
            else { var parts = json["Parts"]!.AsArray(); parts.RemoveAt(parts.Count - 1); }
            Sql(paths, "DROP TRIGGER context_plan_immutable; UPDATE reply_context_plan SET plan_json=$json WHERE task_id=$id", ("$json", json.ToJsonString()), ("$id", work.Task.Id));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadContextPlanAsync(work.Task.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StartCompressionAsync(work.Task.Id, key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PrepareReplyRequestAsync(work.Task.Id));
    }
    [Fact]
    public async Task Source_body_revision_and_hash_are_rechecked_before_reusing_completed_cache()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store); await History(store, conversation.Id);
        var work = await store.AcceptReplyAsync(conversation.Id, "current", Config()); var chunk = Chunks(work)[0];
        var attempt = await store.StartCompressionAsync(work.Task.Id, chunk.CacheKey); await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, Summary, GenerationStatus.Completed, "stop");
        Sql(paths, "DROP TRIGGER terminal_message_immutable; UPDATE chat_message SET body=body || 'tampered' WHERE id=$id", ("$id", chunk.Slices[0].MessageId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCompletedSummaryAsync(work.Task.Id, chunk.CacheKey));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PrepareReplyRequestAsync(work.Task.Id));
    }
    [Theory]
    [InlineData("messages")][InlineData("uses")]
    public async Task Reopened_prepared_material_is_verified_against_real_sources(string corrupt)
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store);
        var work = await store.AcceptReplyAsync(conversation.Id, "current", Config()); await store.PrepareReplyRequestAsync(work.Task.Id);
        if (corrupt == "messages") Sql(paths, "DROP TRIGGER prepared_request_immutable; UPDATE prepared_reply_request SET messages_json='[]'");
        else Sql(paths, "DELETE FROM prepared_context_use");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadPreparedReplyRequestAsync(work.Task.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PrepareReplyRequestAsync(work.Task.Id));
    }
    [Fact]
    public async Task Recovery_late_parent_failure_rolls_back_interruption_of_compression()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var conversation = await Conversation(store); await History(store, conversation.Id);
        var work = await store.AcceptReplyAsync(conversation.Id, "current", Config()); var attempt = await store.StartCompressionAsync(work.Task.Id, Chunks(work)[0].CacheKey);
        Sql(paths, "CREATE TRIGGER fail_recovery BEFORE UPDATE ON generation_task BEGIN SELECT RAISE(ROLLBACK,'fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => store.RecoverInterruptedAsync());
        Assert.Equal(GenerationStatus.Queued, (await store.LoadCompressionAttemptAsync(attempt.Id))!.Status);
        Assert.Equal(GenerationStatus.Queued, (await store.LoadGenerationAsync(work.Task.Id))!.Status);
    }

    [Fact]
    public async Task Sibling_ancestor_cache_and_producer_survive_completed_requester_leaf_deletion()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var a = await Conversation(store); await History(store, a.Id);
        var source = (await store.ReadMessagesAsync(a.Id))[^1];
        var b = await store.CreateBranchAsync(a.Id, BranchStorageTests.Selection(source) with { Quote = "e" }, Guid.NewGuid());
        var d = await store.CreateBranchAsync(a.Id, BranchStorageTests.Selection(source) with { Quote = "e" }, Guid.NewGuid());
        await BranchStorageTests.Answer(store, b.Id, "B-private-question", "B-private-answer");
        await BranchStorageTests.Answer(store, d.Id, "D-private-question", "D-private-answer");
        var config = Config() with { ContextTokenBudget = 4500 };
        var bw = await store.AcceptReplyAsync(b.Id, "B-current", config); var dw = await store.AcceptReplyAsync(d.Id, "D-current", config);
        var ids = new List<Guid>(); var producers = new List<Guid>();
        foreach (var chunk in Chunks(bw))
        {
            var producer = await store.StartCompressionAsync(bw.Task.Id, chunk.CacheKey); producers.Add(producer.Id);
            await store.WriteCompressionAsync(bw.Task.Id, producer.Id, 1, Summary, GenerationStatus.Completed, "stop");
            var cached = (await store.LoadCompletedSummaryAsync(dw.Task.Id, chunk.CacheKey))!;
            Assert.NotNull(cached); ids.Add(cached.Id);
        }
        var before = await store.PrepareReplyRequestAsync(dw.Task.Id);
        Assert.Equal(ids.Order(), before.Uses.Where(u => u.SummaryId != null).Select(u => u.SummaryId!.Value).Order());
        Assert.DoesNotContain(before.Request.Messages, m => m.Content.Contains("B-private"));
        Assert.Contains(before.Request.Messages, m => m.Content == "D-private-answer");
        Assert.Equal((long)producers.Count, Sql(paths, "SELECT count(*) FROM context_compression_attempt"));
        await store.PrepareReplyRequestAsync(bw.Task.Id); await store.WriteGenerationAsync(bw.Task.Id, 1, "B-final", GenerationStatus.Completed);
        Sql(paths, "BEGIN; PRAGMA defer_foreign_keys=ON; UPDATE app_selection SET kind='Overview',target_id=NULL; DELETE FROM generation_task WHERE conversation_id=$id; DELETE FROM conversation WHERE id=$id; COMMIT;", ("$id", b.Id));
        Assert.Null(await store.LoadConversationAsync(b.Id)); Assert.NotNull(await store.LoadConversationAsync(a.Id));
        foreach (var producer in producers) Assert.Equal(GenerationStatus.Completed, (await store.LoadCompressionAttemptAsync(producer))!.Status);
        Assert.Equal(before.Request.Messages, (await store.LoadPreparedReplyRequestAsync(dw.Task.Id))!.Request.Messages);
        var after = new List<Guid>();
        foreach (var chunk in Chunks(dw)) after.Add((await store.LoadCompletedSummaryAsync(dw.Task.Id, chunk.CacheKey))!.Id);
        Assert.Equal(ids.Order(), after.Order());
        Assert.Equal(0L, Sql(paths, "SELECT count(*) FROM context_compression_dependency WHERE task_id=$task", ("$task", bw.Task.Id)));
        await store.DisposeAsync();
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(paths);
        Assert.Equal(before.Request.Messages, (await reopened.LoadPreparedReplyRequestAsync(dw.Task.Id))!.Request.Messages);
        foreach (var producer in producers) Assert.Equal(a.Id, (await reopened.LoadCompressionAttemptAsync(producer))!.SourceConversationId);
        Assert.Equal(0L, Sql(paths, "SELECT count(*) FROM pragma_foreign_key_check"));
    }
    [Fact]
    public async Task Concurrent_sibling_completions_keep_first_cache_without_failing_second_valid_completion()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var a = await Conversation(store); await History(store, a.Id);
        var source = (await store.ReadMessagesAsync(a.Id))[^1];
        var b = await store.CreateBranchAsync(a.Id, BranchStorageTests.Selection(source) with { Quote = "e" }, Guid.NewGuid());
        var d = await store.CreateBranchAsync(a.Id, BranchStorageTests.Selection(source) with { Quote = "e" }, Guid.NewGuid());
        var config = Config(); var bw = await store.AcceptReplyAsync(b.Id, "B-current", config); var dw = await store.AcceptReplyAsync(d.Id, "D-current", config);
        var key = Chunks(bw)[0].CacheKey;
        var ba = await store.StartCompressionAsync(bw.Task.Id, key); var da = await store.StartCompressionAsync(dw.Task.Id, key);
        Assert.NotEqual(ba.Id, da.Id);
        Assert.False(await store.WriteCompressionAsync(dw.Task.Id, ba.Id, 1, Summary, GenerationStatus.Completed, "stop"));
        Assert.Equal(GenerationStatus.Queued, (await store.LoadCompressionAttemptAsync(ba.Id))!.Status);
        Assert.True(await store.WriteCompressionAsync(bw.Task.Id, ba.Id, 1, Summary, GenerationStatus.Completed, "stop"));
        var first = (await store.LoadCompletedSummaryAsync(bw.Task.Id, key))!;
        Assert.True(await store.WriteCompressionAsync(dw.Task.Id, da.Id, 1, Summary.Replace("old facts", "other valid summary"), GenerationStatus.Completed, "stop"));
        Assert.Equal(first.Id, (await store.LoadCompletedSummaryAsync(dw.Task.Id, key))!.Id);
        Assert.Equal(ba.Id, (await store.LoadCompletedSummaryAsync(dw.Task.Id, key))!.AttemptId);
        Assert.Equal(GenerationStatus.Completed, (await store.LoadCompressionAttemptAsync(da.Id))!.Status);
    }

    [Fact]
    public async Task Shared_ancestor_summary_reuse_never_authorizes_sibling_private_or_later_ancestor_chunks()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var a = await Conversation(store); await History(store, a.Id);
        var originalIds = (await store.ReadMessagesAsync(a.Id)).Select(m => m.Id).ToHashSet();
        var source = (await store.ReadMessagesAsync(a.Id))[^1];
        var b = await store.CreateBranchAsync(a.Id, BranchStorageTests.Selection(source) with { Quote = "e" }, Guid.NewGuid());
        var d = await store.CreateBranchAsync(a.Id, BranchStorageTests.Selection(source) with { Quote = "e" }, Guid.NewGuid());
        for (var i = 0; i < 5; i++)
        {
            await BranchStorageTests.Answer(store, b.Id, "B-private-" + i, new string('b', 1500));
            await BranchStorageTests.Answer(store, d.Id, "D-private-" + i, new string('d', 1500));
        }
        var config = Config(); var bw = await store.AcceptReplyAsync(b.Id, "B-current", config); var dw = await store.AcceptReplyAsync(d.Id, "D-current", config);
        Assert.Contains(Chunks(bw), c => c.SourceConversationId == b.Id);
        Assert.Contains(Chunks(dw), c => c.SourceConversationId == d.Id);
        foreach (var chunk in Chunks(bw))
        {
            var producer = await store.StartCompressionAsync(bw.Task.Id, chunk.CacheKey);
            var payload = chunk.SourceConversationId == a.Id ? Summary : Summary.Replace("old facts", "B-private-summary");
            await store.WriteCompressionAsync(bw.Task.Id, producer.Id, 1, payload, GenerationStatus.Completed, "stop");
            if (chunk.SourceConversationId == a.Id)
                Assert.Equal(producer.Id, (await store.LoadCompletedSummaryAsync(dw.Task.Id, chunk.CacheKey))!.AttemptId);
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCompletedSummaryAsync(dw.Task.Id, chunk.CacheKey));
        }
        foreach (var chunk in Chunks(dw).Where(c => c.SourceConversationId == d.Id))
        {
            var producer = await store.StartCompressionAsync(dw.Task.Id, chunk.CacheKey);
            await store.WriteCompressionAsync(dw.Task.Id, producer.Id, 1, Summary.Replace("old facts", "D-private-summary"), GenerationStatus.Completed, "stop");
        }
        var prepared = await store.PrepareReplyRequestAsync(dw.Task.Id);
        Assert.DoesNotContain(prepared.Request.Messages, m => m.Content.Contains("B-private"));
        Assert.Contains(prepared.Request.Messages, m => m.Content.Contains("D-private-summary"));
        await History(store, a.Id);
        var later = (await store.ReadMessagesAsync(a.Id))[^1];
        var e = await store.CreateBranchAsync(a.Id, BranchStorageTests.Selection(later) with { Quote = "e" }, Guid.NewGuid());
        var ew = await store.AcceptReplyAsync(e.Id, "later child", config);
        var laterChunk = Chunks(ew).First(c => c.Slices.Any(slice => !originalIds.Contains(slice.MessageId)));
        var lateProducer = await store.StartCompressionAsync(ew.Task.Id, laterChunk.CacheKey);
        await store.WriteCompressionAsync(ew.Task.Id, lateProducer.Id, 1, Summary, GenerationStatus.Completed, "stop");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCompletedSummaryAsync(bw.Task.Id, laterChunk.CacheKey));
        Assert.DoesNotContain((await store.LoadContextPlanAsync(bw.Task.Id))!.OriginalMessageIds, id => id == later.Id);
    }
    [Fact]
    public async Task Producer_source_rows_are_required_and_bound_to_the_source_conversation()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths); var a = await Conversation(store); await History(store, a.Id);
        var source = (await store.ReadMessagesAsync(a.Id))[^1];
        var b = await store.CreateBranchAsync(a.Id, BranchStorageTests.Selection(source) with { Quote = "e" }, Guid.NewGuid());
        var work = await store.AcceptReplyAsync(b.Id, "private current", Config()); var chunk = Chunks(work)[0];
        var attempt = await store.StartCompressionAsync(work.Task.Id, chunk.CacheKey);
        Sql(paths, "DROP TRIGGER compression_source_immutable");
        Assert.Throws<SqliteException>(() => Sql(paths, "UPDATE context_compression_source SET message_id=$message WHERE attempt_id=$attempt AND slice_order=0",
            ("$message", work.InputMessages[^1].Id), ("$attempt", attempt.Id)));
        await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, Summary, GenerationStatus.Completed, "stop");
        Sql(paths, "DELETE FROM context_compression_source WHERE attempt_id=$attempt AND slice_order=0", ("$attempt", attempt.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCompressionAttemptAsync(attempt.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCompletedSummaryAsync(work.Task.Id, chunk.CacheKey));
    }
}
