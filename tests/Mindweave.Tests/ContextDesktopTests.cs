using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Xunit;
using F = Mindweave.Tests.GenerationCoordinatorTests.Fixture;

namespace Mindweave.Tests;

public sealed class ContextDesktopTests
{
    private const string Summary = """{"Background":"SYNTHETIC_PRIVATE_SUMMARY","Requirements":"","Corrections":"","OpenQuestions":"","References":""}""";
    private static async Task<Guid> Seed(F f)
    {
        var c = await f.ConversationAsync();
        for (var i = 0; i < 5; i++) await BranchStorageTests.Answer(f.Store, c.Id, "question" + i, new string((char)('a' + i), 1500));
        await f.Chat.RenameConversationAsync(c.Id, "manual");
        await f.Chat.SaveConfigurationAsync(f.Configuration with { ContextTokenBudget = 4500, MaxOutputTokens = 128 });
        return c.Id;
    }
    private static ChatView Mount(ChatViewModel vm, out Window window)
    {
        var view = new ChatView { DataContext = vm };
        window = new Window { Content = view, Width = 1100, Height = 760 }; window.Show();
        Dispatcher.UIThread.RunJobs(); return view;
    }
    private static IEnumerable<string> Texts(ChatView view) => view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");
    private static async Task Wait(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, deadline.Token); }
    }
    private static void Complete(GenerationCoordinatorTests.Call call, string text) { call.Emit(text, "stop"); call.Finish(); }
    private static async Task CompleteCompression(F f)
    {
        while (true)
        {
            var call = await f.Transport.NextAsync();
            if (!call.Request.Messages[0].Content.Contains("Summarize the supplied original", StringComparison.Ordinal))
            { Complete(call, "visible answer"); return; }
            Complete(call, Summary);
        }
    }

    [AvaloniaFact]
    public async Task Compressing_chat_shows_progress_and_stop_without_summary_body_then_reopens_notice_without_live_snapshot()
    {
        await using var f = await F.OpenAsync(); var id = await Seed(f);
        var vm = new ChatViewModel(id, f.Chat, f.Coordinator, () => false, () => { }); await vm.InitializeAsync();
        var view = Mount(vm, out var window);
        try
        {
            vm.InputText = "current"; await vm.SendCommand.ExecuteAsync(null);
            Assert.Contains("准备上下文", vm.Messages[^1].StatusLabel);
            Assert.True(view.FindControl<Button>("StopButton")!.IsEnabled);
            Assert.Empty(vm.Messages[^1].Body);
            await CompleteCompression(f);
            await f.WaitAsync(() => f.Coordinator.Snapshots.Single().Status == GenerationStatus.Completed && !f.Coordinator.Snapshots.Single().HasUnsavedChanges);
            vm.Project(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("visible answer", vm.Messages[^1].Body);
            Assert.Contains(Texts(view), t => t.Contains("较早对话已压缩，原记录保留", StringComparison.Ordinal));
            await using var restarted = new GenerationCoordinator(f.Store, new GenerationCoordinatorTests.ControlledTransport(), f.Vault);
            await restarted.InitializeAsync(); Assert.Empty(restarted.Snapshots);
            var reopened = new ChatViewModel(id, f.Store, restarted, () => false, () => { }); await reopened.InitializeAsync();
            view.DataContext = reopened; Dispatcher.UIThread.RunJobs();
            Assert.Equal("visible answer", reopened.Messages[^1].Body);
            Assert.Contains(Texts(view), t => t.Contains("较早对话已压缩，原记录保留", StringComparison.Ordinal));
            Assert.DoesNotContain(Texts(view), t => t.Contains("SYNTHETIC_PRIVATE_SUMMARY", StringComparison.Ordinal));
        }
        finally { window.Close(); vm.Detach(); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Failed_or_stopped_compression_is_terminal_preserves_question_and_draft_and_allows_explicit_retry(bool stop)
    {
        await using var f = await F.OpenAsync(); var id = await Seed(f);
        var vm = new ChatViewModel(id, f.Chat, f.Coordinator, () => false, () => { }); await vm.InitializeAsync();
        var view = Mount(vm, out var window);
        try
        {
            vm.InputText = "current"; await vm.SendCommand.ExecuteAsync(null); var call = await f.Transport.NextAsync();
            vm.InputText = "next draft"; await vm.FlushDraftAsync();
            if (stop) await vm.StopCommand.ExecuteAsync(null);
            else { Complete(call, "{}"); await f.WaitAsync(() => f.Coordinator.Snapshots.Single().Status == GenerationStatus.Failed && !f.Coordinator.Snapshots.Single().HasUnsavedChanges); }
            vm.Project(); Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("准备上下文", vm.Messages[^1].StatusLabel);
            Assert.Contains(Texts(view), t => t.Contains("重试", StringComparison.Ordinal) && t.Contains("预算", StringComparison.Ordinal));
            Assert.True(vm.CanRetry); Assert.Empty(vm.Messages[^1].Body);
            Assert.Equal("current", vm.Messages[^2].Body); Assert.Equal("next draft", vm.InputText);
            Assert.Equal("next draft", (await f.Chat.LoadDraftAsync(id)).Text);
            await vm.RetryCommand.ExecuteAsync(null); await CompleteCompression(f);
            await f.WaitAsync(() => f.Coordinator.Snapshots.Last().Status == GenerationStatus.Completed && !f.Coordinator.Snapshots.Last().HasUnsavedChanges);
            vm.Project(); Assert.Equal("visible answer", vm.Messages[^1].Body); Assert.Equal("next draft", vm.InputText);
        }
        finally { window.Close(); vm.Detach(); }
    }

    [AvaloniaFact]
    public async Task Progress_only_projection_does_not_replace_body_and_terminal_budget_failure_is_not_used_notice()
    {
        await using var f = await F.OpenAsync(); var id = await Seed(f);
        var vm = new ChatViewModel(id, f.Store, f.Coordinator, () => false, () => { }); await vm.InitializeAsync();
        vm.InputText = "current"; await vm.SendCommand.ExecuteAsync(null);
        var snapshot = f.Coordinator.Snapshots.Single(); var message = vm.Messages[^1];
        var bodyNotifications = 0; message.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(message.Body)) bodyNotifications++; };
        message.Project(snapshot with { Phase = GenerationPhase.Compressing, CompletedContextChunks = 1, TotalContextChunks = 3 });
        Assert.Contains("1/3", message.StatusLabel); Assert.Equal(snapshot.Revision, message.Revision); Assert.Equal(0, bodyNotifications);
        var view = Mount(vm, out var window);
        try
        {
            message.Project(snapshot with { Phase = GenerationPhase.Compressing, CompletedContextChunks = 3, TotalContextChunks = 3, Status = GenerationStatus.Failed, Error = "ContextBudgetExceeded" });
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("准备上下文", message.StatusLabel);
            Assert.Contains(Texts(view), t => t.Contains("仍超出预算", StringComparison.Ordinal));
            Assert.DoesNotContain(Texts(view), t => t.Contains("较早对话已压缩，原记录保留", StringComparison.Ordinal));
        }
        finally { window.Close(); vm.Detach(); }
    }
    [AvaloniaFact]
    public async Task Optional_usage_unavailable_or_incomplete_keeps_history_readable_without_claiming_summary_use()
    {
        await using var f = await F.OpenAsync(); var id = await Seed(f);
        var task = await f.Coordinator.SendAsync(id, "current"); await CompleteCompression(f);
        await f.WaitAsync(() => f.Snapshot(task).Status == GenerationStatus.Completed && !f.Snapshot(task).HasUnsavedChanges);
        await using var restarted = new GenerationCoordinator(f.Store, new GenerationCoordinatorTests.ControlledTransport(), f.Vault);
        await restarted.InitializeAsync(); Assert.Empty(restarted.Snapshots);
        var proxy = new ChatDesktopTests.ControlledChatStore(f.Store) { FailContextUsage = true };
        var vm = new ChatViewModel(id, proxy, restarted, () => false, () => throw new Exception("optional read must not pause saves"));
        await vm.InitializeAsync(); var view = Mount(vm, out var window);
        try
        {
            Assert.Equal("visible answer", vm.Messages[^1].Body);
            Assert.Contains(Texts(view), t => t.Contains("使用信息暂不可用", StringComparison.Ordinal));
            Assert.DoesNotContain(Texts(view), t => t.Contains("较早对话已压缩，原记录保留", StringComparison.Ordinal));
            proxy.FailContextUsage = false;
            f.Sql($"DELETE FROM prepared_context_use WHERE task_id='{task:D}' AND kind='Summary'");
            var corrupt = new ChatViewModel(id, f.Store, restarted, () => false, () => { }); await corrupt.InitializeAsync();
            view.DataContext = corrupt; Dispatcher.UIThread.RunJobs();
            Assert.Equal("visible answer", corrupt.Messages[^1].Body);
            Assert.Contains(Texts(view), t => t.Contains("使用信息暂不可用", StringComparison.Ordinal));
            Assert.DoesNotContain(Texts(view), t => t.Contains("较早对话已压缩，原记录保留", StringComparison.Ordinal));
        }
        finally { window.Close(); vm.Detach(); }
    }

    [AvaloniaFact]
    public async Task Usage_refresh_failure_clears_stale_success_notice_without_changing_original_body()
    {
        await using var f = await F.OpenAsync(); var id = await Seed(f);
        var task = await f.Coordinator.SendAsync(id, "current"); await CompleteCompression(f);
        await f.WaitAsync(() => f.Snapshot(task).Status == GenerationStatus.Completed && !f.Snapshot(task).HasUnsavedChanges);
        await using var restarted = new GenerationCoordinator(f.Store, new GenerationCoordinatorTests.ControlledTransport(), f.Vault);
        await restarted.InitializeAsync();
        var proxy = new ChatDesktopTests.ControlledChatStore(f.Store);
        var vm = new ChatViewModel(id, proxy, restarted, () => false, () => throw new Exception("optional metadata read must not pause saves"));
        await vm.InitializeAsync();
        var previous = vm.Messages[^1];
        Assert.Contains("较早对话已压缩", previous.ContextNotice);
        proxy.FailContextUsage = true;
        vm.InputText = "next question"; await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("visible answer", previous.Body);
        Assert.Contains("使用信息暂不可用", previous.ContextNotice);
        Assert.DoesNotContain("较早对话已压缩", previous.ContextNotice);
        Assert.Null(vm.ErrorMessage);
        vm.Detach();
    }

    [AvaloniaFact]
    public async Task Prepared_empty_failed_answer_is_not_transport_evidence_and_usage_query_is_bounded_to_requester_messages()
    {
        await using var f = await F.OpenAsync(); var id = await Seed(f);
        var config = (await f.Store.LoadActiveConfigurationAsync())!;
        var work = await f.Store.AcceptReplyAsync(id, "current", config);
        foreach (var part in work.ContextPlan!.Parts.OfType<SummaryContextPart>())
        {
            var attempt = await f.Store.StartCompressionAsync(work.Task.Id, part.Chunk.CacheKey);
            await f.Store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, Summary, GenerationStatus.Completed, "stop");
        }
        await f.Store.PrepareReplyRequestAsync(work.Task.Id);
        await f.Store.WriteGenerationAsync(work.Task.Id, 1, "", GenerationStatus.Failed, "Timeout");
        var other = await f.ConversationAsync();
        Assert.Empty(await f.Store.LoadContextUsageAsync(other.Id, [work.Task.AnswerMessageId!.Value]));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.LoadContextUsageAsync(id, Enumerable.Range(0, 257).Select(_ => Guid.NewGuid()).ToArray()));
        Assert.True(Assert.Single(await f.Store.LoadContextUsageAsync(id, [work.Task.AnswerMessageId!.Value])).SummaryCount > 0);
        var vm = new ChatViewModel(id, f.Store, f.Coordinator, () => false, () => { }); await vm.InitializeAsync();
        var view = Mount(vm, out var window);
        try
        {
            Assert.Empty(vm.Messages[^1].Body); Assert.Contains("Timeout", vm.Messages[^1].StatusLabel);
            Assert.DoesNotContain(Texts(view), t => t.Contains("较早对话已压缩，原记录保留", StringComparison.Ordinal));
            Assert.Equal(0, f.Transport.StartCount);
        }
        finally { window.Close(); vm.Detach(); }
    }

    [AvaloniaFact]
    public async Task Actual_original_markers_flow_to_physical_request_cache_ids_and_explicit_retry_reuses_same_cache()
    {
        await using var f = await F.OpenAsync(); var c = await f.ConversationAsync();
        string[] markers = ["[MW_SOURCE:orchid]", "[MW_CORRECTION:blue]", "[MW_QUESTION:when]", "[MW_REFERENCE:it=orchid]"];
        await BranchStorageTests.Answer(f.Store, c.Id, string.Join(" ", markers) + new string('a', 2500), new string('r', 160));
        await BranchStorageTests.Answer(f.Store, c.Id, new string('b', 2500), new string('s', 160));
        await f.Store.RenameConversationAsync(c.Id, "manual");
        await f.Store.SaveConfigurationAsync(f.Configuration with { ContextTokenBudget = 7000, MaxOutputTokens = 128 });
        var vm = new ChatViewModel(c.Id, f.Store, f.Coordinator, () => false, () => { }); await vm.InitializeAsync();
        vm.InputText = "protocol check " + new string('c', 2500); await vm.SendCommand.ExecuteAsync(null);
        var firstId = f.Coordinator.Snapshots.Single().TaskId;
        var summaries = 0;
        while (true)
        {
            var call = await f.Transport.NextAsync();
            if (!call.Request.Messages[0].Content.StartsWith("Summarize the supplied original", StringComparison.Ordinal))
            {
                var prepared = (await f.Store.LoadPreparedReplyRequestAsync(firstId))!;
                Assert.Equal(prepared.Request.Messages, call.Request.Messages);
                var physical = string.Join("\n", call.Request.Messages.Select(m => m.Content));
                Assert.All(markers, marker => Assert.Contains(marker, physical));
                foreach (var use in prepared.Uses.Where(u => u.Kind == ContextUseKind.Summary))
                    Assert.Equal(1L, ContextStorageTests.Sql(f.Paths, $"SELECT count(*) FROM context_summary_cache WHERE id='{use.SummaryId:D}'"));
                Complete(call, "visible answer"); break;
            }
            summaries++;
            using var document = System.Text.Json.JsonDocument.Parse(call.Request.Messages[^1].Content);
            var original = string.Join("\n", document.RootElement.GetProperty("OriginalSlices").EnumerateArray().Select(e => e.GetProperty("Body").GetString()));
            // Explicit synthetic protocol echo, derived only from supplied original bodies.
            var present = markers.Where(original.Contains).ToArray();
            Complete(call, System.Text.Json.JsonSerializer.Serialize(new ContextSummaryContent("SYNTHETIC " + string.Join(" ", present), "", "", "", "")));
        }
        await f.WaitAsync(() => f.Snapshot(firstId).Status == GenerationStatus.Completed && !f.Snapshot(firstId).HasUnsavedChanges);
        Assert.True(summaries > 0);
        var before = f.Transport.StartCount;
        await vm.RetryCommand.ExecuteAsync(null); var retry = await f.Transport.NextAsync();
        Assert.False(retry.Request.Messages[0].Content.StartsWith("Summarize the supplied original", StringComparison.Ordinal));
        var secondId = f.Coordinator.Snapshots.Last().TaskId;
        var firstUses = (await f.Store.LoadPreparedReplyRequestAsync(firstId))!.Uses.Where(u => u.SummaryId is not null).Select(u => u.SummaryId);
        var secondUses = (await f.Store.LoadPreparedReplyRequestAsync(secondId))!.Uses.Where(u => u.SummaryId is not null).Select(u => u.SummaryId);
        Assert.Equal(firstUses, secondUses); Assert.Equal(before + 1, f.Transport.StartCount);
        Complete(retry, "retried answer");
        await f.WaitAsync(() => f.Snapshot(secondId).Status == GenerationStatus.Completed && !f.Snapshot(secondId).HasUnsavedChanges);
        vm.Project(); Assert.Equal("retried answer", vm.Messages[^1].Body);
        // A new turn moves another original exchange into the compressed range. An
        // unchanged retry above reused exact cache IDs; this larger range must not.
        vm.InputText = "new range " + new string('d', 2500); await vm.SendCommand.ExecuteAsync(null);
        var thirdId = f.Coordinator.Snapshots.Last().TaskId;
        var changed = await f.Transport.NextAsync();
        Assert.StartsWith("Summarize the supplied original", changed.Request.Messages[0].Content);
        var oldKeys = (await f.Store.LoadContextPlanAsync(firstId))!.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk.CacheKey);
        var newKeys = (await f.Store.LoadContextPlanAsync(thirdId))!.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk.CacheKey);
        Assert.NotEmpty(newKeys.Except(oldKeys));
        Complete(changed, Summary); await CompleteCompression(f);
        await f.WaitAsync(() => f.Snapshot(thirdId).Status == GenerationStatus.Completed && !f.Snapshot(thirdId).HasUnsavedChanges);
        var newIds = (await f.Store.LoadPreparedReplyRequestAsync(thirdId))!.Uses.Where(u => u.SummaryId is not null).Select(u => u.SummaryId);
        Assert.NotEmpty(newIds.Except(firstUses));
        Assert.Equal(8, (await f.Store.ReadMessagesAsync(c.Id)).Count(m => m.Role == MessageRole.User || m.SupersedesMessageId is null));
        vm.Detach();
    }

}
