using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The pointer on the network: a click selects, a double click opens, dragging empty canvas pans, and no dot is ever
/// moved by hand (requirements v0.25 §7.4: positions are computed).
/// </summary>
public class SummaryNetworkPointerTests
{
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
        Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);

        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        Assert.Equal(before, Positions(network));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dragging_empty_canvas_pans_and_moves_no_dot()
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

        Assert.Equal(viewport.CenterX - (100 / viewport.Zoom), canvas.Current.CenterX, 6);
        Assert.Equal(viewport.CenterY - (50 / viewport.Zoom), canvas.Current.CenterY, 6);
        Assert.Equal(before, Positions(network));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_drag_that_starts_on_a_dot_selects_it_and_moves_neither_dots_nor_view()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var node = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var before = Positions(network);
        var viewport = canvas.Current;

        Drag(window, canvas, canvas.ToScreen(node.X, node.Y), new Vector(60, 40));

        Assert.Equal(node.Id, network.Selected?.Id);
        Assert.Equal(before, Positions(network));
        Assert.Equal(viewport, canvas.Current);
        window.Close();
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
}
