using Xunit;

using Mindweave.Application.Generation;
namespace Mindweave.Tests;

public sealed class AiRequestSchedulerTests
{
    [Fact]
    public async Task Preparing_reply_blocks_lower_classes_but_not_ready_reply_peers()
    {
        var scheduler = new AiRequestScheduler();
        using var preparing = scheduler.RegisterLogicalWork(GenerationPriority.Reply);
        using var summary = scheduler.RegisterLogicalWork(GenerationPriority.Summary);
        using var peer = scheduler.RegisterLogicalWork(GenerationPriority.Reply);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var low = summary.RunPhaseAsync(() => { started.SetResult(); return Task.CompletedTask; });
        await peer.RunPhaseAsync(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(started.Task.IsCompleted);
        preparing.Dispose(); peer.Dispose();
        await low.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Two_queued_titles_cannot_hide_new_reply_and_capacity_is_shared()
    {
        var scheduler = new AiRequestScheduler();
        using var a = scheduler.RegisterLogicalWork(GenerationPriority.Summary);
        using var b = scheduler.RegisterLogicalWork(GenerationPriority.Summary);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        int count = 0, peak = 0;
        async Task Physical(Task release, string? label = null)
        {
            int n = Interlocked.Increment(ref count);
            int old;
            do { old = Volatile.Read(ref peak); }
            while (n > old && Interlocked.CompareExchange(ref peak, n, old) != old);
            if (n == 2) entered.TrySetResult();
            if (label is not null) lock (order) order.Add(label);
            if (label == "reply") replyStarted.TrySetResult();
            try { await release; }
            finally { Interlocked.Decrement(ref count); }
        }
        var first = a.RunPhaseAsync(() => Physical(releaseA.Task));
        var second = b.RunPhaseAsync(() => Physical(releaseB.Task));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var title1 = scheduler.RegisterLogicalWork(GenerationPriority.Background);
        using var title2 = scheduler.RegisterLogicalWork(GenerationPriority.Background);
        var t1 = title1.RunPhaseAsync(() => Physical(Task.CompletedTask, "title1"));
        var t2 = title2.RunPhaseAsync(() => Physical(Task.CompletedTask, "title2"));
        using var reply = scheduler.RegisterLogicalWork(GenerationPriority.Reply);
        var r = reply.RunPhaseAsync(() => Physical(releaseReply.Task, "reply"));
        try
        {
            // Keep B physically running: only one slot is available, so callback order
            // now witnesses grant order instead of racing two independently granted callbacks.
            a.Dispose(); releaseA.SetResult(); await first;
            await replyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (order) Assert.Equal(new[] { "reply" }, order);
            Assert.False(t1.IsCompleted); Assert.False(t2.IsCompleted);
            Assert.False(second.IsCompleted); Assert.Equal(2, scheduler.ActivePhaseCount);
            releaseReply.SetResult(); await r; reply.Dispose();
            await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "reply", "title1", "title2" }, order);
            Assert.Equal(2, Volatile.Read(ref peak));
        }
        finally
        {
            a.Dispose(); b.Dispose(); reply.Dispose();
            releaseA.TrySetResult(); releaseB.TrySetResult(); releaseReply.TrySetResult();
            try { await Task.WhenAll(first, second, r, t1, t2).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { /* Cleanup may cancel a queued phase after an assertion failure. */ }
        }
    }

    [Fact]
    public async Task Cancelled_queue_never_calls_transport_and_phase_cannot_overlap()
    {
        var scheduler = new AiRequestScheduler(); scheduler.SetPaused(true);
        using var work = scheduler.RegisterLogicalWork(GenerationPriority.Reply);
        using var cancel = new CancellationTokenSource(); int calls = 0;
        var phase = work.RunPhaseAsync(() => { calls++; return Task.CompletedTask; }, cancel.Token);
        Assert.Throws<InvalidOperationException>(() => { _ = work.RunPhaseAsync(() => Task.CompletedTask); });
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => phase);
        scheduler.SetPaused(false); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task Same_class_fifo_and_all_four_kinds_share_capacity_through_disposal()
    {
        var scheduler = new AiRequestScheduler();
        using var reply = scheduler.RegisterLogicalWork(GenerationPriority.Reply);
        using var summary = scheduler.RegisterLogicalWork(GenerationPriority.Summary);
        using var title = scheduler.RegisterLogicalWork(GenerationPriority.Background);
        using var naming = scheduler.RegisterLogicalWork(GenerationPriority.Background);
        var calls = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var replyDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var summaryDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int physical = 0, peak = 0;
        async Task Run(string name, Task tail)
        {
            int count = Interlocked.Increment(ref physical);
            int old; do { old = peak; } while (count > old && Interlocked.CompareExchange(ref peak, count, old) != old);
            calls.Writer.TryWrite(name);
            await tail; Interlocked.Decrement(ref physical);
        }
        var r = reply.RunPhaseAsync(() => Run("reply", replyDispose.Task));
        Assert.Equal("reply", await calls.Reader.ReadAsync());
        var a = summary.RunPhaseAsync(() => Run("summary", summaryDispose.Task));
        Assert.Equal("summary", await calls.Reader.ReadAsync());
        var t = title.RunPhaseAsync(() => Run("title", Task.CompletedTask));
        var n = naming.RunPhaseAsync(() => Run("naming", Task.CompletedTask));
        Assert.False(t.IsCompleted); Assert.False(n.IsCompleted);
        // Cancellation/terminal publication may end logical demand, but cannot release the live phase.
        reply.Dispose(); Assert.Equal(2, scheduler.ActivePhaseCount);
        Assert.False(calls.Reader.TryRead(out _));
        replyDispose.SetResult(); await r;
        Assert.Equal("title", await calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await t;
        Assert.Equal("naming", await calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await n; summaryDispose.SetResult(); await a;
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task Phase_completion_keeps_dependency_barrier_without_holding_capacity()
    {
        var scheduler = new AiRequestScheduler();
        using var reply = scheduler.RegisterLogicalWork(GenerationPriority.Reply);
        using var summary = scheduler.RegisterLogicalWork(GenerationPriority.Summary);
        await reply.RunPhaseAsync(() => Task.CompletedTask);
        Assert.Equal(0, scheduler.ActivePhaseCount);
        int calls = 0;
        var lower = summary.RunPhaseAsync(() => { calls++; return Task.CompletedTask; });
        Assert.False(lower.IsCompleted);
        Assert.Equal(0, scheduler.ActivePhaseCount);
        Assert.Equal(0, calls);
        reply.Dispose(); await lower.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, calls);
    }

}
