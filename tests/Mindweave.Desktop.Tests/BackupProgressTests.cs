using Avalonia.Headless.XUnit;
using Mindweave.Application.Startup;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// performance-7 (D-042): the delete dialogs show the backup's progress only once the work has taken a second; at full
/// size the copy takes about 0.2 s (scripts/perf.sh), so a quick delete never shows it.
/// </summary>
public class BackupProgressTests
{
    [AvaloniaFact]
    public async Task Progress_appears_after_a_second_follows_the_copy_and_goes_away_at_the_end()
    {
        var backup = new SteppedBackup();
        var progress = new BackupProgressViewModel(backup);
        var done = new TaskCompletionSource<int>();

        var watched = progress.WatchAsync(() => done.Task);
        Assert.False(progress.IsShown);

        await UiWait.UntilAsync(() => progress.IsShown);
        Assert.True(progress.IsIndeterminate);

        backup.Progress = 0.4;
        await UiWait.UntilAsync(() => !progress.IsIndeterminate && Math.Abs(progress.Value - 40) < 0.01);

        done.SetResult(7);
        Assert.Equal(7, await watched);
        Assert.False(progress.IsShown);
        Assert.Equal(0, progress.Value);
    }

    [AvaloniaFact]
    public async Task Quick_work_never_shows_progress()
    {
        var progress = new BackupProgressViewModel(new SteppedBackup());
        var seen = false;
        progress.PropertyChanged += (_, e) => seen |= e.PropertyName == nameof(BackupProgressViewModel.IsShown) && progress.IsShown;

        Assert.Equal(1, await progress.WatchAsync(async () =>
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            return 1;
        }));

        Assert.False(seen);
    }

    private sealed class SteppedBackup : IDatabaseBackup
    {
        public double? Progress { get; set; }

        public Task<string> BackupAsync(BackupReason reason, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public void Discard(string path)
        {
        }
    }
}
