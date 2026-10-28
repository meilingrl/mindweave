using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Mindweave.Desktop.Controls;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Application.Network;
using Mindweave.Core.Network;
using Mindweave.Desktop;
using Mindweave.Desktop.Network;
using Mindweave.Desktop.ViewModels;
using Xunit;
using static Mindweave.Tests.NetworkSessionTests;

namespace Mindweave.Tests;

public sealed class NetworkInteractionTests(ITestOutputHelper output)
{
    // Missing the real route/control tree would leave the network inaccessible to keyboard and automation.
    [AvaloniaFact]
    public async Task Real_nodes_are_accessible_and_keyboard_selects_without_opening_until_enter()
    {
        using var f = await Open();
        var nodes = f.Nodes(); Assert.Equal(2, nodes.Length);
        Assert.All(nodes, n => { Assert.True(n.Focusable); Assert.Contains("Title", AutomationProperties.GetName(n)); });
        f.Session.Viewport = new(-2000, 500, 1);
        f.Control<TextBox>("NetworkSearch").Focus();
        for (var i = 0; i < 20 && !nodes.Any(n => n.IsFocused); i++) { f.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); Layout(); }
        Assert.Contains(nodes, n => n.IsFocused); Assert.Equal(new NetworkViewport(0, 0, 1), f.Session.Viewport);
        nodes[0].Focus(); f.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); Layout();
        Assert.Equal(Id(12), f.Session.SelectedNodeId); Assert.Empty(f.Opened);
        f.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Layout(); Assert.Equal(new[] { Id(12) }, f.Opened);
    }

    // The visible semantic circle must sit at the projected world center used by true source edges.
    [AvaloniaFact]
    public async Task Visible_dot_center_matches_the_node_and_edge_world_projection()
    {
        using var f = await Open(); f.Session.Viewport = new(140, 20, 1.3); Layout();
        var surface = f.Control<Control>("NetworkSurface");
        foreach (var node in f.Nodes())
        {
            var dot = Assert.Single(node.GetVisualDescendants().OfType<Border>(), b => b.Width is > 0 and <= 32 && b.Height == b.Width);
            var center = dot.TranslatePoint(new Point(dot.Bounds.Width / 2, dot.Bounds.Height / 2), surface)!.Value;
            var position = f.Page.Nodes.Single(n => $"NetworkNode_{n.Id:N}" == node.Name).Position;
            var projected = NetworkViewportMath.ToScreen(position, f.Session.Viewport, surface.Bounds.Width, surface.Bounds.Height);
            Assert.Equal(projected.X, center.X, 5); Assert.Equal(projected.Y, center.Y, 5);
        }
    }

    // A click must expose metadata only: all real SQLite business/reading rows remain identical.
    [AvaloniaFact]
    public async Task Single_click_is_metadata_only_and_double_click_uses_existing_open_route()
    {
        await using var database = await SummaryStorageFixture.Open();
        var session = new NetworkSession(database.Workspace, database.Store);
        using var f = await Open(session); var before = database.DatabaseSnapshot();
        var node = Assert.Single(f.Nodes()); f.Click(node);
        Assert.NotNull(f.Page.SelectedNode); Assert.Empty(f.Opened); Assert.Equal(before, database.DatabaseSnapshot());
        Assert.Contains(f.Page.SelectedNode.DisplayName, f.Control<TextBox>("NetworkMetadata").Text!);
        // Two press/release pairs exercise Avalonia's actual double-tap recognizer.
        f.Click(node); Layout(); Assert.Equal(new[] { database.Node }, f.Opened);
        Assert.Equal(before, database.DatabaseSnapshot());
        f.Click(f.Control<Button>("NetworkOpen")); Assert.Equal(new[] { database.Node, database.Node }, f.Opened);
        Assert.Equal(before, database.DatabaseSnapshot());
    }

    // Wheel modifiers must select different camera operations; anchor zoom must not shift the pointed world point.
    [AvaloniaFact]
    public async Task Wheel_pan_and_modifier_or_direct_zoom_share_the_node_projection()
    {
        using var f = await Open(); var surface = f.Control<Control>("NetworkSurface");
        f.Session.Viewport = new(0, 0, 1); Layout(); var local = new Point(45, 60); var screen = surface.TranslatePoint(local, f.Window)!.Value;
        f.Window.MouseWheel(screen, new Vector(2, -3)); Layout();
        Assert.Equal(1, f.Session.Viewport.Scale); Assert.NotEqual(0, f.Session.Viewport.CenterX); Assert.NotEqual(0, f.Session.Viewport.CenterY);
        var world = NetworkViewportMath.ToWorld(new(local.X, local.Y), f.Session.Viewport, surface.Bounds.Width, surface.Bounds.Height);
        f.Window.MouseWheel(screen, new Vector(0, 1), RawInputModifiers.Control); Layout();
        var after = NetworkViewportMath.ToWorld(new(local.X, local.Y), f.Session.Viewport, surface.Bounds.Width, surface.Bounds.Height);
        Assert.Equal(world.X, after.X, 8); Assert.Equal(world.Y, after.Y, 8); Assert.True(f.Session.Viewport.Scale > 1);
        f.Control<CheckBox>("NetworkDirectZoom").IsChecked = true;
        var scale = f.Session.Viewport.Scale; f.Window.MouseWheel(screen, new Vector(0, 1)); Layout(); Assert.True(f.Session.Viewport.Scale > scale);
        var node = f.Nodes()[0]; var center = node.TranslatePoint(new Point(node.Bounds.Width / 2, node.Bounds.Height / 2), surface)!.Value;
        var expected = NetworkViewportMath.ToScreen(f.Page.Nodes[0].Position, f.Session.Viewport, surface.Bounds.Width, surface.Bounds.Height);
        Assert.Equal(expected.X, center.X, 5); Assert.Equal(expected.Y, center.Y, 5); Assert.False(f.Session.IsDirty);
    }

    // Only a blank press may acquire pan capture, and release/capture-loss must stop future moves.
    [AvaloniaFact]
    public async Task Blank_drag_pans_but_node_drag_does_not_and_release_stops_pan()
    {
        using var f = await Open(); var surface = f.Control<Control>("NetworkSurface");
        var start = surface.TranslatePoint(new Point(12, 12), f.Window)!.Value; var camera = f.Session.Viewport;
        f.Window.MouseDown(start, MouseButton.Left); f.Window.MouseMove(start + new Vector(40, 25), RawInputModifiers.LeftMouseButton); f.Window.MouseUp(start + new Vector(40, 25), MouseButton.Left); Layout();
        Assert.NotEqual(camera, f.Session.Viewport); var stopped = f.Session.Viewport;
        f.Window.MouseMove(start + new Vector(100, 100)); Layout(); Assert.Equal(stopped, f.Session.Viewport);
        f.Page.ShowAllCommand.Execute(null); Layout(); camera = f.Session.Viewport;
        var node = f.Nodes()[0]; var point = f.Center(node);
        f.Window.MouseDown(point, MouseButton.Left); f.Window.MouseMove(point + new Vector(20, 10), RawInputModifiers.LeftMouseButton); f.Window.MouseUp(point + new Vector(20, 10), MouseButton.Left); Layout();
        Assert.Equal(camera, f.Session.Viewport); Assert.False(f.Session.IsDirty);
    }

    // Save's actual button/shortcut guard must prevent duplicate IO and retain later dirty work.
    [AvaloniaFact]
    public async Task Save_is_disabled_clean_and_busy_and_shortcuts_are_scoped_to_network()
    {
        using var f = await Open(); var save = f.Control<Button>("NetworkSave");
        Assert.Equal("保存布局", AutomationProperties.GetName(save)); Assert.NotNull(ToolTip.GetTip(save)); Assert.False(save.IsEffectivelyEnabled);
        f.Click(save); f.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control); Layout(); Assert.Equal(0, f.Store!.SaveCalls);
        f.Click(f.Control<Button>("NetworkArrange")); await f.Page.ArrangeCommand.ExecutionTask!; Layout(); Assert.True(f.Session.IsDirty); Assert.True(save.IsEffectivelyEnabled);
        Assert.True(f.Control<TextBlock>("NetworkDirty").IsVisible);
        var gate = Gate<LayoutWriteResult>(); f.Store.Save = (_, _) => gate.Task;
        f.Nodes()[0].Focus(); f.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control); Layout(); Assert.Equal(1, f.Store.SaveCalls); Assert.False(save.IsEffectivelyEnabled);
        f.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Meta); Layout(); Assert.Equal(1, f.Store.SaveCalls);
        Assert.True(f.Control<ProgressBar>("NetworkSaveBusy").IsVisible);
        gate.SetResult(new(LayoutWriteStatus.Applied)); await f.Page.SaveLayoutCommand.ExecutionTask!; Layout(); Assert.False(f.Session.IsDirty);
        f.Store.Save = null; await f.Session.ArrangeAsync(); f.Nodes()[0].Focus();
        f.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Meta); await f.Page.SaveLayoutCommand.ExecutionTask!; Layout();
        Assert.Equal(2, f.Store.SaveCalls); Assert.False(f.Session.IsDirty);
        await f.Session.ArrangeAsync();
        var editor = new TextBox { AcceptsReturn = true }; f.Window.Content = editor; Layout(); editor.Focus();
        f.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control); Layout(); Assert.Equal(2, f.Store.SaveCalls);
    }

    // Search must keep all ordinary controls; fit changes only camera, while explicit arrange makes layout dirty.
    [AvaloniaFact]
    public async Task Search_keeps_every_node_and_metadata_has_full_hidden_path_and_separate_status()
    {
        using var f = await Open(); f.Control<TextBox>("NetworkSearch").Text = "Base 2"; Layout();
        Assert.Equal(2, f.Nodes().Length); Assert.Single(f.Page.Nodes, n => n.IsSearchMatch);
        f.Click(f.Control<Button>("NetworkFindNext")); Assert.Equal(Id(12), f.Session.SelectedNodeId);
        var metadata = f.Control<TextBox>("NetworkMetadata"); Assert.Contains("Source 2", metadata.Text!); Assert.Contains("hidden source", metadata.Text!);
        Assert.Contains("候选", metadata.Text!); Assert.Contains("未读", metadata.Text!); Assert.Contains("后续内容", metadata.Text!);
        Assert.True(metadata.IsReadOnly); Assert.Contains("经1个未总结会话", f.Control<Control>("NetworkEdges").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        var scale = f.Session.Viewport.Scale; f.Click(f.Control<Button>("NetworkZoomIn")); Assert.True(f.Session.Viewport.Scale >= scale);
        scale = f.Session.Viewport.Scale; f.Click(f.Control<Button>("NetworkZoomOut")); Assert.True(f.Session.Viewport.Scale < scale);
        Assert.Contains("%", f.Control<TextBlock>("NetworkScale").Text!);
        f.Click(f.Control<Button>("NetworkShowAll")); Assert.False(f.Session.IsDirty);
    }

    // View recreation must release old subscriptions, preserve camera and selection, and bound long-title geometry.
    [AvaloniaFact]
    public async Task Resize_and_detach_rebuild_preserve_camera_and_measured_node_bounds()
    {
        using var f = await Open(); f.Session.Viewport = new(35, -24, 1.2); f.Page.SelectedNode = f.Page.Nodes[1]; Layout();
        var retained = f.Session.Viewport; f.Window.Width = 550; Layout(); Assert.Equal(retained, f.Session.Viewport);
        f.Window.Content = null; Layout(); f.Window.Content = f.View; Layout(); Assert.Equal(retained, f.Session.Viewport);
        Assert.Equal(Id(12), f.Session.SelectedNodeId); Assert.Equal(2, f.Nodes().Length);
        Assert.All(f.Session.Extents.Values, e => { Assert.InRange(e.Width, 1, 200); Assert.InRange(e.Height, 1, 112); });
        var scale = retained.Scale; var surface = f.Control<Control>("NetworkSurface"); var point = surface.TranslatePoint(new Point(10, 10), f.Window)!.Value;
        f.Window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control); Layout(); Assert.Equal(scale * 1.15, f.Session.Viewport.Scale, 8);
    }

    // Empty/zero-size construction must defer fit without manufacturing nodes or NaN coordinates.
    [AvaloniaFact]
    public async Task Empty_network_has_exploration_guidance_and_zero_size_fit_is_deferred()
    {
        var session = new NetworkSession(Workspace, new ControlledStore(Snapshot())); await session.ReloadAsync(true);
        Assert.True(session.IsFitPending); using var f = await Open(session); Assert.Empty(f.Nodes());
        Assert.Contains("探索", f.Control<TextBlock>("NetworkEmpty").Text!); Assert.False(session.IsFitPending); Assert.Equal(new NetworkViewport(0, 0, 1), session.Viewport);
    }

    // A lost pan capture must stop camera motion even while the button remains held.
    [AvaloniaFact]
    public async Task Capture_loss_and_detached_old_page_stop_camera_updates()
    {
        using var f = await Open(); var surface = f.Control<Control>("NetworkSurface"); IPointer? pointer = null;
        surface.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, handledEventsToo: true);
        var start = surface.TranslatePoint(new Point(12, 12), f.Window)!.Value;
        f.Window.MouseDown(start, MouseButton.Left); Assert.NotNull(pointer);
        pointer.Capture(f.Control<Button>("NetworkShowAll")); var camera = f.Session.Viewport;
        f.Window.MouseMove(start + new Vector(80, 30), RawInputModifiers.LeftMouseButton); Layout(); Assert.Equal(camera, f.Session.Viewport);
        f.Window.MouseUp(start, MouseButton.Left);
        f.Window.Content = null; Layout();
        var other = new NetworkSession(Workspace, new ControlledStore(Snapshot(1))); await other.ReloadAsync(true);
        using var adapter = new NetworkViewModel(other, _ => Task.CompletedTask); f.View.DataContext = adapter; f.Window.Content = f.View; Layout();
        var newCamera = other.Viewport; f.Session.Zoom(1); Layout(); Assert.Equal(newCamera, other.Viewport);
        Assert.Single(f.Nodes());
    }

    // Save failure must retain dirty coordinates, show the promised recovery message, and permit a real retry.
    [AvaloniaFact]
    public async Task Save_failure_keeps_layout_and_retry_button_commits_it()
    {
        using var f = await Open(); await f.Session.ArrangeAsync(); var positions = f.Session.Positions;
        f.Store!.Save = (_, _) => throw new IOException("synthetic writer failure");
        f.Click(f.Control<Button>("NetworkSave")); await f.Page.SaveLayoutCommand.ExecutionTask!; Layout();
        Assert.True(f.Session.IsDirty); Assert.Equal(positions, f.Session.Positions);
        Assert.Contains("布局保存失败", string.Join("", f.View.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)));
        Assert.True(f.Control<Button>("NetworkSave").IsEffectivelyEnabled);
        f.Store.Save = null; f.Click(f.Control<Button>("NetworkSave")); await f.Page.SaveLayoutCommand.ExecutionTask!; Layout();
        Assert.False(f.Session.IsDirty); Assert.Equal(positions, f.Store.Current.SavedPositions);
    }

    // Wrapped toolbar and two-line titles must remain within the measured 200x112 world extent.
    [AvaloniaFact]
    public async Task Long_titles_stay_bounded_and_narrow_toolbar_wraps_without_hiding_metadata()
    {
        using var f = await Open(); var graph = f.Store!.Current.Graph;
        var title = string.Concat(Enumerable.Repeat("很长的完整总结标题", 30));
        f.Store.Current = f.Store.Current with { Graph = graph with { DataVersion = "long title", Nodes = [graph.Nodes[0] with { Metadata = graph.Nodes[0].Metadata with { DisplayName = title } }, graph.Nodes[1]] } };
        await f.Session.ReloadAsync(); f.Window.Width = 340; Layout();
        var node = f.Nodes()[0]; node.Focus(); Layout();
        Assert.Contains(title, f.Control<TextBox>("NetworkMetadata").Text!);
        var heading = Assert.Single(node.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == title);
        Assert.InRange(heading.Bounds.Height, 1, 36); Assert.InRange(heading.Bounds.Width, 1, 200);
        Assert.Equal(new NodeExtent(200, 112), f.Session.Extents[f.Page.Nodes[0].Id]);
        var toolbar = f.Control<WrapPanel>("NetworkToolbar"); Assert.True(toolbar.Bounds.Height > 70);
        Assert.All(toolbar.Children, child => Assert.True(child.Bounds.Right <= toolbar.Bounds.Width + 1));
        Assert.True(f.Control<Button>("NetworkSave").IsEffectivelyVisible);
    }

    // Moving the camera must redraw real source edges, not leave cached strokes at their old screen positions.
    [AvaloniaFact]
    public async Task Rendered_edges_follow_camera_and_hidden_path_label_midpoint()
    {
        using var f = await Open(); var surface = f.Control<Control>("NetworkSurface");
        f.Store!.Current = f.Store.Current with { SavedPositions = Point(Id(11), 0, 0).Add(Id(12), new(600, 0)) };
        await f.Session.ReloadAsync(); f.Session.Viewport = new(300, 0, 1); Layout();
        var edgeLayer = f.Control<NetworkEdges>("NetworkEdges");
        using var before = f.Window.CaptureRenderedFrame()!;
        var anchor = surface.TranslatePoint(new Point(surface.Bounds.Width / 2 - 140, surface.Bounds.Height / 2), f.Window)!.Value;
        Assert.True(HasEdgePixels(before, anchor));
        f.Session.Pan(0, 45); Layout(); using var after = f.Window.CaptureRenderedFrame()!;
        Assert.True(HasEdgePixels(after, anchor + new Vector(0, 45))); Assert.False(HasEdgePixels(after, anchor));
        var label = Assert.Single(edgeLayer.GetVisualDescendants().OfType<TextBlock>());
        Assert.InRange(Math.Abs(label.Bounds.Center.Y - (surface.Bounds.Height / 2 + 45)), 0, 1);
        var evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.superpowers/sdd/s5-network-plan/task-5-evidence"));
        Directory.CreateDirectory(evidence); after.Save(Path.Combine(evidence, "headless-network.png"), PngBitmapEncoderOptions.Default);
        f.Store.Current = f.Store.Current with { Graph = f.Store.Current.Graph with { Edges = [] } };
        await f.Session.ReloadAsync(); Layout(); using var unrelated = f.Window.CaptureRenderedFrame()!;
        Assert.False(HasEdgePixels(unrelated, anchor + new Vector(0, 45)));
        Assert.Empty(f.Control<NetworkEdges>("NetworkEdges").GetVisualDescendants().OfType<TextBlock>());
    }

    private static bool HasEdgePixels(WriteableBitmap bitmap, Point point)
    {
        var rect = new PixelRect((int)point.X - 5, (int)point.Y - 2, 11, 5); var buffer = Marshal.AllocHGlobal(11 * 5 * 4);
        try
        {
            bitmap.CopyPixels(rect, buffer, 11 * 5 * 4, 11 * 4); var bytes = new byte[11 * 5 * 4]; Marshal.Copy(buffer, bytes, 0, bytes.Length);
            for (var i = 0; i < bytes.Length; i += 4)
                if (Math.Min(bytes[i], bytes[i + 2]) is > 110 and < 200 && bytes[i + 1] > bytes[i] && bytes[i + 1] > bytes[i + 2]) return true;
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // Synthetic control/render timing measures this complete tree, never a native performance acceptance claim.
    [AvaloniaFact]
    public async Task Synthetic_500_nodes_build_and_render_complete_ordinary_control_tree()
    {
        var timer = Stopwatch.StartNew(); using var f = await Open(count: 500); Layout();
        Assert.Equal(500, f.Nodes().Length); f.Window.CaptureRenderedFrame();
        output.WriteLine($"TASK5_SYNTHETIC_500_CONTROL_BUILD_RENDER_MS={timer.Elapsed.TotalMilliseconds:F1}");
    }

    private static void Layout() { Dispatcher.UIThread.RunJobs(); }
    private static async Task<Fixture> Open(NetworkSession? session = null, int count = 2)
    {
        ControlledStore? store = null;
        if (session is null)
        {
            var snapshot = Snapshot(Enumerable.Range(1, count).ToArray());
            if (count >= 2) snapshot = snapshot with { Graph = snapshot.Graph with { Nodes = snapshot.Graph.Nodes.Select(n => n.Metadata.Id == Id(12) ? n with { RootConversationId = Id(1) } : n).ToImmutableArray(), Edges = [new(Id(11), Id(12), [new(Id(1), "Source 1"), new(Id(9000), "hidden source"), new(Id(2), "Source 2")])] } };
            snapshot = snapshot with { SavedPositions = Enumerable.Range(1, count).ToImmutableDictionary(i => Id(i + 10), i => new NodePosition((i - 1) % 25 * 280, (i - 1) / 25 * 160)) };
            store = new(snapshot); session = new(Workspace, store);
        }
        await session.ReloadAsync(true); Assert.Null(session.ErrorMessage); return new Fixture(session, store);
    }
    private sealed class Fixture : IDisposable
    {
        public readonly NetworkSession Session; public readonly ControlledStore? Store; public readonly NetworkViewModel Page;
        public readonly Control View; public readonly Window Window; public readonly List<Guid> Opened = [];
        public Fixture(NetworkSession session, ControlledStore? store)
        {
            Session = session; Store = store; Page = new(session, id => { Opened.Add(id); return Task.CompletedTask; });
            View = new ViewLocator().Build(Page)!; View.DataContext = Page;
            Window = new() { Content = View, Width = 1000, Height = 760 }; Window.Show(); Layout();
        }
        public T Control<T>(string name) where T : Control => Assert.Single(View.GetVisualDescendants().OfType<T>(), c => c.Name == name);
        public Button[] Nodes() => View.GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith("NetworkNode_") == true).ToArray();
        public Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;
        public void Click(Control control) { var point = Center(control); Window.MouseDown(point, MouseButton.Left); Window.MouseUp(point, MouseButton.Left); Layout(); }
        public void Dispose() { Window.Close(); Page.Dispose(); }
    }
}
