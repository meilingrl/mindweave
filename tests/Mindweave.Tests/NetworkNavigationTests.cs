using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Core.Summaries;
using Mindweave.Core.Network;
using Mindweave.Desktop.Controls;
using Mindweave.Desktop.Views;
using Avalonia.Headless.XUnit;
using Mindweave.Application.Chat;
using Mindweave.Application.Navigation;
using Mindweave.Application.Storage;
using Mindweave.Desktop.Network;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Xunit;

namespace Mindweave.Tests;

public sealed class NetworkNavigationTests
{
    [AvaloniaFact]
    public async Task Layout_save_does_not_block_opening_saved_summary_body()
    {
        NetworkStoreProxy control = null!;
        await using var f = await NetworkNavigationFixture.Open(store => NetworkStoreProxy.Wrap(store, out control));
        var page = await f.Network(); await page.Session.ArrangeAsync(); control.HoldSave = true;
        var saving = page.Session.SaveAsync(); await control.SaveEntered.Task;
        page.SelectedNode = Assert.Single(page.Nodes); var opening = page.OpenSummaryCommand.ExecuteAsync(null);
        try
        {
            await opening.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsType<SummaryViewModel>(f.Main.ActiveContent); Assert.False(saving.IsCompleted);
        }
        finally { control.SaveRelease.TrySetResult(); await saving; await opening; }
        await f.Main.GoBackAsync(); Assert.Same(page.Session, Assert.IsType<NetworkViewModel>(f.Main.ActiveContent).Session);
    }

    [AvaloniaFact]
    public async Task Failed_initial_write_retries_only_on_next_network_entry()
    {
        NetworkStoreProxy control = null!;
        await using var f = await NetworkNavigationFixture.Open(store => NetworkStoreProxy.Wrap(store, out control));
        control.FailInitial = true; var page = await f.Network();
        Assert.Single(page.Nodes); Assert.False(page.Session.IsDirty); Assert.False(f.Main.HasPersistenceFailure);
        Assert.Equal(1, control.InitialAttempts);
        control.FailInitial = false; await f.Main.NavigateAsync(new(MainArea.Network));
        Assert.Equal(2, control.InitialAttempts); Assert.Single(page.Session.SavedPositions);
    }

