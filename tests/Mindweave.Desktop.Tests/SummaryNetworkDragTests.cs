using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Manually dragging a node (requirement §7.4, architecture §11.4): the node and its edges follow the pointer, and
/// the end of a drag leaves an unsaved layout exactly as "arrange" does — the same save, the same leave prompt, the
/// same "don't save". Clicks, double clicks, small jitter and panning empty canvas stay as they were.
/// </summary>
public class SummaryNetworkDragTests
{
    [AvaloniaFact]
    public async Task Dragging_a_node_moves_it_live_and_its_end_leaves_the_layout_unsaved()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var leaf = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var viewport = canvas.Current;
        var start = canvas.ToScreen(leaf.X, leaf.Y);
        Point InWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;

        window.MouseMove(InWindow(start));
        window.MouseDown(InWindow(start), MouseButton.Left);
        window.MouseMove(InWindow(start + new Vector(30, 20)));
        window.MouseMove(InWindow(start + new Vector(60, 40)));

        // Mid-drag: the node, its label and its edge follow the pointer; nothing is unsaved yet, nothing stored moves.
        Assert.Equal(leaf.Id, canvas.Dragging);
        Assert.Same(SummaryNetworkCanvas.GrabCursor, canvas.Cursor);
        var lifted = canvas.PaintNodes().Single(p => p.Node.Id == leaf.Id);
        Assert.True(lifted.Lifted);
        AssertNear(start + new Vector(60, 40), lifted.Center);
        Assert.True(lifted.LabelBounds.X > lifted.Center.X);
        Assert.Contains(canvas.PaintEdges(), e => e.Edge.To == leaf.Id && e.To == lifted.Center);
        Assert.False(network.IsDirty);
        Assert.Equal((leaf.X, leaf.Y), (network.Nodes.Single(n => n.Id == leaf.Id).X, network.Nodes.Single(n => n.Id == leaf.Id).Y));

        window.MouseUp(InWindow(start + new Vector(60, 40)), MouseButton.Left);

        var moved = network.Nodes.Single(n => n.Id == leaf.Id);
        Assert.Equal(leaf.X + (60 / viewport.Zoom), moved.X, 6);
        Assert.Equal(leaf.Y + (40 / viewport.Zoom), moved.Y, 6);
        Assert.Contains(network.Edges, e => e.To == leaf.Id && (e.ToX, e.ToY) == (moved.X, moved.Y));
        Assert.Null(canvas.Dragging);
        Assert.False(canvas.PaintNodes().Single(p => p.Node.Id == leaf.Id).Lifted);
        Assert.Same(SummaryNetworkCanvas.HandCursor, canvas.Cursor);

        // The same unsaved state as "arrange": the accent save button, Ctrl+S / save enabled, the viewport untouched.
        Assert.True(network.IsDirty);
        Assert.Equal(UiText.SaveLayoutUnsaved, network.SaveLayoutLabel);
        Assert.True(network.SaveLayoutCommand.CanExecute(null));
        Assert.Equal(viewport, canvas.Current);
        Assert.Equal(leaf.Id, network.Selected?.Id);
        Assert.NotEqual(new NodePosition(moved.X, moved.Y), (await Layout(app))[leaf.Id]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_saved_drag_survives_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N"));
        SummaryNodeId id;
        NodePosition dragged;
        await using (var app = DesktopHarness.Create(directory: dir))
        {
            var window = app.ShowWindow(1100, 720);
            await SummaryNetworkScenario.SeedAsync(app);
            var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
            var canvas = CanvasOf(window);
            var node = network.Nodes.Single(n => n.ShortTitle == "火箭号速度");
            id = node.Id;
            Drag(window, canvas, canvas.ToScreen(node.X, node.Y), new Vector(-50, 70));
            var moved = network.Nodes.Single(n => n.Id == id);
            dragged = new NodePosition(moved.X, moved.Y);
            Assert.True(network.IsDirty);

            await network.SaveLayoutCommand.ExecuteAsync(null);
            Assert.False(network.IsDirty);
            Assert.Equal(dragged, (await Layout(app))[id]);
            window.Close();
        }

        await using var restarted = DesktopHarness.Create(directory: dir);
        var again = restarted.ShowWindow(1100, 720);
        await restarted.StartAsync();
        var reopened = await SummaryNetworkScenario.OpenSummaryNetworkAsync(restarted, again);
        var back = reopened.Nodes.Single(n => n.Id == id);
        Assert.Equal(dragged, new NodePosition(back.X, back.Y));
        Assert.False(reopened.IsDirty);
        again.Close();
    }

    [AvaloniaFact]
    public async Task Leaving_after_a_drag_asks_first_and_dont_save_puts_the_node_back()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var trains = app.Workbench.CurrentWorkspace!;
        var other = await app.Services.GetRequiredService<Application.Workspaces.WorkspaceService>().CreateAsync(WorkspaceName.FromStored("摄影"), CancellationToken.None);
        app.Workbench.Workspaces.Add(other);
        var canvas = CanvasOf(window);
        var saved = await Layout(app);
        var node = network.Nodes.Single(n => n.ShortTitle == "铁轨");

