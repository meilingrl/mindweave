using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// When storage counts as unavailable, and when it counts as recovered (requirements §9.3; architecture review F1, L3).
/// </summary>
public class StorageHealthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_terminal_write_failing_for_a_reason_other_than_storage_does_not_pause_requests_or_lose_the_answer()
    {
        FlakyStore? flaky = null;
        await using var h = await ChatHarness.StartAsync(wrapStore: s => flaky = new FlakyStore(s));
        flaky!.FailFinishes = int.MaxValue;
        flaky.FinishFailure = () => new InvalidOperationException("simulated defect in the terminal write");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await ChatHarness.UntilAsync(() => h.Conversations.Observe(sent.Answer!.Value)?.Snapshot().State == MessageState.Complete);

        Assert.False(h.Health.IsBlocked);
        Assert.Equal("答：问", h.Conversations.Observe(sent.Answer!.Value)!.Snapshot().Text);

        // The exit's save reports it unsaved while it keeps failing, and saves it once it works. The answer shows Complete
        // just before its terminal write is tried and kept, so the first save can come before there is anything to save:
        // poll until the kept write is reported (a write that was never kept would time out here).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await h.Generation.SaveUnsavedAsync())
        {
            Assert.True(DateTime.UtcNow < deadline, "the failed terminal write was never kept as unsaved");
            await Task.Delay(10, Ct);
        }

        Assert.False(h.Health.IsBlocked);
        flaky.FailFinishes = 0;
        Assert.True(await h.Generation.SaveUnsavedAsync());
        Assert.Equal("答：问", (await h.GetMessageAsync(sent.Answer!.Value))!.Text);
    }

    [Fact]
    public async Task Storage_is_not_declared_recovered_while_an_exit_save_still_has_a_kept_write_in_flight()
    {
        FlakyStore? flaky = null;
        await using var h = await ChatHarness.StartAsync(wrapStore: s => flaky = new FlakyStore(s));
        flaky!.FailFinishes = 1;
        flaky.FinishFailure = () => new StorageWriteException(StorageFailureKind.Full, "simulated full disk", null);
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await ChatHarness.UntilAsync(() => h.Health.IsBlocked);

        // The exit's save takes the kept write out of the queue and holds it while the recovery loop probes.
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flaky.BeforeFinish = async () =>
        {
            inFlight.TrySetResult();
            await release.Task;
        };
        var exitSave = h.Generation.SaveUnsavedAsync();
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Several retry intervals (50 ms in the harness) pass with the write still in flight.
        await ChatHarness.StaysAsync(() => h.Health.IsBlocked, 400);

        release.SetResult();
        Assert.True(await exitSave);
        await ChatHarness.UntilAsync(() => !h.Health.IsBlocked);
        Assert.Equal(MessageState.Complete, (await h.GetMessageAsync(sent.Answer!.Value))!.State);
    }
}