    [AvaloniaFact]
    public async Task Bound_overlay_cancels_navigation_while_changes_lock_is_held()
    {
        await using var f = await NetworkNavigationFixture.Open(); var page = await f.Network(); await page.Session.ArrangeAsync();
        var view = new Mindweave.Desktop.Views.MainView { DataContext = f.Main };
        var window = new Avalonia.Controls.Window { Content = view, Width = 1000, Height = 700 }; window.Show();
        try
        {
            f.Main.NewWorkspaceName = "second"; var leaving = f.Main.CreateWorkspaceAsync();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var button = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(view, "CancelLayoutLeaveButton")!;
            Assert.True(button.IsEffectivelyVisible); Assert.False(f.Main.IsInteractionEnabled);
            button.Command!.Execute(null); await leaving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(f.Main.IsInteractionEnabled); Assert.Equal(f.Data.Workspace, f.Main.CurrentWorkspace!.Id);
            Assert.True(page.Session.IsDirty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_workspace_leave_has_no_persisted_or_navigation_effect(bool create)
    {
        await using var f = await NetworkNavigationFixture.Open();
        var target = await f.Data.Store.CreateAsync("second");
        await f.Main.InitializeAsync(); await f.Main.SwitchWorkspaceAsync(f.Data.Workspace); var page = await f.Network(); await page.Session.ArrangeAsync();
        var before = f.Data.DatabaseSnapshot(); f.Main.NewWorkspaceName = "third";
        var leaving = create ? f.Main.CreateWorkspaceAsync() : f.Main.SwitchWorkspaceAsync(target.Id);
        Assert.True(f.Main.IsLayoutLeaveConfirmationVisible);
        f.Main.CancelLayoutLeaveCommand.Execute(null); await leaving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(f.Data.Workspace, f.Main.CurrentWorkspace!.Id); Assert.True(page.Session.IsDirty);
        Assert.Equal(before, f.Data.DatabaseSnapshot()); Assert.Same(page, f.Main.ActiveContent);
    }

    [AvaloniaFact]
    public async Task Failed_layout_save_blocks_leave_without_poisoning_AI_gate()
    {
        await using var f = await NetworkNavigationFixture.Open(); var page = await f.Network(); await page.Session.ArrangeAsync();
        f.Data.Sql("CREATE TRIGGER fail_layout BEFORE INSERT ON node_layout BEGIN SELECT RAISE(ABORT,'layout'); END");
        f.Main.NewWorkspaceName = "second"; var leaving = f.Main.CreateWorkspaceAsync();
        Assert.True(f.Main.IsLayoutLeaveConfirmationVisible); f.Main.SaveLayoutAndContinueCommand.Execute(null);
        await leaving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(f.Data.Workspace, f.Main.CurrentWorkspace!.Id); Assert.True(page.Session.IsDirty);
        Assert.NotNull(page.Session.ErrorMessage); Assert.False(f.Main.HasPersistenceFailure); Assert.False(f.Chat.Runtime.HasPersistenceFailure);
    }

    [AvaloniaFact]
    public async Task Discard_workspace_leave_changes_only_layout_and_reentry_fits()
    {
        await using var f = await NetworkNavigationFixture.Open(); var page = await f.Network(); await page.Session.ArrangeAsync();
        page.Session.SetViewportSize(800, 600); page.Session.Viewport = new(333, 444, .4);
        var body = await f.Data.Versions.LoadSummaryVersionAsync(f.Data.First);
        f.Main.NewWorkspaceName = "second"; var leaving = f.Main.CreateWorkspaceAsync();
        Assert.True(f.Main.IsLayoutLeaveConfirmationVisible); f.Main.DiscardLayoutAndContinueCommand.Execute(null);
        await leaving.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(page.Session.IsDirty); Assert.True(page.Session.IsQuiesced);
        var retained = await f.Data.Versions.LoadSummaryVersionAsync(f.Data.First);
        Assert.Equal(body!.Header, retained!.Header); Assert.Equal(body.Markdown, retained.Markdown);
        await f.Main.SwitchWorkspaceAsync(f.Data.Workspace); var returned = Assert.IsType<NetworkViewModel>(f.Main.ActiveContent);
        Assert.Same(page.Session, returned.Session); Assert.False(page.Session.IsQuiesced);
        Assert.True(page.Session.IsFitPending); page.Session.SetViewportSize(800, 600);
        Assert.Equal(0, page.Session.Viewport.CenterX); Assert.Equal(0, page.Session.Viewport.CenterY);
    }

    [AvaloniaFact]
    public async Task Inflight_arrange_cannot_make_inactive_workspace_dirty()
    {
        await using var f = await NetworkNavigationFixture.Open(); var page = await f.Network();
        var arranging = page.Session.ArrangeAsync();
        f.Main.NewWorkspaceName = "second"; var leaving = f.Main.CreateWorkspaceAsync();
        // No UI continuation has run between accepting Arrange and freezing the workspace.
        Assert.True(page.Session.IsQuiesced);
        await Task.WhenAll(arranging, leaving).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(page.Session.IsDirty); Assert.NotEqual(f.Data.Workspace, f.Main.CurrentWorkspace!.Id);
    }

    [AvaloniaFact]
    public async Task Saved_snapshot_with_later_arrange_requires_another_leave_choice()
    {
        NetworkStoreProxy control = null!;
        await using var f = await NetworkNavigationFixture.Open(store => NetworkStoreProxy.Wrap(store, out control));
        var page = await f.Network(); await page.Session.ArrangeAsync(); control.HoldSave = true;
        f.Main.NewWorkspaceName = "second"; var leaving = f.Main.CreateWorkspaceAsync();
        f.Main.SaveLayoutAndContinueCommand.Execute(null); await control.SaveEntered.Task;
        await page.Session.ArrangeAsync(); var laterRevision = page.Session.Revision;
        control.SaveRelease.SetResult(); await WaitUi(() => f.Main.IsLayoutLeaveConfirmationVisible);
        Assert.True(page.Session.IsDirty); Assert.Equal(laterRevision, page.Session.Revision);
        f.Main.CancelLayoutLeaveCommand.Execute(null); await leaving;
        Assert.Equal(f.Data.Workspace, f.Main.CurrentWorkspace!.Id); Assert.True(page.Session.IsDirty);
    }

    internal static async Task WaitUi(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task Body_back_reuses_session_camera_selection_and_dirty_layout()
    {
        await using var f = await NetworkNavigationFixture.Open();
        var page = await f.Network(); var session = page.Session;
        session.SetViewportSize(800, 600); await session.ArrangeAsync();
        session.Viewport = new(321, -123, .7); page.SelectedNode = Assert.Single(page.Nodes);
        page.Search = "First";
        Assert.False((await f.Data.Versions.LoadSummaryReadingAsync(f.Data.First))!.State.IsRead);
        await page.OpenSummaryCommand.ExecuteAsync(null); Assert.IsType<SummaryViewModel>(f.Main.ActiveContent);
        await f.Main.GoBackAsync(); var restored = Assert.IsType<NetworkViewModel>(f.Main.ActiveContent);
        Assert.NotSame(page, restored); Assert.Same(session, restored.Session);
        Assert.Equal(new NetworkViewport(321, -123, .7), session.Viewport);
        Assert.Equal(f.Data.Node, session.SelectedNodeId); Assert.True(session.IsDirty);
        Assert.Single(f.Main.ContentStack);
    }

    // Fitting against a detached view's old width clips real node/title rectangles after entry.
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Genuine_reentry_after_offscreen_resize_fits_current_canvas_without_changing_layout(bool workspace, bool dirty)
    {
        await using var f = await NetworkNavigationFixture.Open();
        await SeedWideNetwork(f);
        var other = await f.Data.Store.CreateAsync("other");
        await f.Main.InitializeAsync(); await f.Main.SwitchWorkspaceAsync(f.Data.Workspace);
        var page = await f.Network(); var session = page.Session;
        var window = new Window { Content = new MainView { DataContext = f.Main }, Width = 1500, Height = 1000 };
        window.Show(); Layout();
        try
        {
            if (dirty) { await session.ArrangeAsync(); Layout(); }
            var positions = session.Positions; var revision = session.Revision;
            var initialWidth = Surface(window).Bounds.Width;
            if (workspace) await f.Main.SwitchWorkspaceAsync(other.Id);
            else await f.Main.ExploreCommand.ExecuteAsync(null);
            Layout(); Assert.Empty(window.GetVisualDescendants().OfType<NetworkCanvas>());
            window.Width = 720; Layout();
            if (workspace) await f.Main.SwitchWorkspaceAsync(f.Data.Workspace);
            else await f.Main.NetworkCommand.ExecuteAsync(null);
            Layout();
            Assert.Same(session, Assert.IsType<NetworkViewModel>(f.Main.ActiveContent).Session);
            Assert.True(Surface(window).Bounds.Width < initialWidth / 2);
            AssertFittedRectangles(window);
            Assert.Equal(positions, session.Positions); Assert.Equal(revision, session.Revision); Assert.Equal(dirty, session.IsDirty);
            Assert.Single(f.Main.ContentStack);

            // A second genuine entry at the same size must still fit after panning/zooming.
            var fitted = session.Viewport; session.Viewport = new(900, 800, .2); Layout();
            await f.Main.ExploreCommand.ExecuteAsync(null); Layout();
            await f.Main.NetworkCommand.ExecuteAsync(null); Layout();
            Assert.Equal(fitted, session.Viewport); AssertFittedRectangles(window);
            session.Viewport = new(-700, 600, .3); Layout();
            await f.Main.NetworkCommand.ExecuteAsync(null); Layout();
            Assert.Equal(fitted, session.Viewport); AssertFittedRectangles(window);
            Assert.Equal(positions, session.Positions); Assert.Equal(revision, session.Revision); Assert.Equal(dirty, session.IsDirty);
        }
        finally { window.Close(); }
    }

    // A deferred entry fit must never turn a summary return or visible resize into an implicit fit.
    [AvaloniaFact]
    public async Task Summary_back_after_offscreen_resize_and_visible_resize_preserve_camera_selection_and_dirty_layout()
    {
        await using var f = await NetworkNavigationFixture.Open(); await SeedWideNetwork(f);
        var page = await f.Network(); var session = page.Session;
        var window = new Window { Content = new MainView { DataContext = f.Main }, Width = 1500, Height = 1000 };
        window.Show(); Layout();
        try
        {
            await session.ArrangeAsync(); Layout();
            session.Viewport = new(321, -123, .7); session.SelectedNodeId = f.Data.Node; Layout();
            var camera = session.Viewport; var positions = session.Positions; var revision = session.Revision;
            await page.OpenSummaryCommand.ExecuteAsync(null); Layout();
            Assert.IsType<SummaryViewModel>(f.Main.ActiveContent); Assert.Empty(window.GetVisualDescendants().OfType<NetworkCanvas>());
            window.Width = 720; Layout();
            await f.Main.GoBackAsync(); Layout();
            Assert.Same(session, Assert.IsType<NetworkViewModel>(f.Main.ActiveContent).Session);
            Assert.Equal(camera, session.Viewport); Assert.Equal(f.Data.Node, session.SelectedNodeId);
            window.Width = 980; window.Height = 850; Layout();
            Assert.Equal(camera, session.Viewport); Assert.Equal(f.Data.Node, session.SelectedNodeId);
            Assert.Equal(positions, session.Positions); Assert.Equal(revision, session.Revision); Assert.True(session.IsDirty);
        }
        finally { window.Close(); }
    }

    private static async Task SeedWideNetwork(NetworkNavigationFixture f)
    {
        var conversation = (await f.Data.Store.CreateConversationAsync(f.Data.Workspace)).Id;
        await BranchStorageTests.Answer(f.Data.Store, conversation, "second question", "second answer");
        var job = (await f.Data.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), conversation, BranchStorageTests.Config(32768))).Value!;
        await f.Data.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, job.Revision);
        var second = await f.Data.Store.CompleteKnowledgeSummaryAsync(new(job.Id, job.Revision, new("A second saved summary title", "body"), "synthetic"));
        Assert.Equal(SummaryOutcomeKind.Applied, second.Kind);
        await f.Data.Store.TrySaveInitialNodePositionAsync(f.Data.Workspace, f.Data.Node, new(-500, 0));
        await f.Data.Store.TrySaveInitialNodePositionAsync(f.Data.Workspace, second.Value!.NodeId, new(500, 0));
    }

    private static void Layout() => Dispatcher.UIThread.RunJobs();
    private static NetworkCanvas Surface(Window window) => Assert.Single(window.GetVisualDescendants().OfType<NetworkCanvas>());
    private static void AssertFittedRectangles(Window window)
    {
        var surface = Surface(window);
        var nodes = surface.Children.OfType<NetworkNodeControl>().ToArray(); Assert.Equal(2, nodes.Length);
        foreach (var node in nodes)
        {
            // Translate actual visual corners, including RenderTransform, without calling the fit/projection implementation.
            foreach (var visual in new Control[] { node }.Concat(node.GetVisualDescendants().OfType<TextBlock>()))
            {
                var topLeft = visual.TranslatePoint(new Point(0, 0), surface)!.Value;
                var bottomRight = visual.TranslatePoint(new Point(visual.Bounds.Width, visual.Bounds.Height), surface)!.Value;
                Assert.InRange(topLeft.X, 47.9, surface.Bounds.Width - 47.9);
                Assert.InRange(topLeft.Y, 47.9, surface.Bounds.Height - 47.9);
                Assert.InRange(bottomRight.X, 47.9, surface.Bounds.Width - 47.9);
                Assert.InRange(bottomRight.Y, 47.9, surface.Bounds.Height - 47.9);
            }
        }
    }

}

internal sealed class NetworkNavigationFixture : IAsyncDisposable
{
    public SummaryStorageFixture Data { get; private init; } = null!;
    public RootViewModel Root { get; private set; } = null!;
    public MainViewModel Main => Assert.IsType<MainViewModel>(Root.Content);
    public DesktopChatSession Chat { get; private set; } = null!;
    public GenerationCoordinatorTests.ControlledTransport Transport { get; } = new();
    public static async Task<NetworkNavigationFixture> Open(Func<Mindweave.Infrastructure.Storage.SqliteWorkspaceStore, IWorkspaceStore>? wrap = null)
    {
        var f = new NetworkNavigationFixture { Data = await SummaryStorageFixture.Open() };
        f.Root = new(() => Task.FromResult(wrap?.Invoke(f.Data.Store) ?? f.Data.Store), new DesktopActions(f.Data.Paths.DataDirectory, () => { }), f.Data.Paths.DataDirectory,
            store => f.Chat = new DesktopChatSession((IChatStore)store, f.Transport, new GenerationCoordinatorTests.MemoryVault()));
        await f.Root.InitializeAsync(); return f;
    }
    public async Task<NetworkViewModel> Network()
    { await Main.NavigateAsync(new(MainArea.Network)); return Assert.IsType<NetworkViewModel>(Main.ActiveContent); }
    public async ValueTask DisposeAsync()
    {
        Transport.Close();
        Main.DetachObservers(); await Chat.DisposeAsync(); await Data.DisposeAsync();
    }
}