        Drag(window, canvas, canvas.ToScreen(node.X, node.Y), new Vector(80, -30));
        Assert.True(network.IsDirty);

        app.ShellPage.SelectedWorkspace = other;
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        app.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        Assert.Equal(other.Id, app.Workbench.CurrentWorkspace!.Id);
        Assert.Equal(saved, await Layout(app, trains.Id));

        app.ShellPage.SelectedWorkspace = trains;
        app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
        var reopened = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
        await reopened.Loaded;
        Assert.False(reopened.IsDirty);
        await UiWait.UntilAsync(() => reopened.Nodes.Single(n => n.Id == node.Id) is var n && new NodePosition(n.X, n.Y) == saved[node.Id]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dont_save_on_the_page_itself_reverts_the_dragged_node()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var node = network.Nodes.Single(n => n.ShortTitle == "铁轨");

        Drag(window, canvas, canvas.ToScreen(node.X, node.Y), new Vector(40, 40));
        Assert.True(network.IsDirty);
        network.Session.DiscardChanges();

        await UiWait.UntilAsync(() => network.Nodes.Single(n => n.Id == node.Id) is var n && (n.X, n.Y) == (node.X, node.Y));
        Assert.False(network.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_click_selects_a_double_click_opens_and_neither_moves_anything()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var node = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var before = Positions(network);
        var at = canvas.TranslatePoint(canvas.ToScreen(node.X, node.Y), window)!.Value;

        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Equal(node.Id, network.Selected?.Id);
        Assert.Equal(before, Positions(network));
        Assert.False(network.IsDirty);
        Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);

        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        Assert.False(network.IsDirty);
        Assert.Equal(before, Positions(network));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pointer_jitter_below_the_threshold_is_a_click_not_a_drag()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var node = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var before = Positions(network);
        var start = canvas.ToScreen(node.X, node.Y);
        Point InWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;

        window.MouseMove(InWindow(start));
        window.MouseDown(InWindow(start), MouseButton.Left);
        window.MouseMove(InWindow(start + new Vector(2, 1)));
        Assert.Null(canvas.Dragging);
        window.MouseMove(InWindow(start + new Vector(-1, 2)));
        window.MouseUp(InWindow(start + new Vector(-1, 2)), MouseButton.Left);

        Assert.Null(canvas.Dragging);
        Assert.Equal(before, Positions(network));
        Assert.False(network.IsDirty);
        Assert.Equal(node.Id, network.Selected?.Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dragging_empty_canvas_still_pans_and_leaves_nothing_unsaved()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var before = Positions(network);
        var viewport = canvas.Current;
        var start = new Point(12, 12);
        Assert.Null(canvas.HitTest(start));

        Drag(window, canvas, start, new Vector(100, 50));

        Assert.Null(canvas.Dragging);
        Assert.Equal(viewport.CenterX - (100 / viewport.Zoom), canvas.Current.CenterX, 6);
        Assert.Equal(viewport.CenterY - (50 / viewport.Zoom), canvas.Current.CenterY, 6);
        Assert.Equal(before, Positions(network));
        Assert.False(network.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dragging_while_a_save_runs_stays_unsaved_afterwards()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var first = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        Drag(window, canvas, canvas.ToScreen(first.X, first.Y), new Vector(40, 0));
        var saving = network.SaveLayoutCommand.ExecuteAsync(null);

        // The session moves a node between the snapshot and the save's completion (the canvas calls the same method).
        var second = network.Nodes.Single(n => n.ShortTitle == "火箭号速度");
        network.MoveNode(second.Id, second.X + 30, second.Y);
        await saving;

        Assert.True(network.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Moving_an_unknown_node_or_to_the_same_place_changes_nothing()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var node = network.Nodes[0];
        var revision = network.Session.Revision;

        Assert.False(network.Session.Move(SummaryNodeId.New(), new NodePosition(1, 2)));
        Assert.False(network.Session.Move(node.Id, new NodePosition(node.X, node.Y)));
        Assert.Equal(revision, network.Session.Revision);
        Assert.False(network.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task After_a_drag_the_save_button_is_the_one_primary_and_open_summary_steps_back()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var node = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        string[] Primaries()
        {
            window.UpdateLayout();
            return [.. window.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
                .Where(b => b.IsEffectivelyVisible && b.Classes.Contains("primary")).Select(b => b.Name ?? string.Empty)];
        }

        Drag(window, canvas, canvas.ToScreen(node.X, node.Y), new Vector(50, 30));
        Assert.Equal(node.Id, network.Selected?.Id);
        Assert.True(network.IsDirty);
        Assert.Equal(["SaveLayoutButton"], Primaries());
        var open = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "OpenSelectedButton");
        Assert.True(open.IsEffectivelyVisible && open.Classes.Contains("secondary"));

        await network.SaveLayoutCommand.ExecuteAsync(null);
        Assert.Equal(["OpenSelectedButton"], Primaries());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Scrolling_or_zooming_mid_drag_keeps_the_node_under_the_pointer()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var leaf = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var start = canvas.ToScreen(leaf.X, leaf.Y);
        var grip = start + new Vector(3, -2);
        var held = grip + new Vector(40, 30);
        Point InWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;
        Point Lifted() => canvas.PaintNodes().Single(p => p.Node.Id == leaf.Id).Center;

        window.MouseMove(InWindow(grip));
        window.MouseDown(InWindow(grip), MouseButton.Left);
        window.MouseMove(InWindow(grip + new Vector(20, 15)));
        window.MouseMove(InWindow(held));
        Assert.Equal(leaf.Id, canvas.Dragging);
        var viewport = canvas.Current;
        AssertNear(held - (grip - start), Lifted());

        // A two-finger scroll pans the view and a Ctrl+wheel zooms it; the held node stays where the pointer is.
        window.MouseWheel(InWindow(held), new Vector(0, -2));
        Assert.NotEqual(viewport, canvas.Current);
        AssertNear(held - (grip - start), Lifted());
        window.MouseWheel(InWindow(held), new Vector(0, 1), RawInputModifiers.Control);
        Assert.NotEqual(viewport.Zoom, canvas.Current.Zoom);
        var zoom = canvas.Current.Zoom;
        Assert.Equal(leaf.Id, canvas.Dragging);
        AssertNear(held - ((grip - start) * (zoom / viewport.Zoom)), Lifted());

        window.MouseUp(InWindow(held), MouseButton.Left);
        var moved = network.Nodes.Single(n => n.Id == leaf.Id);
        var (heldX, heldY) = canvas.ToCanvas(held);
        var (gripX, gripY) = (grip.X - start.X, grip.Y - start.Y);
        Assert.Equal(heldX - (gripX / viewport.Zoom), moved.X, 6);
        Assert.Equal(heldY - (gripY / viewport.Zoom), moved.Y, 6);
        Assert.True(network.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Losing_the_pointer_capture_mid_drag_puts_the_node_back_and_leaves_nothing_unsaved()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var leaf = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var start = canvas.ToScreen(leaf.X, leaf.Y);
        IPointer? pointer = null;
        canvas.AddHandler(InputElement.PointerMovedEvent, (_, e) => pointer = e.Pointer, handledEventsToo: true);
        Point InWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;

        window.MouseMove(InWindow(start));
        window.MouseDown(InWindow(start), MouseButton.Left);
        window.MouseMove(InWindow(start + new Vector(30, 20)));
        window.MouseMove(InWindow(start + new Vector(60, 40)));
        Assert.Equal(leaf.Id, canvas.Dragging);

        // A dialog or a window switch takes the capture away.
        Assert.NotNull(pointer);
        pointer.Capture(null);

        Assert.Null(canvas.Dragging);
        Assert.Equal(start, canvas.PaintNodes().Single(p => p.Node.Id == leaf.Id).Center);
        window.MouseUp(InWindow(start + new Vector(60, 40)), MouseButton.Left);
        Assert.Equal((leaf.X, leaf.Y), (network.Nodes.Single(n => n.Id == leaf.Id).X, network.Nodes.Single(n => n.Id == leaf.Id).Y));
        Assert.False(network.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Leaving_the_canvas_mid_drag_keeps_the_drag_cursor_and_the_pointer()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var leaf = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var start = canvas.ToScreen(leaf.X, leaf.Y);
        Point InWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;

        window.MouseMove(InWindow(start));
        window.MouseDown(InWindow(start), MouseButton.Left);
        window.MouseMove(InWindow(start + new Vector(30, 20)));
        Assert.Equal(leaf.Id, canvas.Dragging);
        canvas.RaiseEvent(new PointerEventArgs(
            InputElement.PointerExitedEvent, canvas, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), window, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));

        Assert.Equal(leaf.Id, canvas.Dragging);
        Assert.NotNull(canvas.Pointer);
        Assert.Equal(leaf.Id, canvas.Hovered);
        Assert.Same(SummaryNetworkCanvas.GrabCursor, canvas.Cursor);
        window.MouseUp(InWindow(start + new Vector(30, 20)), MouseButton.Left);
        Assert.True(network.IsDirty);
        window.Close();
    }

    private static void AssertNear(Point expected, Point actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }

    /// <summary>Presses at <paramref name="from"/>, moves there by <paramref name="by"/> in steps and releases.</summary>
    internal static void Drag(MainWindow window, SummaryNetworkCanvas canvas, Point from, Vector by)
    {
        Point InWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;
        window.MouseMove(InWindow(from));
        window.MouseDown(InWindow(from), MouseButton.Left);
        for (var i = 1; i <= 4; i++)
        {
            window.MouseMove(InWindow(from + (by * (i / 4.0))));
        }

        window.MouseUp(InWindow(from + by), MouseButton.Left);
    }

    private static Dictionary<SummaryNodeId, NodePosition> Positions(SummaryNetworkHomeViewModel network) =>
        network.Nodes.ToDictionary(n => n.Id, n => new NodePosition(n.X, n.Y));

    private static Task<IReadOnlyDictionary<SummaryNodeId, NodePosition>> Layout(DesktopHarness app, WorkspaceId? workspace = null) =>
        app.Services.GetRequiredService<ISummaryNetworkStore>().GetLayoutAsync(workspace ?? app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);
}
