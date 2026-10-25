using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Mindweave.Core;
using Mindweave.Desktop;
using Xunit;

namespace Mindweave.Core.Tests;

public class WindowLifecycleTests
{
    [AvaloniaFact]
    public async Task Confirmed_exit_blocks_new_edits_until_the_snapshot_has_been_saved()
    {
        using var folder = new TestFolder();
        var vm = new WorkspaceViewModel(store: new JsonWorkspaceStore(Path.Combine(folder.Path, "state.json")));
        var window = new MainWindow(vm);
        window.Show(); Dispatcher.UIThread.RunJobs();
        var closing = window.RequestCloseAsync();
        Assert.False(window.FindControl<Grid>("ShellGrid")!.IsEnabled);
        Assert.True(await closing);
        Assert.True(window.CloseApproved);
    }

    [AvaloniaFact]
    public async Task Exit_prompt_can_keep_running_then_confirm_cancel_and_save_partial()
    {
        using var folder = new TestFolder();
        var store = new JsonWorkspaceStore(Path.Combine(folder.Path, "state.json"));
        var stream = new ControlledReplyStream();
        var vm = new WorkspaceViewModel(replyStream: stream, store: store);
        var window = new MainWindow(vm);
        window.Show(); Dispatcher.UIThread.RunJobs();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.MessageUpdated += update => { if (update.Change == GenerationChange.Appended) received.TrySetResult(); };
        var generation = vm.SendAsync("closing-test");
        await stream.For("closing-test").Writer.WriteAsync("saved partial answer");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = window.RequestCloseAsync();
        var dialog = Assert.Single(window.OwnedWindows);
        dialog.GetLogicalDescendants().OfType<Button>().Single(x => x.Name == "KeepExploringButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(await first);
        Assert.True(vm.Generation.HasRunning);
        Assert.True(window.IsVisible);
        var confirmed = window.RequestCloseAsync();
        Assert.Single(window.OwnedWindows).GetLogicalDescendants().OfType<Button>().Single(x => x.Name == "ConfirmExitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await confirmed);
        await generation;
        Assert.False(window.IsVisible);
        var restored = (await store.LoadAsync())!;
        Assert.Equal("saved partial answer", restored.Current.Messages.Last().Markdown);
        Assert.Equal(ReplyStatus.Cancelled, restored.Current.Messages.Last().Status);
    }

    [AvaloniaFact]
    public async Task Failed_save_keeps_the_window_open_and_reports_the_error()
    {
        using var folder = new TestFolder();
        var store = new JsonWorkspaceStore(folder.Path); // Existing directory cannot be replaced by a state file.
        var vm = new WorkspaceViewModel(store: store);
        var window = new MainWindow(vm);
        window.Show(); Dispatcher.UIThread.RunJobs();
        window.FindControl<TextBox>("Composer")!.Text = "keep this unsaved draft";
        Assert.False(await window.RequestCloseAsync());
        Assert.True(window.IsVisible);
        Assert.Equal("keep this unsaved draft", vm.Current.Draft);
        Assert.NotNull(vm.PersistenceError);
        Assert.True(window.FindControl<Grid>("ShellGrid")!.IsEnabled);
        Assert.Contains("保存失败", window.FindControl<TextBlock>("FooterStatus")!.Text);
        window.Hide();
    }
}
