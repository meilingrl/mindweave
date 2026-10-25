using Avalonia.Threading;

namespace Mindweave.Desktop.Services;

/// <summary>Marshals work from background threads (generation, storage) onto the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>
/// Coalesces bursts of change notifications into at most one UI update per interval — the "UI refresh"
/// rhythm, kept separate from network chunks and disk checkpoints.
/// </summary>
public sealed class UiThrottle(IUiDispatcher dispatcher, TimeSpan interval, Action update)
{
    private int _scheduled;

    public void Request()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(interval).ContinueWith(
            _ => dispatcher.Post(() =>
            {
                Volatile.Write(ref _scheduled, 0);
                update();
            }),
            TaskScheduler.Default);
    }
}
