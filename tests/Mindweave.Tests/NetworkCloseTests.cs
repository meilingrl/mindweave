using Avalonia.Headless.XUnit;
using Mindweave.Application.Navigation;
using Mindweave.Desktop.ViewModels;
using Xunit;

namespace Mindweave.Tests;

public sealed class NetworkCloseTests
{
    [AvaloniaFact]
    public async Task Permit_cannot_commit_discard_before_Main_drain_barrier()
    {
        NetworkStoreProxy control = null!;
        await using var f = await NetworkNavigationFixture.Open(store => NetworkStoreProxy.Wrap(store, out control));
        var page = await f.Network(); await page.Session.ArrangeAsync(); control.HoldSave = true;
        var saving = page.Session.SaveAsync(); await control.SaveEntered.Task;
        await page.Session.ArrangeAsync();
        var confirmation = f.Main.ConfirmLayoutLeaveAsync(); f.Main.DiscardLayoutAndContinueCommand.Execute(null);
        var permit = (await confirmation)!; page.Session.Quiesce();
        try { Assert.False(f.Main.TryCommitLayoutLeave(permit)); Assert.True(page.Session.IsDirty); }
        finally { control.SaveRelease.TrySetResult(); await saving; page.Session.Resume(); }
    }

    [AvaloniaFact]
    public async Task Failed_core_close_return_resumes_network_without_restarting_stopped_work()
    {
        await using var f = await NetworkNavigationFixture.Open();
        await f.Chat.Configuration.SaveAsync(BranchStorageTests.Config(32768), "synthetic");
        await f.Main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.Data.Conversation));
        var chat = Assert.IsType<ChatViewModel>(f.Main.ActiveContent); chat.InputText = "pending"; await chat.SendCommand.ExecuteAsync(null);
        var call = await f.Transport.NextAsync(); var page = await f.Network(); await page.Session.ArrangeAsync();
        f.Data.Sql("CREATE TRIGGER fail_network_close BEFORE UPDATE ON generation_task BEGIN SELECT RAISE(ABORT,'core'); END");
        try
        {
            var closing = f.Root.CloseAsync(); f.Main.DiscardLayoutAndContinueCommand.Execute(null); await closing;
            await f.Root.ConfirmCloseCommand.ExecuteAsync(null);
            Assert.False(f.Root.IsClosed); Assert.NotNull(f.Root.CloseError); Assert.True(page.Session.IsQuiesced);
            Assert.True(call.Cancellation.IsCancellationRequested);
            f.Root.ReturnToWorkbenchCommand.Execute(null); Assert.False(page.Session.IsQuiesced);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); await f.Main.DrainBackgroundRefreshesAsync();
            await page.Session.ArrangeAsync(); Assert.True(page.Session.IsDirty); Assert.Equal(1, f.Transport.StartCount);
            Assert.True(call.Cancellation.IsCancellationRequested);
        }
        finally { f.Data.Sql("DROP TRIGGER fail_network_close"); await f.Root.RetrySaveCommand.ExecuteAsync(null); }
    }

    [AvaloniaFact]
    public async Task Close_freezes_and_drains_initial_write_before_store_disposal_and_ignores_return_during_drain()
    {
        NetworkStoreProxy control = null!;
        await using var f = await NetworkNavigationFixture.Open(store => NetworkStoreProxy.Wrap(store, out control));
        control.HoldInitial = true;
        var opening = f.Main.NavigateAsync(new(MainArea.Network)); await control.InitialEntered.Task;
        var closing = f.Root.CloseAsync();
        try
        {
            Assert.False(closing.IsCompleted); Assert.False(control.Disposed);
            f.Root.ReturnToWorkbenchCommand.Execute(null);
            Assert.True(f.Main.IsClosing);
        }
        finally { control.InitialRelease.TrySetResult(); await opening; await closing.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(f.Root.IsClosed); Assert.True(control.Disposed); Assert.False(control.WriteAfterDispose);
    }

    [AvaloniaFact]
    public async Task Discard_permit_waits_for_accepted_save_and_restores_its_committed_baseline()
    {
        NetworkStoreProxy control = null!;
        await using var f = await NetworkNavigationFixture.Open(store => NetworkStoreProxy.Wrap(store, out control));
        var page = await f.Network(); await page.Session.ArrangeAsync(); control.HoldSave = true;
        var saving = page.Session.SaveAsync(); await control.SaveEntered.Task;
        await page.Session.ArrangeAsync();
        var closing = f.Root.CloseAsync(); f.Main.DiscardLayoutAndContinueCommand.Execute(null);
        await NetworkNavigationTests.WaitUi(() => page.Session.IsQuiesced);
        Assert.True(page.Session.IsDirty); Assert.False(closing.IsCompleted); Assert.False(control.Disposed);
        control.SaveRelease.SetResult(); await saving; await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Root.IsClosed); Assert.False(page.Session.IsDirty);
        Assert.Equal(page.Session.SavedPositions.OrderBy(x => x.Key), page.Session.Positions.OrderBy(x => x.Key));
        Assert.False(control.WriteAfterDispose);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_task_prompt_preserves_layout_choice_and_running_work(bool save)
    {
        await using var f = await NetworkNavigationFixture.Open();
        await f.Chat.Configuration.SaveAsync(BranchStorageTests.Config(32768), "synthetic");
        await f.Main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.Data.Conversation));
        var chat = Assert.IsType<ChatViewModel>(f.Main.ActiveContent); chat.InputText = "pending"; await chat.SendCommand.ExecuteAsync(null);
        var call = await f.Transport.NextAsync(); var page = await f.Network(); await page.Session.ArrangeAsync();
        var closing = f.Root.CloseAsync(); var repeated = f.Root.CloseAsync(); Assert.Same(closing, repeated);
        Assert.True(f.Main.IsLayoutLeaveConfirmationVisible);
        (save ? f.Main.SaveLayoutAndContinueCommand : f.Main.DiscardLayoutAndContinueCommand).Execute(null);
        await Task.WhenAll(closing, repeated).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Root.IsCloseConfirmationVisible); Assert.False(call.Cancellation.IsCancellationRequested);
        f.Root.CancelCloseCommand.Execute(null);
        Assert.Equal(!save, page.Session.IsDirty); Assert.False(page.Session.IsQuiesced);
        Assert.False(call.Cancellation.IsCancellationRequested); Assert.False(f.Main.IsClosing);
    }

    [AvaloniaFact]
    public async Task Clean_permit_does_not_bypass_layout_created_during_task_prompt()
    {
        await using var f = await NetworkNavigationFixture.Open(); var page = await f.Network();
        using var admission = await f.Chat.Runtime.EnterAcceptanceAsync();
        await f.Root.CloseAsync(); Assert.True(f.Root.IsCloseConfirmationVisible);
        await page.Session.ArrangeAsync();
        var closing = f.Root.ConfirmCloseCommand.ExecuteAsync(null);
        Assert.True(f.Main.IsLayoutLeaveConfirmationVisible); Assert.True(f.Chat.Runtime.HasActiveOrPendingWork);
        f.Main.CancelLayoutLeaveCommand.Execute(null); await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(page.Session.IsDirty); Assert.False(f.Root.IsClosed); Assert.False(f.Chat.Runtime.IsAdmissionPaused);
    }
}

