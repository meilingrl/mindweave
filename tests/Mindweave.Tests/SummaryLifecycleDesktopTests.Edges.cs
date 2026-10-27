using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Chat;
using Mindweave.Application.Navigation;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryLifecycleDesktopTests
{
    [AvaloniaFact]
    public async Task Running_first_summary_stop_after_workspace_roundtrip_leaves_later_reply_running()
    {
        await using var f = await Fixture.Open();
        var view = await f.OpenChat(); await Click(view, "GenerateSummaryButton");
        var summary = await f.Transport.NextAsync(); summary.Emit("partial JSON");
        await Wait(() => f.Session.Summaries.Snapshots.Single().Status == GenerationStatus.Running);
        var replyId = await f.Session.Coordinator.SendAsync(f.Conversation, "later question");
        var reply = await f.Transport.NextAsync();
        await f.Main.SwitchWorkspaceAsync(f.OtherWorkspace); await f.Main.SwitchWorkspaceAsync(f.Workspace);
        view = await f.OpenChat(); Assert.False(summary.Cancellation.IsCancellationRequested);
        await Click(view, "StopSummaryButton");
        Assert.True(summary.Cancellation.IsCancellationRequested); Assert.False(reply.Cancellation.IsCancellationRequested);
        Assert.Equal(GenerationStatus.Running, f.Session.Coordinator.Snapshots.Single(s => s.TaskId == replyId).Status);
        Assert.Contains("已停止", view.FindControl<TextBlock>("SummaryStatusNotice")!.Text!);
        Assert.False(view.FindControl<Button>("GenerateSummaryButton")!.IsEnabled); // Reply admission rule still applies.
        reply.Emit("reply continues"); await Wait(() => f.Session.Coordinator.Snapshots.Single(s => s.TaskId == replyId).Body.Contains("continues"));
        Assert.Empty(f.Main.RetainedSummaries); Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version"));
    }

    [AvaloniaFact]
    public async Task Recovered_interruption_and_other_workspace_terminal_event_keep_conversation_owned_status()
    {
        await using var f = await Fixture.Open(recovered: true);
        var view = await f.OpenChat();
        Assert.Contains("已中断", view.FindControl<TextBlock>("SummaryStatusNotice")!.Text!);
        Assert.True(view.FindControl<Button>("GenerateSummaryButton")!.IsEnabled); Assert.Equal(0, f.Transport.StartCount);
        await f.Main.SwitchWorkspaceAsync(f.OtherWorkspace);
        await f.Main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.OtherConversation)); Layout();
        var otherView = f.View.GetVisualDescendants().OfType<ChatView>().Single();
        Assert.DoesNotContain("已中断", otherView.FindControl<TextBlock>("SummaryStatusNotice")!.Text ?? "");
        await Click(otherView, "GenerateSummaryButton"); var call = await f.Transport.NextAsync();
        await f.Main.SwitchWorkspaceAsync(f.Workspace); view = await f.OpenChat();
        call.Fail("SyntheticFailure");
        await Wait(() => f.Session.Summaries.Snapshots.Single(s => s.ConversationId == f.OtherConversation).Status == GenerationStatus.Failed);
        await f.Main.DrainBackgroundRefreshesAsync(); Layout();
        Assert.Contains("已中断", view.FindControl<TextBlock>("SummaryStatusNotice")!.Text!);
        Assert.DoesNotContain("连接", view.FindControl<TextBlock>("SummaryStatusNotice")!.Text!);
        await f.Main.SwitchWorkspaceAsync(f.OtherWorkspace);
        await f.Main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.OtherConversation)); Layout();
        Assert.Contains("连接", f.View.GetVisualDescendants().OfType<ChatView>().Single().FindControl<TextBlock>("SummaryStatusNotice")!.Text!);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version"));
    }

    [AvaloniaFact]
    public async Task Full_response_is_not_exposed_before_disposal_then_reports_completed_saved_status()
    {
        await using var f = await Fixture.Open(); var view = await f.OpenChat(); await Click(view, "GenerateSummaryButton");
        var call = await f.Transport.NextAsync(); call.DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        call.Emit(Payload, "stop"); call.Finish(); await call.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Layout();
        Assert.Empty(f.Main.RetainedSummaries); Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version"));
        call.DisposeRelease.SetResult(); await Wait(() => f.Session.Summaries.Snapshots.Single().Status == GenerationStatus.Completed);
        await f.Main.DrainBackgroundRefreshesAsync(); Layout();
        Assert.Contains("已完成并保存", view.FindControl<TextBlock>("SummaryStatusNotice")!.Text!);
        Assert.False(view.FindControl<Button>("StopSummaryButton")!.IsEnabled); Assert.Empty(f.Main.RetainedSummaries);
    }

    [AvaloniaFact]
    public async Task Summary_stop_remains_available_during_unrelated_persistence_failure()
    {
        await using var f = await Fixture.Open(); var view = await f.OpenChat(); await Click(view, "GenerateSummaryButton");
        var call = await f.Transport.NextAsync();
        f.Session.Coordinator.ReportPersistenceFailure(); Layout();
        Assert.True(f.Main.HasPersistenceFailure); Assert.False(view.FindControl<Button>("GenerateSummaryButton")!.IsEnabled);
        Assert.True(view.FindControl<Button>("StopSummaryButton")!.IsEnabled);
        await Click(view, "StopSummaryButton"); Assert.True(call.Cancellation.IsCancellationRequested);
        Assert.Equal(GenerationStatus.Stopped, f.Session.Summaries.Snapshots.Single().Status);
    }

    [AvaloniaFact]
    public async Task Root_close_drains_inflight_summary_stop_command_before_releasing_store()
    {
        DesktopFaultProxy? proxy = null;
        await using var f = await Fixture.Open(wrap: store =>
        {
            var wrapped = DispatchProxy.Create<SummaryDesktopTests.IDesktopStore, DesktopFaultProxy>();
            proxy = (DesktopFaultProxy)(object)wrapped; proxy.Inner = store; return wrapped;
        });
        var view = await f.OpenChat(); await Click(view, "GenerateSummaryButton"); var call = await f.Transport.NextAsync();
        proxy!.HoldTerminal = true;
        var stop = ((IAsyncRelayCommand)view.FindControl<Button>("StopSummaryButton")!.Command!).ExecuteAsync(null);
        await proxy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var closing = f.Root.ConfirmCloseCommand.ExecuteAsync(null);
        try { Assert.False(closing.IsCompleted); Assert.False(proxy.Disposed); Assert.True(call.Cancellation.IsCancellationRequested); }
        finally { proxy.Release.TrySetResult(); await stop; await closing; }
        Assert.True(f.Root.IsClosed); Assert.True(proxy.Disposed);
    }

    [AvaloniaFact]
    public async Task Metadata_message_read_returning_after_version_switch_cannot_retag_new_body_or_epoch()
    {
        await using var f = await SummaryStorageFixture.Open();
        await BranchStorageTests.Answer(f.Store, f.Conversation, "second", "second answer"); var candidate = await f.Generate("Candidate");
        var wrapped = DispatchProxy.Create<SummaryDesktopTests.IDesktopStore, DesktopFaultProxy>();
        var proxy = (DesktopFaultProxy)(object)wrapped; proxy.Inner = f.Store;
        var page = new SummaryViewModel(f.Workspace, f.Node, wrapped, new(wrapped, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(f.First);
        var view = new SummaryView { DataContext = page, AutomaticObservation = false };
        var window = new Window { Content = view, Width = 1000, Height = 800 }; window.Show(); Layout();
        Task? refresh = null;
        try
        {
            proxy.HoldMessages = true; refresh = page.RefreshMetadataAsync(); await proxy.Entered.Task;
            await page.SelectVersionAsync(candidate.VersionId); Layout(); await view.ObserveNowAsync(true);
            var epoch = page.Reading!.State.Epoch; var body = page.SavedBody;
            proxy.Release.SetResult(); await refresh; Layout();
            Assert.DoesNotContain("此后有新内容", page.Metadata); Assert.Equal(body, page.SavedBody);
            Assert.Equal(epoch, page.Reading!.State.Epoch); Assert.Equal(candidate.VersionId, page.Version!.Header.Identity.Id);
        }
        finally { proxy.Release.TrySetResult(); if (refresh is not null) await refresh; page.Detach(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Lost_completion_ack_exposes_unconfirmed_result_until_exact_repair_without_duplicate_version()
    {
        DesktopFaultProxy? proxy = null;
        await using var f = await Fixture.Open(wrap: store =>
        {
            var wrapped = DispatchProxy.Create<SummaryDesktopTests.IDesktopStore, DesktopFaultProxy>();
            proxy = (DesktopFaultProxy)(object)wrapped; proxy.Inner = store; return wrapped;
        });
        var view = await f.OpenChat(); proxy!.LoseCompletionAck = true;
        await Click(view, "GenerateSummaryButton"); var call = await f.Transport.NextAsync(); call.Emit(Payload, "stop"); call.Finish();
        await Wait(() => f.Session.Runtime.HasPersistenceFailure && f.Main.HasRetainedSummaries);
        AssertRetained(f); Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version"));
        Assert.False(view.FindControl<Button>("GenerateSummaryButton")!.IsEnabled);
        await f.Root.RetrySaveCommand.ExecuteAsync(null); Layout();
        Assert.Empty(f.Main.RetainedSummaries); Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version"));
        Assert.Equal(1, f.Transport.StartCount); Assert.Equal(0L, f.Sql("SELECT is_read FROM summary_read_state"));
    }

    [AvaloniaFact]
    public async Task Unknown_new_acceptance_after_failed_task_has_separate_repair_feedback_and_no_blind_retry()
    {
        DesktopFaultProxy? proxy = null;
        await using var f = await Fixture.Open(wrap: store =>
        {
            var wrapped = DispatchProxy.Create<SummaryDesktopTests.IDesktopStore, DesktopFaultProxy>();
            proxy = (DesktopFaultProxy)(object)wrapped; proxy.Inner = store; return wrapped;
        });
        var view = await f.OpenChat(); await Click(view, "GenerateSummaryButton"); var first = await f.Transport.NextAsync(); first.Fail("SyntheticFailure");
        await Wait(() => f.Session.Summaries.Snapshots.Single().Status == GenerationStatus.Failed && !f.Session.Summaries.Snapshots.Single().HasUnsavedChanges);
        await f.Main.DrainBackgroundRefreshesAsync(); Layout();
        proxy!.LoseAcceptanceAck = true; await Click(view, "GenerateSummaryButton");
        Assert.True(f.Main.HasPersistenceFailure); Assert.Equal(1, f.Transport.StartCount);
        var feedback = view.FindControl<TextBlock>("SummaryAcceptanceNotice"); Assert.NotNull(feedback);
        Assert.Contains("接受状态尚未确认", feedback.Text!); Assert.False(view.FindControl<Button>("GenerateSummaryButton")!.IsEnabled);
        Assert.DoesNotContain("重新生成", view.FindControl<TextBlock>("SummaryStatusNotice")!.Text!);
        await f.Root.RetrySaveCommand.ExecuteAsync(null);
        var second = await f.Transport.NextAsync(); Assert.Equal(2L, f.Sql("SELECT count(*) FROM knowledge_summary_job"));
        second.Fail("SyntheticFailure"); await Wait(() => f.Session.Summaries.Snapshots.Last().Status == GenerationStatus.Failed);
    }

    public class DesktopFaultProxy : DispatchProxy
    {
        public SqliteWorkspaceStore Inner { get; set; } = null!;
        public bool HoldTerminal, HoldMessages, Disposed, LoseCompletionAck, LoseAcceptanceAck;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAsyncDisposable.DisposeAsync)) Disposed = true;
            if (method.Name == nameof(IKnowledgeSummaryStore.CompleteKnowledgeSummaryAsync) && LoseCompletionAck)
            { LoseCompletionAck = false; return LoseCompletionAsync((SummaryGenerationCompletion)args![0]!); }
            if (method.Name == nameof(IKnowledgeSummaryStore.AcceptKnowledgeSummaryAsync) && LoseAcceptanceAck)
            { LoseAcceptanceAck = false; return LoseAcceptanceAsync((Guid)args![0]!, (Guid)args[1]!, (AiConfiguration)args[2]!); }
            if (method.Name == nameof(IKnowledgeSummaryStore.UpdateKnowledgeSummaryMetadataAsync) && HoldTerminal)
            { HoldTerminal = false; return HoldTerminalAsync((SummaryGenerationMetadata)args![0]!); }
            if (method.Name == nameof(IChatStore.ReadMessagesAsync) && HoldMessages)
            { HoldMessages = false; return HoldMessagesAsync((Guid)args![0]!); }
            return method.Invoke(Inner, args);
        }
        private async Task<SummaryOutcome<SummaryGenerationResult>> LoseCompletionAsync(SummaryGenerationCompletion completion)
        { await Inner.CompleteKnowledgeSummaryAsync(completion); throw new IOException("synthetic lost completion acknowledgement"); }
        private async Task<SummaryOutcome<KnowledgeSummaryJob>> LoseAcceptanceAsync(Guid operation, Guid conversation, AiConfiguration configuration)
        { await Inner.AcceptKnowledgeSummaryAsync(operation, conversation, configuration); throw new IOException("synthetic lost acceptance acknowledgement"); }
        private async Task<SummaryOutcome<KnowledgeSummaryJob>> HoldTerminalAsync(SummaryGenerationMetadata metadata)
        { Entered.TrySetResult(); await Release.Task; return await Inner.UpdateKnowledgeSummaryMetadataAsync(metadata); }
        private async Task<IReadOnlyList<ChatMessage>> HoldMessagesAsync(Guid conversation)
        { var messages = await Inner.ReadMessagesAsync(conversation); Entered.TrySetResult(); await Release.Task; return messages; }
    }
}
