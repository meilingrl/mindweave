using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryVersionDesktopTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_delete_ack_repair_after_navigation_projects_copyable_recovery_in_actual_main_and_clears_only_repaired_IO(bool anotherWriteFails)
    {
        await using var f = await SummaryStorageFixture.Open(); var proxy = Proxy(f, out var control);
        var root = RecoveryRoot(f, proxy); await root.InitializeAsync(); var main = Assert.IsType<MainViewModel>(root.Content);
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        var page = Assert.IsType<SummaryViewModel>(main.ActiveContent);
        var window = new MainWindow { DataContext = root }; window.Show(); Layout(); DisableReading(window);
        try
        {
            await page.EditCommand.ExecuteAsync(null); await PrepareLastDelete(page, f.First);
            control.Intercept = "DeleteSummaryVersionsAsync"; control.Hold = true; control.LoseAck = true;
            var deleting = page.ConfirmVersionOperationCommand.ExecuteAsync(null);
            await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            page.Draft.Title = "Hidden pending title"; page.Draft.Markdown = "Copy this pending input after navigation";
            page.Draft.StopDebounce(); control.Release.TrySetResult(); await deleting;
            Assert.True(main.HasPersistenceFailure); Assert.False(page.HasRemovedNodeInput);
            control.FailSelection = anotherWriteFails;
            await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.Conversation));
            Assert.IsType<ChatViewModel>(main.ActiveContent);
            await root.RetrySaveCommand.ExecuteAsync(null); Layout();
            if (anotherWriteFails)
            {
                Assert.True(main.HasPersistenceFailure); // The independent, still failing selection write survives discovery.
                AssertRecoveryText(window, "Hidden pending title", "Copy this pending input after navigation");
                control.FailSelection = false;
                var retry = VisibleRecoveryControl<Button>(window, "RetryRecoveredSummaryPersistenceButton");
                await Assert.IsAssignableFrom<IAsyncRelayCommand>(retry.Command).ExecuteAsync(null); Layout();
            }
            Assert.False(main.HasPersistenceFailure); // The original lost ack was reconciled; expected input is not IO.
            AssertRecoveryText(window, "Hidden pending title", "Copy this pending input after navigation");
            Assert.IsType<ChatViewModel>(main.ActiveContent); // Recovery does not invent a deleted business route.
            Assert.Null((await f.Versions.LoadSummarySelectionAsync()).Selection);
            await root.CloseAsync(); Assert.False(root.IsClosed); Assert.Contains("输入", root.CloseError!);
            root.ReturnToWorkbenchCommand.Execute(null); Layout();
            AssertRecoveryText(window, "Hidden pending title", "Copy this pending input after navigation");
            var shownRevision = page.Draft.CaptureRemovedInput().LocalRevision;
            page.Draft.Markdown = "Newer input invalidates old consent"; page.Draft.StopDebounce();
            var newerInput = page.Draft.CaptureRemovedInput(); Assert.True(newerInput.LocalRevision > shownRevision);
            await ClickRecoveryDiscard(window); Assert.True(page.Draft.HasRemovedNodeInput);
            Assert.Equal(newerInput, page.Draft.CaptureRemovedInput());
            AssertRecoveryText(window, "Hidden pending title", "Newer input invalidates old consent");
            await ClickRecoveryDiscard(window); Assert.False(page.Draft.HasRemovedNodeInput);
            await root.RetrySaveCommand.ExecuteAsync(null); Assert.False(main.HasPersistenceFailure);
            Assert.Equal(1, control.Calls); Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node));
            Assert.Null(await f.Versions.LoadSummaryVersionAsync(f.First)); Assert.Null(await f.Versions.LoadSummaryReadingAsync(f.First));
            await root.CloseAsync(); Assert.True(root.IsClosed);
        }
        finally { control.FailSelection = false; control.Release.TrySetResult(); await CleanupRecovery(root, page, window); }
    }

    [AvaloniaFact]
    public async Task Input_at_final_postdelete_node_read_and_after_projection_remains_owned_until_exact_confirmation()
    {
        await using var f = await SummaryStorageFixture.Open(); var proxy = Proxy(f, out var control);
        var root = RecoveryRoot(f, proxy); await root.InitializeAsync(); var main = Assert.IsType<MainViewModel>(root.Content);
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        var page = Assert.IsType<SummaryViewModel>(main.ActiveContent);
        var window = new MainWindow { DataContext = root }; window.Show(); Layout(); DisableReading(window);
        try
        {
            await page.EditCommand.ExecuteAsync(null); await PrepareLastDelete(page, f.First);
            control.Intercept = "DeleteSummaryVersionsAsync"; control.HoldPostDeleteNodeRead = true;
            var deleting = page.ConfirmVersionOperationCommand.ExecuteAsync(null);
            await control.NodeReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node)); Assert.Null(page.Draft.Token);
            page.Draft.Title = "Final-read late title"; page.Draft.Markdown = "Final-read late body"; page.Draft.StopDebounce();
            control.ReleaseNodeRead.TrySetResult(); await deleting; Layout(); await main.DrainBackgroundRefreshesAsync();
            Assert.True(page.Draft.HasRemovedNodeInput); Assert.False(main.HasPersistenceFailure); Assert.Empty(page.SavedBody);
            AssertRecoveryText(window, "Final-read late title", "Final-read late body");
            await root.CloseAsync(); Assert.False(root.IsClosed); Assert.False(main.HasPersistenceFailure);
            root.ReturnToWorkbenchCommand.Execute(null); Layout();
            await ClickRecoveryDiscard(window); Assert.False(page.Draft.HasRemovedNodeInput);
            Layout(); await main.DrainBackgroundRefreshesAsync(); Assert.IsType<SummaryListViewModel>(main.ActiveContent);
            // Even after the prior intent was completed and its page detached, a queued input
            // still belongs to the deleted node's shared memory session, never to an absent lease.
            page.Draft.Title = "After-projection late title"; page.Draft.Markdown = "After-projection late body"; page.Draft.StopDebounce();
            Layout(); Assert.True(page.Draft.HasRemovedNodeInput);
            AssertRecoveryText(window, "After-projection late title", "After-projection late body");
            await root.CloseAsync(); Assert.False(root.IsClosed); root.ReturnToWorkbenchCommand.Execute(null); Layout();
            await ClickRecoveryDiscard(window); // First click may refresh consent captured before the second queued field.
            if (page.Draft.HasRemovedNodeInput) await ClickRecoveryDiscard(window);
            Assert.False(page.Draft.HasRemovedNodeInput); Assert.Equal(1, control.Calls);
            Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node));
            await root.CloseAsync(); Assert.True(root.IsClosed);
        }
        finally { control.ReleaseNodeRead.TrySetResult(); await CleanupRecovery(root, page, window); }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Shared_recovery_from_two_real_page_incarnations_resolves_the_active_page_and_uses_workbench_lifecycle(bool priorFailedClose, bool recoverOnChat)
    {
        await using var f = await SummaryStorageFixture.Open(); var proxy = Proxy(f, out var control);
        var root = RecoveryRoot(f, proxy); await root.InitializeAsync(); var main = Assert.IsType<MainViewModel>(root.Content);
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        var first = Assert.IsType<SummaryViewModel>(main.ActiveContent);
        SummaryViewModel? second = null;
        var window = new MainWindow { DataContext = root }; window.Show(); Layout(); DisableReading(window);
        try
        {
            control.FailSelection = priorFailedClose;
            await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.Conversation));
            if (priorFailedClose)
            {
                Assert.True(main.HasPersistenceFailure);
                await root.CloseAsync(); Assert.False(root.IsClosed); Assert.NotNull(root.CloseError);
                root.ReturnToWorkbenchCommand.Execute(null);
                control.FailSelection = false; await root.RetrySaveCommand.ExecuteAsync(null);
                Assert.False(main.HasPersistenceFailure);
            }
            await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
            second = Assert.IsType<SummaryViewModel>(main.ActiveContent);
            Assert.NotSame(first, second); Assert.Same(first.Draft, second.Draft);
            Layout(); DisableReading(window);
            await second.EditCommand.ExecuteAsync(null); await PrepareLastDelete(second, f.First);
            control.Intercept = "DeleteSummaryVersionsAsync"; control.Hold = true; control.LoseAck = recoverOnChat;
            var deleting = second.ConfirmVersionOperationCommand.ExecuteAsync(null);
            await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second.Draft.Title = "Two pages one input"; second.Draft.Markdown = "Shared recovery belongs to the workbench";
            second.Draft.StopDebounce(); control.Release.TrySetResult(); await deleting;
            if (recoverOnChat)
            {
                await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.Conversation));
                await root.RetrySaveCommand.ExecuteAsync(null);
                Assert.IsType<ChatViewModel>(main.ActiveContent);
            }
            Layout(); Assert.True(first.HasRemovedNodeInput); Assert.True(second.HasRemovedNodeInput);
            AssertRecoveryText(window, "Two pages one input", "Shared recovery belongs to the workbench");
            Assert.True(main.IsInteractionEnabled);
            Assert.True(main.RemovedSummaryInput!.CanDiscardRemovedInput); // No extra Close/Return to revive an old owner.
            await ClickRecoveryDiscard(window);
            if (second.Draft.HasRemovedNodeInput) await ClickRecoveryDiscard(window); // Refresh an older exact consent if needed.
            Assert.False(first.HasRemovedNodeInput); Assert.False(second.HasRemovedNodeInput);
            Assert.False(first.HasPendingVersionOperation); Assert.False(second.HasPendingVersionOperation);
            Assert.False(second.Draft.HasRemovedNodeInput); Assert.False(main.HasRemovedSummaryInput);
            Layout(); await main.DrainBackgroundRefreshesAsync();
            if (recoverOnChat) Assert.IsType<ChatViewModel>(main.ActiveContent);
            else Assert.IsType<SummaryListViewModel>(main.ActiveContent);
            Assert.Equal(1, control.Calls); Assert.False(main.HasPersistenceFailure);
            Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node)); Assert.Null(await f.Versions.LoadSummaryVersionAsync(f.First));
            Assert.Null(await f.Versions.LoadSummaryReadingAsync(f.First)); Assert.Null((await f.Versions.LoadSummarySelectionAsync()).Selection);
            await root.CloseAsync(); Assert.True(root.IsClosed);
        }
        finally { control.FailSelection = false; control.Release.TrySetResult(); await CleanupRecovery(root, second ?? first, window); }
    }

    private static RootViewModel RecoveryRoot(SummaryStorageFixture f, SummaryDesktopTests.IDesktopStore store) => new(
        () => Task.FromResult<Mindweave.Application.Storage.IWorkspaceStore>(store), new DesktopActions(f.Paths.DataDirectory, () => { }), f.Paths.DataDirectory,
        actual => new DesktopChatSession((Mindweave.Application.Chat.IChatStore)actual,
            new GenerationCoordinatorTests.ControlledTransport(), new GenerationCoordinatorTests.MemoryVault()));
    private static async Task PrepareLastDelete(SummaryViewModel page, Guid version)
    {
        await page.DeleteVersionsCommand.ExecuteAsync(null); page.DeletionSelection.Add(page.Versions.Single(v => v.Identity.Id == version));
        await page.PreviewDeletionCommand.ExecuteAsync(null); Assert.True(page.CanConfirmVersionOperation);
    }
    private static void DisableReading(Control root)
    { foreach (var view in root.GetVisualDescendants().OfType<SummaryView>()) view.AutomaticObservation = false; }
    private static T VisibleRecoveryControl<T>(Control root, string name) where T : Control
    {
        Layout(); var control = Assert.Single(root.GetVisualDescendants().OfType<T>(), c => c.Name == name && c.IsVisible && c.GetVisualAncestors().All(a => a.IsVisible));
        Assert.NotNull(TopLevel.GetTopLevel(control)); return control;
    }
    private static void AssertRecoveryText(Control root, string title, string body)
    {
        Assert.Equal(title, VisibleRecoveryControl<TextBox>(root, "RecoveredSummaryTitle").Text);
        var input = VisibleRecoveryControl<TextBox>(root, "RecoveredSummaryBody"); Assert.True(input.IsReadOnly);
        input.SelectAll(); Assert.Equal(body, input.SelectedText); // Actually selectable/copyable text in the compiled Main view.
    }
    private static async Task ClickRecoveryDiscard(Control root)
    {
        var button = VisibleRecoveryControl<Button>(root, "DiscardRecoveredSummaryInputButton"); Assert.True(button.IsEnabled);
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(button.Command).ExecuteAsync(null); Layout();
    }
    private static async Task CleanupRecovery(RootViewModel root, SummaryViewModel page, MainWindow window)
    {
        // The assertions above, not cleanup/disposal, must expose a regression.
        if (!root.IsClosed)
        {
            root.ReturnToWorkbenchCommand.Execute(null); page.ResumeAfterClose();
            if (page.HasRemovedNodeInput) await page.DiscardRemovedInputCommand.ExecuteAsync(null);
            if (page.HasRemovedNodeInput) await page.DiscardRemovedInputCommand.ExecuteAsync(null);
            await root.RetrySaveCommand.ExecuteAsync(null); await root.CloseAsync();
        }
        window.DataContext = null; window.Close();
    }
}
