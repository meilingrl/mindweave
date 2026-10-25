using Mindweave.Infrastructure.Hosting;

namespace Mindweave.Infrastructure.Tests;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private async Task<SingleInstance?> AcquireWithinAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (SingleInstance.TryAcquire(_dir.Paths) is { } acquired)
            {
                return acquired;
            }

            if (DateTime.UtcNow > deadline)
            {
                return null;
            }

            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Only_one_holder_per_data_directory_and_release_frees_it()
    {
        var first = SingleInstance.TryAcquire(_dir.Paths);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(_dir.Paths));

        await first.DisposeAsync();

        await using var second = SingleInstance.TryAcquire(_dir.Paths);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task A_second_launch_activates_the_holder()
    {
        await using var holder = SingleInstance.TryAcquire(_dir.Paths)!;
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        holder.StartListening(() => activated.TrySetResult());

        var result = await SingleInstance.SignalPrimaryAsync(_dir.Paths, TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(SecondaryLaunchResult.Activated, result);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_holder_that_never_listens_is_reported_as_not_responding()
    {
        await using var holder = SingleInstance.TryAcquire(_dir.Paths)!;

        var result = await SingleInstance.SignalPrimaryAsync(_dir.Paths, TimeSpan.FromMilliseconds(800), Ct);

        Assert.Equal(SecondaryLaunchResult.PrimaryNotResponding, result);
    }

    [Fact]
    public async Task Different_data_directories_do_not_interfere()
    {
        using var other = new TempDataDirectory();
        await using var a = SingleInstance.TryAcquire(_dir.Paths);
        await using var b = SingleInstance.TryAcquire(other.Paths);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(SingleInstance.PipeNameFor(_dir.Paths), SingleInstance.PipeNameFor(other.Paths));
    }

    [Fact]
    public async Task Another_process_holds_the_lock_until_it_is_killed()
    {
        using var child = ChildProcess.Start("hold-lock", _dir.Paths.DataDirectory);
        Assert.Equal("ready", await child.WaitForLineAsync(TimeSpan.FromSeconds(30)));

        Assert.Null(SingleInstance.TryAcquire(_dir.Paths));
        Assert.Equal(SecondaryLaunchResult.Activated, await SingleInstance.SignalPrimaryAsync(_dir.Paths, TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("activated", await child.WaitForLineAsync(TimeSpan.FromSeconds(5)));

        await child.KillAsync();

        // Windows releases a dead process's handles shortly after it is reported exited.
        await using var mine = await AcquireWithinAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(mine);
    }
}
