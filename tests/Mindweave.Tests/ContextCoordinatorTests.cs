using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;
using static Mindweave.Tests.GenerationCoordinatorTests;

namespace Mindweave.Tests;

public sealed class ContextCoordinatorTests
{
    private const string Summary = """{"Background":"old facts","Requirements":"","Corrections":"","OpenQuestions":"","References":""}""";
    private static async Task<Conversation> LongConversation(Fixture f)
    {
        var c = await f.ConversationAsync();
        for (var i = 0; i < 5; i++) await BranchStorageTests.Answer(f.Store, c.Id, "question" + i, new string((char)('a' + i), 1500));
        await f.Chat.RenameConversationAsync(c.Id, "manual");
        await f.Chat.SaveConfigurationAsync(f.Configuration with { ContextTokenBudget = 7000, MaxOutputTokens = 128 });
        return c;
    }
    private static bool IsSummary(Call call) => call.Request.Messages[0].Content.Contains("Summarize the supplied original", StringComparison.Ordinal);
    private static void Complete(Call call, string text) { call.Emit(text, "stop"); call.Finish(); }

    [Fact]
    public async Task Two_compressing_replies_advance_without_leaking_summary_and_send_durable_material()
    {
        await using var f = await Fixture.OpenAsync();
        var a = await LongConversation(f); var b = await LongConversation(f);
        var aId = await f.Coordinator.SendAsync(a.Id, "current-a");
        var bId = await f.Coordinator.SendAsync(b.Id, "current-b");
        var first = await f.Transport.NextAsync(); var second = await f.Transport.NextAsync();
        Assert.True(IsSummary(first)); Assert.True(IsSummary(second));
        Complete(first, Summary); Complete(second, Summary);
        var answered = new HashSet<Guid>();
        while (answered.Count < 2)
        {
            var call = await f.Transport.NextAsync();
            if (IsSummary(call)) { Complete(call, Summary); continue; }
            var id = call.Request.Messages[^1].Content == "current-a" ? aId : bId;
            var prepared = await f.Chat.LoadPreparedReplyRequestAsync(id);
            Assert.NotNull(prepared);
            Assert.Equal(prepared.Request.Messages, call.Request.Messages);
            Assert.Empty(f.Snapshot(id).Body);
            Complete(call, "answer"); answered.Add(id);
        }
        await f.WaitAsync(() => answered.All(id => f.Snapshot(id).Status == GenerationStatus.Completed && !f.Snapshot(id).HasUnsavedChanges));
        Assert.All(answered, id => Assert.Equal("answer", f.Snapshot(id).Body));
        Assert.Equal(2, f.Transport.PeakActive);
    }

