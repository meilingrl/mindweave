using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Reading;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The storage-health contract (architecture review F1, R2, R3): recovery is never declared over a newer failure or over
/// kept content still unsaved, accepted work waits while blocked, and stopping waits for a probe in progress.
/// </summary>
public class StorageRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StorageWriteException Full() => new(StorageFailureKind.Full, "simulated full disk", null);

    [Fact]
    public async Task A_failure_reported_while_a_probe_runs_keeps_storage_blocked()
    {
        var probe = new ScriptedProbe();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Behavior = call => call == 1 ? gate.Task : Task.FromException(Full());
        await using var health = Health(probe);

        health.Report(Full());
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        health.Report(Full());
        gate.SetResult();

        // The first probe succeeded, but a failure came after its pass began: that pass must not clear. Later probes fail.
        await ChatHarness.UntilAsync(() => probe.Calls >= 3);
        Assert.True(health.IsBlocked);

        probe.Behavior = _ => Task.CompletedTask;
        await ChatHarness.UntilAsync(() => !health.IsBlocked);
    }

    [Fact]
    public async Task Content_kept_during_a_pass_keeps_storage_blocked_until_it_is_saved()
    {
        var probe = new ScriptedProbe();
        await using var health = Health(probe);
        StorageObligation? kept = null;
        health.RegisterRetry(_ =>
        {
            // A write failing inside the pass: kept, with its obligation open.
            kept ??= health.Retain(Full());
            return Task.CompletedTask;
        });

        health.Report(Full());
        await ChatHarness.UntilAsync(() => probe.Calls >= 3);
        Assert.True(health.IsBlocked);
        Assert.Equal(1, health.Outstanding);

        kept!.Resolve();
        kept.Resolve();
        await ChatHarness.UntilAsync(() => !health.IsBlocked);
        Assert.Equal(0, health.Outstanding);
    }

    [Fact]
    public async Task Stopping_waits_for_a_probe_in_progress_and_nothing_probes_afterwards()
    {
        var probe = new ScriptedProbe();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Behavior = _ => gate.Task;
        await using var health = Health(probe);

        health.Report(Full());
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var stop = health.StopAsync();
        await Task.Delay(100, Ct);
        Assert.False(stop.IsCompleted);

        gate.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var calls = probe.Calls;
        health.Report(Full());
        await Task.Delay(100, Ct);
        Assert.Equal(calls, probe.Calls);
        Assert.True(health.IsBlocked);
    }

    [Fact]
    public async Task Retries_are_registered_before_start_only()
    {
        await using var health = Health(new ScriptedProbe());
        health.RegisterRetry(_ => Task.CompletedTask);
        health.Start();
        Assert.Throws<InvalidOperationException>(() => health.RegisterRetry(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task Reports_kept_writes_and_retries_racing_each_other_finish_without_deadlock_and_recover()
    {
        var probe = new ScriptedProbe();
        await using var health = Health(probe);
        var ownerLock = new Lock();
        health.RegisterRetry(_ =>
        {
            // An owner reporting from inside its own lock, as the reading and draft owners do.
            lock (ownerLock)
            {
                health.Report(Full());
            }

            return Task.CompletedTask;
        });

        var work = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            for (var n = 0; n < 200; n++)
            {
                lock (ownerLock)
                {
                    health.Retain(Full()).Resolve();
                }

                if (n % 50 == i)
                {
                    await health.RetryKeptAsync();
                }
            }
        }, Ct));
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, health.Outstanding);
    }

    [Fact]
    public async Task An_accepted_answer_waits_while_storage_is_blocked_and_is_sent_once_after_recovery()
    {
        await using var h = await ChatHarness.StartAsync();
        var first = h.Client.Hold("一");
        var second = h.Client.Hold("二");
        var a = await h.Conversations.StartAsync(h.Workspace, "问A", Ct);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var b = await h.Conversations.StartAsync(h.Workspace, "问B", Ct);
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var c = await h.Conversations.StartAsync(h.Workspace, "问C", Ct);
        Assert.True(c.Accepted);

        // Storage fails with content kept; a slot frees, but the queued answer is not sent while blocked (D2).
        var kept = h.Health.Retain(Full());
        first.Finish();
        await h.WaitForAnswerAsync(a.Answer!.Value);
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(h.Client.Requests, r => r.Turns[^1].Content == "问C");
        Assert.Equal(MessageState.Pending, (await h.GetMessageAsync(c.Answer!.Value))!.State);

        kept.Resolve();
        var answered = await h.WaitForAnswerAsync(c.Answer!.Value);
        Assert.Equal(MessageState.Complete, answered.State);
        Assert.Single(h.Client.Requests, r => r.Turns[^1].Content == "问C");
        second.Finish();
        await h.WaitForAnswerAsync(b.Answer!.Value);
    }

    [Fact]
    public async Task A_message_draft_storage_refuses_pauses_requests_until_it_is_saved_but_a_reading_position_does_not()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        var failing = new RefusingReadingStore(h.ReadingStore) { RefusePositions = true };
        using var reading = new ReadingStateService(failing, new SystemClock(), NullLogger<ReadingStateService>.Instance, TimeSpan.FromHours(1), h.Health);

        reading.SetPosition(sent.Conversation, ViewerSlotKey.Primary, new ReadingPosition(answer.Id, 0, null, 1));
        Assert.True(await reading.FlushAsync());
        Assert.False(h.Health.IsBlocked);

        failing.RefusePositions = false;
        failing.RefuseDrafts = true;
        reading.SetDraft(sent.Conversation, ViewerSlotKey.Primary, "还没发出去的问题");
        Assert.False(await reading.FlushAsync());
        Assert.True(h.Health.IsBlocked);
        Assert.Equal(SendRejection.StorageUnavailable, (await h.Conversations.AskAsync(sent.Conversation, "再问", Ct)).Rejection);

        // Storage takes writes again: the recovery loop saves the kept draft itself, then lifts the pause.
        failing.RefuseDrafts = false;
        await ChatHarness.UntilAsync(() => !h.Health.IsBlocked);
        Assert.Equal("还没发出去的问题", await h.ReadingStore.GetDraftAsync(sent.Conversation, ViewerSlotKey.Primary, Ct));
    }

    [Fact]
    public async Task A_rename_while_storage_is_blocked_is_refused_and_changes_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.SettleAsync(sent.Conversation);
        var before = (await h.Conversations.LoadAsync(sent.Conversation, Ct))!.Conversation.Title;
        var kept = h.Health.Retain(Full());

        await Assert.ThrowsAsync<StorageWriteException>(() => h.Conversations.RenameAsync(sent.Conversation, "新名字", Ct));
        Assert.Equal(before, (await h.Conversations.LoadAsync(sent.Conversation, Ct))!.Conversation.Title);

        kept.Resolve();
        await ChatHarness.UntilAsync(() => !h.Health.IsBlocked);
        await h.Conversations.RenameAsync(sent.Conversation, "新名字", Ct);
        Assert.Equal("新名字", (await h.Conversations.LoadAsync(sent.Conversation, Ct))!.Conversation.Title);
    }

    private static StorageHealth Health(IStorageProbe probe) =>
        new(probe, new StorageHealthOptions { RetryInterval = TimeSpan.FromMilliseconds(10) }, NullLogger<StorageHealth>.Instance);

    /// <summary>A probe whose n-th call (from 1) does what <see cref="Behavior"/> says; succeeds by default.</summary>
    private sealed class ScriptedProbe : IStorageProbe
    {
        private int _calls;

        public Func<int, Task> Behavior { get; set; } = _ => Task.CompletedTask;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public Task ProbeWriteAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            return Behavior(call);
        }
    }

    /// <summary>The real reading store, whose writes storage refuses on demand.</summary>
    private sealed class RefusingReadingStore(IReadingStateStore inner) : IReadingStateStore
    {
        public volatile bool RefuseDrafts;

        public volatile bool RefusePositions;

        public Task<ReadingPosition?> GetPositionAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken) =>
            inner.GetPositionAsync(conversation, slot, cancellationToken);

        public Task SavePositionAsync(ConversationId conversation, ViewerSlotKey slot, ReadingPosition position, DateTimeOffset at, CancellationToken cancellationToken) =>
            RefusePositions ? throw Full() : inner.SavePositionAsync(conversation, slot, position, at, cancellationToken);

        public Task<string?> GetDraftAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken) =>
            inner.GetDraftAsync(conversation, slot, cancellationToken);

        public Task SaveDraftAsync(ConversationId conversation, ViewerSlotKey slot, string text, DateTimeOffset at, CancellationToken cancellationToken) =>
            RefuseDrafts ? throw Full() : inner.SaveDraftAsync(conversation, slot, text, at, cancellationToken);
    }
}