public interface INetworkDesktopStore : SummaryDesktopTests.IDesktopStore, Mindweave.Application.Network.INetworkStore { }
public class NetworkStoreProxy : System.Reflection.DispatchProxy
{
    public Mindweave.Infrastructure.Storage.SqliteWorkspaceStore Inner { get; private set; } = null!;
    public bool HoldInitial { get; set; }
    public bool FailInitial { get; set; }
    public int InitialAttempts { get; private set; }
    public bool HoldSave { get; set; }
    public bool Disposed { get; private set; }
    public bool WriteAfterDispose { get; private set; }
    public TaskCompletionSource InitialEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource InitialRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SaveRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static INetworkDesktopStore Wrap(Mindweave.Infrastructure.Storage.SqliteWorkspaceStore inner, out NetworkStoreProxy control)
    {
        var proxy = Create<INetworkDesktopStore, NetworkStoreProxy>(); control = (NetworkStoreProxy)(object)proxy;
        control.Inner = inner; return proxy;
    }
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IAsyncDisposable.DisposeAsync)) Disposed = true;
        if (method.Name == nameof(Mindweave.Application.Network.INetworkStore.TrySaveInitialNodePositionAsync))
        {
            InitialAttempts++;
            if (FailInitial) return Task.FromException<Mindweave.Application.Network.LayoutWriteResult>(new IOException("initial write"));
            if (HoldInitial) return HoldWrite(method, args, InitialEntered, InitialRelease);
        }
        if (method.Name == nameof(Mindweave.Application.Network.INetworkStore.SaveLayoutAsync) && HoldSave)
            return HoldWrite(method, args, SaveEntered, SaveRelease);
        return method.Invoke(Inner, args);
    }
    private async Task<Mindweave.Application.Network.LayoutWriteResult> HoldWrite(System.Reflection.MethodInfo method, object?[]? args, TaskCompletionSource entered, TaskCompletionSource release)
    {
        entered.TrySetResult(); await release.Task;
        WriteAfterDispose |= Disposed;
        return await (Task<Mindweave.Application.Network.LayoutWriteResult>)method.Invoke(Inner, args)!;
    }
}