    [Theory]
    [InlineData("{}", "stop", "InvalidContextSummary")]
    [InlineData("", "stop", "EmptyContextSummary")]
    [InlineData("partial", null, "IncompleteContextSummary")]
    [InlineData("partial", "length", "UnsupportedContextFinish")]
    public async Task Invalid_summary_fails_parent_without_answer_or_cache(string payload, string? finish, string error)
    {
        await using var f = await Fixture.OpenAsync(); var c = await LongConversation(f);
        var id = await f.Coordinator.SendAsync(c.Id, "current"); var call = await f.Transport.NextAsync();
        call.Emit(payload, finish); call.Finish();
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Failed && !f.Snapshot(id).HasUnsavedChanges);
        Assert.Equal(error, f.Snapshot(id).Error); Assert.Empty(f.Snapshot(id).Body);
        Assert.Equal(0L, ContextStorageTests.Sql(f.Paths, "SELECT count(*) FROM context_summary_cache"));
        Assert.Equal("current", (await f.Chat.ReadMessagesAsync(c.Id))[^2].Body);
        Assert.Equal(1, f.Transport.StartCount);
        Assert.False(f.Coordinator.HasPersistenceFailure);
    }

    [Fact]
    public async Task Raw_reply_records_physical_material_before_transport()
    {
        await using var f = await Fixture.OpenAsync(); var c = await f.ConversationAsync();
        var id = await f.Coordinator.SendAsync(c.Id, "raw"); var call = await f.Transport.NextAsync();
        var prepared = await f.Chat.LoadPreparedReplyRequestAsync(id);
        Assert.NotNull(prepared); Assert.Equal(call.Request.Messages, prepared.Request.Messages);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Lost_start_result_is_reconciled_before_stop_or_shutdown_parent_terminal(bool shutdown)
    {
        ChatDesktopTests.ControlledChatStore? proxy = null;
        await using var f = await Fixture.OpenAsync(wrapStore: store => proxy = new(store) { LoseCompressionStartReturn = true });
        var c = await LongConversation(f); var id = await f.Coordinator.SendAsync(c.Id, "current");
        await f.WaitAsync(() => f.Coordinator.HasPersistenceFailure);
        Assert.Equal(1L, ContextStorageTests.Sql(f.Paths, "SELECT count(*) FROM context_compression_attempt WHERE status='Queued'"));
        if (shutdown) await f.Coordinator.ShutdownAsync();
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.StopAsync(c.Id));
            await f.Coordinator.RetryPersistenceAsync();
        }
        Assert.Equal(GenerationStatus.Stopped, (await f.Chat.LoadGenerationAsync(id))!.Status);
        Assert.Equal(0L, ContextStorageTests.Sql(f.Paths, "SELECT count(*) FROM context_compression_attempt WHERE status IN ('Queued','Running')"));
        Assert.Equal(1L, ContextStorageTests.Sql(f.Paths, "SELECT count(*) FROM context_compression_attempt WHERE status='Stopped'"));
        Assert.Equal(0, f.Transport.StartCount);
    }

    [Fact]
    public async Task Stop_retains_slot_through_late_summary_and_delayed_disposal()
    {
        await using var f = await Fixture.OpenAsync(ignoreCancellation: true);
        var a = await LongConversation(f); var b = await LongConversation(f); var c = await f.ConversationAsync();
        var aId = await f.Coordinator.SendAsync(a.Id, "a"); var first = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(b.Id, "b"); var second = await f.Transport.NextAsync();
        var cId = await f.Coordinator.SendAsync(c.Id, "c");
        first.DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Coordinator.StopAsync(a.Id);
        Complete(first, Summary);
        await first.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GenerationStatus.Queued, f.Snapshot(cId).Status);
        Assert.Equal(2, f.Transport.StartCount);
        Assert.Equal(0L, ContextStorageTests.Sql(f.Paths, "SELECT count(*) FROM context_summary_cache"));
        first.DisposeRelease.TrySetResult();
        var third = await f.Transport.NextAsync(); Assert.Equal("c", third.Request.Messages[^1].Content);
        Assert.Equal(2, f.Transport.PeakActive); Assert.Empty(f.Snapshot(aId).Body);
        second.Fail("Timeout");
    }

    [Fact]
    public async Task Oversized_summary_fails_without_poisoning_persistence_or_cache()
    {
        await using var f = await Fixture.OpenAsync(); var c = await LongConversation(f);
        var id = await f.Coordinator.SendAsync(c.Id, "current"); var call = await f.Transport.NextAsync();
        Complete(call, new string('界', 40000));
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Failed && !f.Snapshot(id).HasUnsavedChanges);
        Assert.Equal("ContextSummaryTooLarge", f.Snapshot(id).Error); Assert.Empty(f.Snapshot(id).Body);
        Assert.False(f.Coordinator.HasPersistenceFailure);
        Assert.Equal(0L, ContextStorageTests.Sql(f.Paths, "SELECT count(*) FROM context_summary_cache"));
    }

    [Fact]
    public async Task Stop_during_running_checkpoint_commits_child_terminal_before_parent()
    {
        ChatDesktopTests.ControlledChatStore? proxy = null;
        await using var f = await Fixture.OpenAsync(wrapStore: store => proxy = new(store));
        var c = await LongConversation(f); var id = await f.Coordinator.SendAsync(c.Id, "current"); var call = await f.Transport.NextAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy!.BeforeCompressionWrite = async status =>
        {
            if (status != GenerationStatus.Running) return;
            entered.TrySetResult(); await release.Task;
        };
        f.Sql("CREATE TRIGGER child_first BEFORE UPDATE ON generation_task WHEN NEW.status='Stopped' AND EXISTS(SELECT 1 FROM context_compression_dependency d JOIN context_compression_attempt a ON a.id=d.attempt_id WHERE d.task_id=NEW.id AND a.status IN ('Queued','Running')) BEGIN SELECT RAISE(ABORT,'child still active'); END");
        call.Emit("partial");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = f.Coordinator.StopAsync(c.Id);
        release.TrySetResult();
        await stop;
        Assert.False(f.Coordinator.HasPersistenceFailure);
        Assert.Equal(GenerationStatus.Stopped, (await f.Chat.LoadGenerationAsync(id))!.Status);
        Assert.Equal(1L, ContextStorageTests.Sql(f.Paths, "SELECT count(*) FROM context_compression_attempt WHERE status='Stopped' AND payload='partial'"));
    }

    [Theory]
    [InlineData("start")] [InlineData("cache")] [InlineData("prepared")] [InlineData("parent")]
    public async Task Phase_save_faults_pause_globally_and_explicit_repair_resumes_without_resending_saved_work(string boundary)
    {
        await using var f = await Fixture.OpenAsync(); var c = await LongConversation(f); var other = await f.ConversationAsync();
        var trigger = boundary switch
        {
            "start" => "BEFORE INSERT ON context_compression_attempt",
            "cache" => "BEFORE INSERT ON context_summary_cache",
            "prepared" => "BEFORE INSERT ON prepared_context_use WHEN NEW.input_index=1",
            _ => "BEFORE UPDATE ON generation_task WHEN NEW.status='Completed'"
        };
        f.Sql($"CREATE TRIGGER fail_phase {trigger} BEGIN SELECT RAISE(ABORT,'phase fault'); END");
        var id = await f.Coordinator.SendAsync(c.Id, "current");
        var plan = (await f.Chat.LoadContextPlanAsync(id))!;
        var chunks = plan.Parts.OfType<SummaryContextPart>().ToArray();
        var sentSummaries = boundary == "start" ? 0 : boundary == "cache" ? 1 : chunks.Length;
        for (var i = 0; i < sentSummaries; i++)
        {
            var summary = await f.Transport.NextAsync(); Assert.True(IsSummary(summary)); Complete(summary, Summary);
        }
        if (boundary == "parent") Complete(await f.Transport.NextAsync(), "answer retained");
        await f.WaitAsync(() => f.Coordinator.HasPersistenceFailure);
        Assert.Equal(boundary == "parent" ? "answer retained" : "", f.Snapshot(id).Body);
        Assert.NotEqual(GenerationStatus.Completed, (await f.Chat.LoadGenerationAsync(id))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.SendAsync(other.Id, "blocked"));
        f.Coordinator.ReportPersistenceFailure();
        f.Sql("DROP TRIGGER fail_phase");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.FlushPendingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.RetryPersistenceAsync());
        var countBeforeRepair = f.Transport.StartCount;
        await f.Coordinator.RetryPersistenceAsync(() =>
        {
            Assert.Equal(countBeforeRepair, f.Transport.StartCount);
            Assert.True(f.Coordinator.HasPersistenceFailure); return Task.CompletedTask;
        });
        if (boundary != "parent")
        {
            var reply = await NextReply(f); Complete(reply, "answer retained");
        }
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Completed && !f.Snapshot(id).HasUnsavedChanges);
        Assert.False(f.Coordinator.HasPersistenceFailure);
        Assert.Equal(chunks.Length + 1, f.Transport.StartCount);
        Assert.Equal("answer retained", (await f.Chat.ReadMessagesAsync(c.Id))[^1].Body);
        Assert.NotNull(await f.Chat.LoadPreparedReplyRequestAsync(id));
    }

    private static async Task<Call> NextReply(Fixture f)
    {
        while (true)
        {
            var call = await f.Transport.NextAsync();
            if (!IsSummary(call)) return call;
            Complete(call, Summary);
        }
    }

    [Fact]
    public async Task Lost_terminal_commit_return_reconciles_exact_attempt_and_never_repeats_compression()
    {
        await using var f = await Fixture.OpenAsync(wrapStore: store => new ChatDesktopTests.ControlledChatStore(store) { LoseCompressionWriteReturn = true });
        var c = await LongConversation(f); var id = await f.Coordinator.SendAsync(c.Id, "current");
        Complete(await f.Transport.NextAsync(), Summary);
        await f.WaitAsync(() => f.Coordinator.HasPersistenceFailure);
        Assert.Equal(1L, ContextStorageTests.Sql(f.Paths, "SELECT count(*) FROM context_summary_cache"));
        await f.Coordinator.RetryPersistenceAsync();
        Complete(await NextReply(f), "answer");
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Completed && !f.Snapshot(id).HasUnsavedChanges);
        var chunks = (await f.Chat.LoadContextPlanAsync(id))!.Parts.OfType<SummaryContextPart>().Count();
        Assert.Equal(chunks + 1, f.Transport.StartCount);
        Assert.False(f.Coordinator.HasPersistenceFailure);
    }

    private static async Task<(Conversation B, Conversation D)> Siblings(Fixture f)
    {
        var root = await LongConversation(f);
        var source = (await f.Chat.ReadMessagesAsync(root.Id))[^1];
        var b = await f.Store.CreateBranchAsync(root.Id, BranchStorageTests.Selection(source), Guid.NewGuid());
        var d = await f.Store.CreateBranchAsync(root.Id, BranchStorageTests.Selection(source), Guid.NewGuid());
        await f.Chat.RenameConversationAsync(b.Id, "B"); await f.Chat.RenameConversationAsync(d.Id, "D");
        return (b, d);
    }

    [Fact]
    public async Task Stopping_one_sibling_does_not_stop_other_producer_and_retry_reuses_winner()
    {
        await using var f = await Fixture.OpenAsync(ignoreCancellation: true); var (b, d) = await Siblings(f);
        var bId = await f.Coordinator.SendAsync(b.Id, "B current"); var bc = await f.Transport.NextAsync();
        var dId = await f.Coordinator.SendAsync(d.Id, "D current"); var dc = await f.Transport.NextAsync();
        await f.Coordinator.StopAsync(b.Id);
        Assert.False(dc.Cancellation.IsCancellationRequested); Assert.True(bc.Cancellation.IsCancellationRequested);
        Complete(bc, Summary.Replace("old facts", "late B")); Complete(dc, Summary);
        Complete(await NextReply(f), "D answer");
        await f.WaitAsync(() => f.Snapshot(dId).Status == GenerationStatus.Completed && !f.Snapshot(dId).HasUnsavedChanges);
        var retry = await f.Coordinator.RetryAsync(b.Id); var retryCall = await f.Transport.NextAsync();
        Assert.False(IsSummary(retryCall)); Assert.Equal("B current", retryCall.Request.Messages[^1].Content);
        Assert.DoesNotContain(retryCall.Request.Messages, m => m.Content.Contains("late B", StringComparison.Ordinal));
        Assert.Equal(GenerationStatus.Stopped, f.Snapshot(bId).Status); Assert.Empty(f.Snapshot(bId).Body);
        Complete(retryCall, "B answer");
        await f.WaitAsync(() => f.Snapshot(retry).Status == GenerationStatus.Completed && !f.Snapshot(retry).HasUnsavedChanges);
    }

    [Fact]
    public async Task Concurrent_valid_losing_attempt_never_supplies_final_request()
    {
        await using var f = await Fixture.OpenAsync(); var (b, d) = await Siblings(f);
        var bId = await f.Coordinator.SendAsync(b.Id, "B current"); var bc = await f.Transport.NextAsync();
        var dId = await f.Coordinator.SendAsync(d.Id, "D current"); var dc = await f.Transport.NextAsync();
        Complete(bc, Summary.Replace("old facts", "winning facts"));
        var next = await f.Transport.NextAsync(); // The next phase proves the first cache committed.
        Complete(dc, Summary.Replace("old facts", "losing facts"));
        var answers = 0;
        while (answers < 2)
        {
            var call = next; next = null!;
            if (IsSummary(call)) Complete(call, Summary);
            else
            {
                Assert.Contains(call.Request.Messages, m => m.Content.Contains("winning facts", StringComparison.Ordinal));
                Assert.DoesNotContain(call.Request.Messages, m => m.Content.Contains("losing facts", StringComparison.Ordinal));
                Complete(call, "answer"); answers++;
            }
            if (answers < 2) next = await f.Transport.NextAsync();
        }
        await f.WaitAsync(() => new[] { bId, dId }.All(id => f.Snapshot(id).Status == GenerationStatus.Completed && !f.Snapshot(id).HasUnsavedChanges));
        Assert.False(f.Coordinator.HasPersistenceFailure);
        Assert.Equal(1L, ContextStorageTests.Sql(f.Paths, "SELECT count(*) FROM context_compression_attempt WHERE status='Completed' AND payload LIKE '%losing facts%'"));
    }

    [Fact]
    public async Task Cache_completed_between_read_and_start_is_normal_race_not_save_failure()
    {
        ChatDesktopTests.ControlledChatStore? proxy = null;
        await using var f = await Fixture.OpenAsync(wrapStore: store => proxy = new(store));
        var (b, d) = await Siblings(f);
        var sibling = await f.Store.AcceptReplyAsync(d.Id, "D current", (await f.Chat.LoadActiveConfigurationAsync())!);
        proxy!.BeforeCompressionStart = async (_, key) =>
        {
            var attempt = await f.Store.StartCompressionAsync(sibling.Task.Id, key);
            await f.Store.WriteCompressionAsync(sibling.Task.Id, attempt.Id, 1, Summary, GenerationStatus.Completed, "stop");
        };
        var id = await f.Coordinator.SendAsync(b.Id, "B current"); var call = await f.Transport.NextAsync();
        Assert.False(IsSummary(call)); Assert.Equal(1, f.Transport.StartCount);
        Assert.False(f.Coordinator.HasPersistenceFailure); Complete(call, "B answer");
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Completed && !f.Snapshot(id).HasUnsavedChanges);
        await f.Store.WriteGenerationAsync(sibling.Task.Id, 1, "", GenerationStatus.Stopped);
    }

    [Fact]
    public async Task Structurally_valid_but_long_summary_fails_final_budget_without_reply()
    {
        await using var f = await Fixture.OpenAsync(); var c = await LongConversation(f);
        var id = await f.Coordinator.SendAsync(c.Id, "current");
        var chunks = (await f.Chat.LoadContextPlanAsync(id))!.Parts.OfType<SummaryContextPart>().Count();
        for (var i = 0; i < chunks; i++)
            Complete(await f.Transport.NextAsync(), Summary.Replace("old facts", new string('x', 6000)));
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Failed && !f.Snapshot(id).HasUnsavedChanges);
        Assert.Equal("ContextBudgetExceeded", f.Snapshot(id).Error);
        Assert.Equal(chunks, f.Snapshot(id).CompletedContextChunks);
        Assert.Equal(chunks, f.Transport.StartCount); Assert.False(f.Coordinator.HasPersistenceFailure);
        Assert.Null(await f.Chat.LoadPreparedReplyRequestAsync(id)); Assert.Empty(f.Snapshot(id).Body);
    }

    [Fact]
    public async Task All_phases_keep_accepted_configuration_and_summary_reserves_configured_output()
    {
        await using var f = await Fixture.OpenAsync(); var c = await LongConversation(f);
        var accepted = f.Configuration with { ContextTokenBudget = 7000 };
        await f.Chat.SaveConfigurationAsync(accepted);
        var id = await f.Coordinator.SendAsync(c.Id, "current");
        var first = await f.Transport.NextAsync(); Assert.True(IsSummary(first));
        await f.Chat.SaveConfigurationAsync(accepted with { ModelId = "new-model", CredentialReference = "v2" });
        var call = first;
        while (IsSummary(call))
        {
            Assert.Equal(accepted with { MaxOutputTokens = 2048 }, call.Request.Configuration);
            Assert.Equal("old-secret", call.Secret); Assert.True(ChatRequestBudget.Fits(call.Request.Configuration, call.Request.Messages));
            Complete(call, Summary); call = await f.Transport.NextAsync();
        }
        Assert.Equal(accepted, call.Request.Configuration); Assert.Equal("old-secret", call.Secret);
        Assert.True(ChatRequestBudget.Fits(call.Request.Configuration, call.Request.Messages));
        Complete(call, "answer");
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Completed && !f.Snapshot(id).HasUnsavedChanges);
        Assert.Equal(f.Snapshot(id).TotalContextChunks, f.Snapshot(id).CompletedContextChunks);
        Assert.Equal(GenerationPhase.Reply, f.Snapshot(id).Phase);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 0)]
    public async Task Summary_policy_is_frozen_for_all_accepted_phases_and_only_next_task_changes(int initial, int next)
    {
        await using var f = await Fixture.OpenAsync(); var c = await LongConversation(f);
        var accepted = ContextThinkingTests.Property(f.Configuration with { BaseUrl = "https://api.deepseek.com/v1", ContextTokenBudget = 7000 }, "SummaryThinking", initial);
        await f.Chat.SaveConfigurationAsync(accepted);
        var id = await f.Coordinator.SendAsync(c.Id, "current");
        var call = await f.Transport.NextAsync(); Assert.True(IsSummary(call));
        var changed = ContextThinkingTests.Property(accepted with { ModelId = "new-model", CredentialReference = "v2" }, "SummaryThinking", next);
        await f.Chat.SaveConfigurationAsync(changed);
        while (IsSummary(call))
        {
            Assert.Equal(1, ContextThinkingTests.Number(call.Request, "Purpose"));
            Assert.Equal(initial, ContextThinkingTests.Number(call.Request.Configuration, "SummaryThinking"));
            Assert.Equal(accepted, call.Request.Configuration); Assert.Equal("old-secret", call.Secret);
            Complete(call, Summary); call = await f.Transport.NextAsync();
        }
        Assert.Equal(0, ContextThinkingTests.Number(call.Request, "Purpose")); Assert.Equal(accepted, call.Request.Configuration);
        Complete(call, "answer");
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Completed && !f.Snapshot(id).HasUnsavedChanges);
        var nextId = await f.Coordinator.SendAsync(c.Id, "next");
        call = await f.Transport.NextAsync();
        Assert.Equal(next, ContextThinkingTests.Number(call.Request.Configuration, "SummaryThinking"));
        Assert.Equal("new-model", call.Request.Configuration.ModelId); Assert.Equal("new-secret", call.Secret);
        await f.Coordinator.StopAsync(c.Id);
        Assert.Equal(GenerationStatus.Stopped, f.Snapshot(nextId).Status);
    }

    [Fact]
    public async Task Disabled_policy_keeps_actual_reply_and_auto_title_standard()
    {
        await using var f = await Fixture.OpenAsync(); var c = await f.ConversationAsync();
        var config = ContextThinkingTests.Property(f.Configuration with { BaseUrl = "https://api.deepseek.com" }, "SummaryThinking", 1);
        await f.Chat.SaveConfigurationAsync(config);
        var id = await f.Coordinator.SendAsync(c.Id, "short"); var reply = await f.Transport.NextAsync();
        Assert.Equal(0, ContextThinkingTests.Number(reply.Request, "Purpose"));
        Assert.Equal(1, ContextThinkingTests.Number(reply.Request.Configuration, "SummaryThinking"));
        Complete(reply, "answer"); var title = await f.Transport.NextAsync();
        Assert.Contains("Generate a concise conversation title", title.Request.Messages[0].Content);
        Assert.Equal(0, ContextThinkingTests.Number(title.Request, "Purpose"));
        Assert.Equal(1, ContextThinkingTests.Number(title.Request.Configuration, "SummaryThinking"));
        Complete(title, "a title");
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Completed && !f.Snapshot(id).HasUnsavedChanges);
    }

    [Fact]
    public async Task Shutdown_waits_for_old_phase_tail_after_its_slot_is_released()
    {
        await using var f = await Fixture.OpenAsync(); var c = await LongConversation(f); var other = await f.ConversationAsync();
        var id = await f.Coordinator.SendAsync(c.Id, "current"); var call = await f.Transport.NextAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = 0;
        void HoldTail()
        {
            if (call.Disposed.Task.IsCompleted && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); }
        }
        f.Coordinator.Changed += HoldTail;
        try
        {
            Complete(call, Summary);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Coordinator.SendAsync(other.Id, "other");
            var otherCall = await f.Transport.NextAsync();
            Assert.Equal("other", otherCall.Request.Messages[^1].Content);
            var shutdown = f.Coordinator.ShutdownAsync();
            await otherCall.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(shutdown.IsCompleted);
            release.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(GenerationStatus.Stopped, f.Snapshot(id).Status);
            Assert.Equal(2, f.Transport.StartCount); // No phase overlaps its own pending tail.
        }
        finally { release.Set(); f.Coordinator.Changed -= HoldTail; }
    }

    [Fact]
    public async Task Reopen_recovers_attempts_without_dispatch_and_projects_completed_cache_for_explicit_retry()
    {
        using var directory = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", directory.Path);
        Guid conversation, task, pending, completed; int total;
        var config = BranchStorageTests.Config(4500) with { CredentialReference = "v1" };
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            conversation = (await store.CreateConversationAsync((await store.CreateAsync("test")).Id)).Id;
            await store.RenameConversationAsync(conversation, "manual");
            for (var i = 0; i < 5; i++) await BranchStorageTests.Answer(store, conversation, "q" + i, new string('x', 1500));
            await store.SaveConfigurationAsync(config);
            var work = await store.AcceptReplyAsync(conversation, "current", config); task = work.Task.Id;
            var chunks = work.ContextPlan!.Parts.OfType<SummaryContextPart>().ToArray(); total = chunks.Length;
            Assert.True(total >= 2);
            var first = await store.StartCompressionAsync(task, chunks[0].Chunk.CacheKey); completed = first.Id;
            await store.WriteCompressionAsync(task, first.Id, 1, Summary, GenerationStatus.Completed, "stop");
            var second = await store.StartCompressionAsync(task, chunks[1].Chunk.CacheKey); pending = second.Id;
            await store.WriteCompressionAsync(task, second.Id, 1, "partial", GenerationStatus.Running);
        }
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(paths);
        var transport = new ControlledTransport();
        await using var coordinator = new GenerationCoordinator(reopened, transport, new MemoryVault());
        await coordinator.InitializeAsync();
        var recovered = Assert.Single(coordinator.Snapshots);
        Assert.Equal(GenerationStatus.Interrupted, recovered.Status); Assert.Equal(task, recovered.TaskId);
        Assert.Equal(GenerationStatus.Interrupted, (await reopened.LoadCompressionAttemptAsync(pending))!.Status);
        Assert.Equal(GenerationStatus.Completed, (await reopened.LoadCompressionAttemptAsync(completed))!.Status);
        Assert.Equal(1, recovered.CompletedContextChunks); Assert.Equal(total, recovered.TotalContextChunks);
        Assert.Equal(0, transport.StartCount); Assert.False(coordinator.HasActiveWork);
        await coordinator.RetryAsync(conversation);
        for (var i = 1; i < total; i++)
        {
            var call = await transport.NextAsync(); Assert.True(IsSummary(call)); Complete(call, Summary);
        }
        var reply = await transport.NextAsync(); Assert.False(IsSummary(reply));
        Assert.Equal(total, transport.StartCount);
    }

    [Fact]
    public async Task External_save_gate_cannot_be_bypassed_by_summary_flush()
    {
        await using var f = await Fixture.OpenAsync(); var c = await LongConversation(f);
        var id = await f.Coordinator.SendAsync(c.Id, "current"); var call = await f.Transport.NextAsync();
        f.Coordinator.ReportPersistenceFailure(); Complete(call, Summary);
        await call.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Coordinator.FlushPendingAsync();
        Assert.True(f.Coordinator.HasPersistenceFailure); Assert.Equal(1, f.Transport.StartCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.RetryPersistenceAsync());
        await f.Coordinator.RetryPersistenceAsync(() => Task.CompletedTask);
        Complete(await NextReply(f), "answer");
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Completed && !f.Snapshot(id).HasUnsavedChanges);
    }

    [Fact]
    public async Task Ordinary_reply_and_necessary_compression_dispatch_before_pending_title()
    {
        await using var f = await Fixture.OpenAsync();
        var titleOwner = await f.ConversationAsync(); var compressed = await LongConversation(f); var ordinary = await f.ConversationAsync();
        await f.Coordinator.SendAsync(titleOwner.Id, "short"); var shortCall = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(compressed.Id, "compressed current"); var summary = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(ordinary.Id, "ordinary current");
        Complete(shortCall, "answer");
        var ordinaryCall = await f.Transport.NextAsync(); Assert.Equal("ordinary current", ordinaryCall.Request.Messages[^1].Content);
        Complete(summary, Summary);
        var next = await f.Transport.NextAsync();
        Assert.True(IsSummary(next) || next.Request.Messages[^1].Content == "compressed current");
        Assert.DoesNotContain(next.Request.Messages, m => m.Content.Contains("Generate a concise conversation title", StringComparison.Ordinal));
    }

}
