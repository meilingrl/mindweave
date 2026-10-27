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
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0, peak = 0;
        async Task Physical() { int n = Interlocked.Increment(ref count); peak = Math.Max(peak, n); if(n == 2) entered.TrySetResult(); await release.Task; Interlocked.Decrement(ref count); }
        var first = a.RunPhaseAsync(Physical); var second = b.RunPhaseAsync(Physical);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var title1 = scheduler.RegisterLogicalWork(GenerationPriority.Background);
        using var title2 = scheduler.RegisterLogicalWork(GenerationPriority.Background);
        var order = new List<string>();
        var t1 = title1.RunPhaseAsync(() => { lock(order) order.Add("title1"); return Task.CompletedTask; });
        var t2 = title2.RunPhaseAsync(() => { lock(order) order.Add("title2"); return Task.CompletedTask; });
        using var reply = scheduler.RegisterLogicalWork(GenerationPriority.Reply);
        var r = reply.RunPhaseAsync(() => { lock(order) order.Add("reply"); return Task.CompletedTask; });
        release.SetResult(); await Task.WhenAll(first, second); a.Dispose(); b.Dispose();
        await r.WaitAsync(TimeSpan.FromSeconds(5)); reply.Dispose();
        await Task.WhenAll(t1,t2).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("reply", order[0]); Assert.Equal(2, peak);
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
