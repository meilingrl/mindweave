using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Startup;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// The progress of the backup a delete makes first (performance-7, D-042; D-030 keeps the backup). Shown only once the
/// work has taken a second: at full size the copy takes about 0.2 s (scripts/perf.sh), so most deletes never show it.
/// While a copy is being written the bar follows its size; before and after, it runs without a value.
/// </summary>
public sealed partial class BackupProgressViewModel(IDatabaseBackup backup) : ObservableObject
{
    internal static readonly TimeSpan ShowAfter = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(100);

    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>From 0 to 100.</summary>
    [ObservableProperty]
    public partial double Value { get; private set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; } = true;

    /// <summary>Runs <paramref name="work"/> (on the UI thread), showing progress once it has taken a second.</summary>
    public async Task<T> WatchAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var stop = new CancellationTokenSource();
        var watching = ShowWhileRunningAsync(stop.Token);
        try
        {
            return await work();
        }
        finally
        {
            await stop.CancelAsync();
            await watching;
            IsShown = false;
            IsIndeterminate = true;
            Value = 0;
        }
    }

    private async Task ShowWhileRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ShowAfter, cancellationToken);
            IsShown = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                var progress = backup.Progress;
                IsIndeterminate = progress is null;
                Value = (progress ?? 0) * 100;
                await Task.Delay(Every, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The work ended.
        }
    }
}
