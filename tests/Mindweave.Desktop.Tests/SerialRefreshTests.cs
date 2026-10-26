using Avalonia.Headless.XUnit;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.Tests;

public class SerialRefreshTests
{
    [AvaloniaFact]
    public async Task A_request_during_a_refresh_runs_once_more_after_it_and_never_overlaps()
    {
        var gates = new List<TaskCompletionSource>();
        var running = 0;
        var maxRunning = 0;
        var refresh = new SerialRefresh(async () =>
        {
            running++;
            maxRunning = Math.Max(maxRunning, running);
            var gate = new TaskCompletionSource();
            gates.Add(gate);
            await gate.Task;
            running--;
        });

        var first = refresh.Request();
        var second = refresh.Request();
        var third = refresh.Request();
        Assert.Single(gates);

        gates[0].SetResult();
        await UiWait.UntilAsync(() => gates.Count == 2);
        Assert.False(first.IsCompleted);

        gates[1].SetResult();
        await Task.WhenAll(first, second, third);
        Assert.Equal(2, gates.Count);
        Assert.Equal(1, maxRunning);

        // Idle again: the next request starts a new run at once.
        var fourth = refresh.Request();
        Assert.Equal(3, gates.Count);
        gates[2].SetResult();
        await fourth;
    }
}
