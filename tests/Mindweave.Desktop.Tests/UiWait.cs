using Avalonia.Threading;

namespace Mindweave.Desktop.Tests;

internal static class UiWait
{
    /// <summary>Pumps the UI dispatcher until <paramref name="condition"/> holds; fails after a timeout instead of racing.</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not reached on the UI thread.");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }
}
