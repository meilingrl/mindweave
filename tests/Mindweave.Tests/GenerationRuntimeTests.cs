using Xunit;

using Mindweave.Application.Generation;
namespace Mindweave.Tests;

public sealed class GenerationRuntimeTests
{
    [Fact]
    public async Task Failure_sources_clear_only_their_captured_epoch()
    {
        await using var runtime = new AppGenerationRuntime();
        var chat = runtime.CreateFailureSource(); var summary = runtime.CreateFailureSource();
        long old = chat.Report(); summary.Report(); chat.Report();
        Assert.False(chat.TryClear(old));
        Assert.True(chat.TryClear(chat.Epoch)); Assert.True(runtime.HasPersistenceFailure);
        summary.TryClear(summary.Epoch); Assert.False(runtime.HasPersistenceFailure);
    }

    [Fact]
    public async Task Shutdown_waits_pending_acceptance_and_post_physical_tail()
    {
        await using var runtime = new AppGenerationRuntime();
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var participant = new Participant { Shutdown = () => tail.Task };
        using var registration = runtime.RegisterParticipant(participant);
        using var accepted = await runtime.EnterAcceptanceAsync();
        Assert.True(runtime.HasActiveOrPendingWork);
        var close = runtime.ShutdownAsync(); Assert.False(close.IsCompleted);
        accepted.Dispose(); await Task.Yield(); Assert.False(close.IsCompleted);
        tail.SetResult(); await close.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { using var lease = await runtime.EnterAcceptanceAsync(); });
        runtime.ResumeAccepting(); using var again = await runtime.EnterAcceptanceAsync();
    }

    [Fact]
    public async Task Independent_chat_participants_share_two_slots_and_failure_gate()
    {
        await using var runtime = new AppGenerationRuntime();
        await using var first = await GenerationCoordinatorTests.Fixture.OpenAsync(runtime: runtime);
        await using var second = await GenerationCoordinatorTests.Fixture.OpenAsync(runtime: runtime);
        var a = await first.ConversationAsync(); var b = await first.ConversationAsync(); var c = await second.ConversationAsync();
        await first.Coordinator.SendAsync(a.Id, "a"); var one = await first.Transport.NextAsync();
        await first.Coordinator.SendAsync(b.Id, "b"); await first.Transport.NextAsync();
        await second.Coordinator.SendAsync(c.Id, "c");
        Assert.Equal(2, runtime.Scheduler.ActivePhaseCount);
        first.Coordinator.ReportPersistenceFailure();
        Assert.True(second.Coordinator.HasPersistenceFailure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.Coordinator.SendAsync((Guid.NewGuid()), "blocked"));
        await first.Coordinator.RetryPersistenceAsync(() => Task.CompletedTask);
        one.Fail("Timeout");
        Assert.Equal("c", (await second.Transport.NextAsync()).Request.Messages.Last().Content);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Desktop_close_observes_other_participant_and_drains_its_tail()
    {
        await using var f = await GenerationCoordinatorTests.Fixture.OpenAsync();
        Mindweave.Desktop.Services.DesktopChatSession? session = null;
        var root = new Mindweave.Desktop.ViewModels.RootViewModel(
            () => Task.FromResult<Mindweave.Application.Storage.IWorkspaceStore>(f.Store),
            new Mindweave.Desktop.Services.DesktopActions(f.Paths.DataDirectory, () => { }), f.Paths.DataDirectory,
            store => session = new((Mindweave.Application.Chat.IChatStore)store, f.Transport, f.Vault));
        await root.InitializeAsync();
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = session!.Runtime.RegisterParticipant(new Participant { Active = true, Shutdown = () => tail.Task });
        await root.CloseAsync(); Assert.True(root.IsCloseConfirmationVisible);
        var close = root.ConfirmCloseCommand.ExecuteAsync(null);
        Assert.False(root.IsClosed); Assert.False(close.IsCompleted);
        tail.SetResult(); await close;
        Assert.True(root.IsClosed);
    }

    [Fact]
    public async Task Healthy_participant_cannot_clear_another_source_and_new_external_epoch_survives_repair()
    {
        await using var f = await GenerationCoordinatorTests.Fixture.OpenAsync();
        var other = f.Coordinator.Runtime.CreateFailureSource();
        other.Report();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.RetryPersistenceAsync());
        Assert.True(other.HasFailure); other.TryClear(other.Epoch);
        f.Coordinator.ReportPersistenceFailure();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.RetryPersistenceAsync(() =>
        {
            f.Coordinator.ReportPersistenceFailure(); return Task.CompletedTask;
        }));
        Assert.True(f.Coordinator.HasPersistenceFailure);
        await f.Coordinator.RetryPersistenceAsync(() => Task.CompletedTask);
        Assert.False(f.Coordinator.HasPersistenceFailure);
    }

    [Fact]
    public async Task Failed_shutdown_retains_participants_and_retry_drains_tail()
    {
        await using var runtime = new AppGenerationRuntime();
        bool fail = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var participant = runtime.RegisterParticipant(new Participant
        {
            Shutdown = () => { if (fail) throw new InvalidOperationException("fixture"); entered.TrySetResult(); return tail.Task; }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ShutdownAsync());
        runtime.ResumeAccepting(); using (await runtime.EnterAcceptanceAsync()) { }
        fail = false; var retry = runtime.ShutdownAsync(); await entered.Task;
        Assert.False(retry.IsCompleted); tail.SetResult(); await retry;
    }

    [Fact]
    public async Task Chat_titles_waiting_globally_cannot_hide_later_reply_behind_a_local_cap()
    {
        await using var runtime = new AppGenerationRuntime();
        await using var f = await GenerationCoordinatorTests.Fixture.OpenAsync(runtime: runtime);
        var a = await f.ConversationAsync(); var b = await f.ConversationAsync(); var c = await f.ConversationAsync();
        await f.Coordinator.SendAsync(a.Id, "a"); var one = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(b.Id, "b"); var two = await f.Transport.NextAsync();
        using var summaryA = runtime.Scheduler.RegisterLogicalWork(GenerationPriority.Summary);
        using var summaryB = runtime.Scheduler.RegisterLogicalWork(GenerationPriority.Summary);
        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sa = summaryA.RunPhaseAsync(async () => { enteredA.SetResult(); await releaseA.Task; });
        var sb = summaryB.RunPhaseAsync(async () => { enteredB.SetResult(); await releaseB.Task; });
        try
        {
            one.Emit("answer a", "stop"); one.Finish(); two.Emit("answer b", "stop"); two.Finish();
            await Task.WhenAll(enteredA.Task, enteredB.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await f.WaitAsync(() => f.Coordinator.Snapshots.Count(s => s.Kind == Mindweave.Core.Chat.GenerationKind.Title) == 2);
            await f.Coordinator.SendAsync(c.Id, "urgent reply");
            summaryA.Dispose(); releaseA.SetResult(); await sa;
            var next = await f.Transport.NextAsync();
            Assert.Equal("urgent reply", next.Request.Messages.Last().Content);
        }
        finally { summaryA.Dispose(); summaryB.Dispose(); releaseA.TrySetResult(); releaseB.TrySetResult(); await Task.WhenAll(sa, sb); }
    }

    [Fact]
    public async Task Durable_readiness_stays_queued_until_shared_physical_grant()
    {
        await using var runtime = new AppGenerationRuntime();
        await using var f = await GenerationCoordinatorTests.Fixture.OpenAsync(runtime: runtime);
        using var blockerA = runtime.Scheduler.RegisterLogicalWork(GenerationPriority.Background);
        using var blockerB = runtime.Scheduler.RegisterLogicalWork(GenerationPriority.Background);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pa = blockerA.RunPhaseAsync(() => release.Task); var pb = blockerB.RunPhaseAsync(() => release.Task);
        var conversation = await f.ConversationAsync();
        try
        {
            var id = await f.Coordinator.SendAsync(conversation.Id, "queued readiness");
            await f.WaitAsync(() => f.Snapshot(id).Revision > 0 && !f.Snapshot(id).HasUnsavedChanges);
            Assert.Equal(Mindweave.Core.Chat.GenerationStatus.Queued, f.Snapshot(id).Status);
            Assert.Equal(Mindweave.Core.Chat.GenerationStatus.Queued, (await f.Chat.LoadGenerationAsync(id))!.Status);
            Assert.Equal(0, f.Transport.StartCount);
            blockerA.Dispose(); blockerB.Dispose(); release.SetResult(); await Task.WhenAll(pa, pb);
            await f.Transport.NextAsync();
            Assert.Equal(Mindweave.Core.Chat.GenerationStatus.Running, f.Snapshot(id).Status);
        }
        finally { release.TrySetResult(); await Task.WhenAll(pa, pb); }
    }

    [Fact]
    public async Task Shutdown_notifies_every_participant_even_if_one_throws_synchronously()
    {
        await using var runtime = new AppGenerationRuntime();
        bool fail = true, secondStopped = false;
        using var first = runtime.RegisterParticipant(new Participant { Shutdown = () => fail ? throw new InvalidOperationException("fixture") : Task.CompletedTask });
        using var second = runtime.RegisterParticipant(new Participant { Shutdown = () => { secondStopped = true; return Task.CompletedTask; } });
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ShutdownAsync());
            Assert.True(secondStopped);
        }
        finally { fail = false; }
    }

    [Fact]
    public async Task Return_cannot_reopen_admission_while_shutdown_is_still_draining()
    {
        await using var runtime = new AppGenerationRuntime();
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var participant = runtime.RegisterParticipant(new Participant { Shutdown = () => tail.Task });
        var closing = runtime.ShutdownAsync();
        try { Assert.Throws<InvalidOperationException>(() => runtime.ResumeAccepting()); }
        finally { tail.SetResult(); await closing; }
        runtime.ResumeAccepting(); using var accepted = await runtime.EnterAcceptanceAsync();
    }

    [Fact]
    public async Task Direct_runtime_repair_resumes_chat_title_intents_after_another_source_failed()
    {
        await using var runtime = new AppGenerationRuntime();
        await using var f = await GenerationCoordinatorTests.Fixture.OpenAsync(runtime: runtime);
        var conversation = await f.ConversationAsync();
        await f.Coordinator.SendAsync(conversation.Id, "q"); var reply = await f.Transport.NextAsync();
        var other = runtime.CreateFailureSource(); var epoch = other.Report();
        reply.Emit("complete answer", "stop"); reply.Finish();
        await f.WaitAsync(() => !f.Coordinator.HasActiveOrPendingWork && f.Coordinator.Snapshots.All(s => !s.HasUnsavedChanges));
        await runtime.RetryPersistenceAsync(() => { other.TryClear(epoch); return Task.CompletedTask; });
        var title = await f.Transport.NextAsync();
        Assert.Equal("system", title.Request.Messages[0].Role);
        Assert.Equal("complete answer", title.Request.Messages.Last().Content);
    }

    private sealed class Participant : IGenerationRuntimeParticipant
    {
        public Func<Task> Shutdown = () => Task.CompletedTask;
        public bool Active;
        public bool HasActiveOrPendingWork => Active;
        public Task ShutdownAsync() => Shutdown();
        public Task RepairPersistenceAsync() => Task.CompletedTask;
        public void ResumeAccepting() { }
    }
}
