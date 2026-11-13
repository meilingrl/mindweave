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

/// <summary>
/// Runs an asynchronous refresh at most once at a time. A request that arrives while a refresh is running
/// schedules exactly one more run after it, so the last run to finish always started after the last request.
/// Without this, an older load that finishes late overwrites a newer one and nothing corrects it.
/// Call on the UI thread only.
/// </summary>
public sealed class SerialRefresh(Func<Task> refresh)
{
    private Task _running = Task.CompletedTask;
    private bool _busy;
    private bool _again;

    /// <summary>Starts a refresh, or queues one after the running one. Completes when no request is left.</summary>
    public Task Request()
    {
        if (_busy)
        {
            _again = true;
            return _running;
        }

        _running = RunAsync();
        return _running;
    }

    /// <summary>
    /// A refresh failed (health check 2026-11-12, errors-5): reported here, once for the whole application (logged and
    /// named on the shell), instead of leaving a page blank and silent, or crashing a command that awaited the reload.
    /// </summary>
    public static event EventHandler<Exception>? Failed;

    private async Task RunAsync()
    {
        _busy = true;
        try
        {
            do
            {
                _again = false;
                try
                {
                    await refresh();
                }
#pragma warning disable CA1031 // Reported centrally; the page keeps what it showed, and a queued request still runs.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    Failed?.Invoke(this, ex);
                }
            }
            while (_again);
        }
        finally
        {
            _busy = false;
        }
    }
}
