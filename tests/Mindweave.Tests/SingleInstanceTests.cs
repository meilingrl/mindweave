using System.Diagnostics;
using Mindweave.Infrastructure.Runtime;
using Xunit;

namespace Mindweave.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task Cancelling_listener_does_not_require_the_callers_UI_context_to_pump()
    {
        using var dir = new TestDirectory();
        using var owner = SingleInstance.TryAcquire(AppPaths.ForTrack("codex-steady", dir.Path));
        using var cancellation = new CancellationTokenSource();
        var previous = SynchronizationContext.Current;
        Task listening;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            listening = owner!.ListenAsync(() => { }, cancellation.Token);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        cancellation.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Tracks_use_different_data_and_activation_identities()
    {
        using var dir = new TestDirectory();
        var steady = AppPaths.ForTrack("codex-steady", dir.Path);
        var auto = AppPaths.ForTrack("claude-auto", dir.Path);
        Assert.NotEqual(steady.DatabasePath, auto.DatabasePath);
        Assert.NotEqual(steady.PipeName, auto.PipeName);
        Assert.NotEqual(steady.CredentialNamespace, auto.CredentialNamespace);
        Assert.StartsWith(dir.Path, steady.DatabasePath);
        Assert.Throws<ArgumentException>(() => AppPaths.ForTrack("../escape", dir.Path));
    }

    [Fact]
    public async Task Second_process_notifies_owner_and_cannot_take_database_ownership()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        using var owner = SingleInstance.TryAcquire(paths);
        Assert.NotNull(owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listening = owner.ListenAsync(() => activated.TrySetResult(), timeout.Token);
        using var child = ProcessProbe.Start("instance", dir.Path);
        await child.WaitForExitAsync(timeout.Token);
        Assert.True(child.ExitCode == 0, await child.StandardError.ReadToEndAsync(timeout.Token));
        await activated.Task.WaitAsync(timeout.Token);
        timeout.Cancel();
        await listening;
    }

    [Fact]
    public void Releasing_owner_allows_new_owner_without_deleting_lock_file()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        using (var owner = SingleInstance.TryAcquire(paths))
        {
            Assert.NotNull(owner);
            SingleInstance? second = null;
            var error = Record.Exception(() => second = SingleInstance.TryAcquire(paths));
            Assert.True(error is null, $"HRESULT {error?.HResult:X}: {error}");
            Assert.Null(second);
        }
        Assert.True(File.Exists(paths.LockPath));
        using var next = SingleInstance.TryAcquire(paths);
        Assert.NotNull(next);
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }
}
