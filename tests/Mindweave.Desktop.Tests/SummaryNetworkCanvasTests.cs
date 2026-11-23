using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The network's drawing (S7, the confirmed sample's visual language): labels hidden when zoomed far out, the selected
/// dot's source trail, search rings and hover. Drawing parameters come from the same list the render pass uses; shapes are also checked
/// on rendered pixels.
/// </summary>
public class SummaryNetworkCanvasTests
{

    [AvaloniaFact]
    public async Task Labels_are_hidden_at_045_and_below_except_the_selected_and_matching_ones()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var viewport = canvas.Current;

        network.Session.Viewport = viewport with { Zoom = 0.5 };
        Assert.All(canvas.PaintNodes(), p => Assert.True(p.LabelVisible));
        Assert.Equal(SummaryNetworkLayout.LabelEm * 0.5, canvas.PaintNodes()[0].LabelSize, 3);

        network.Session.Viewport = viewport with { Zoom = SummaryNetworkCanvas.LabelHideScale };
        Assert.All(canvas.PaintNodes(), p => Assert.False(p.LabelVisible));

        var selected = network.Nodes.Single(n => n.ShortTitle == "火车起源");
        var matched = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        network.Select(selected.Id);
        network.SearchText = "铁轨";
        network.Session.Viewport = viewport with { Zoom = SummaryNetworkCanvas.LabelHideScale };
        var paints = canvas.PaintNodes();
        Assert.True(paints.Single(p => p.Node.Id == selected.Id).LabelVisible);
        Assert.True(paints.Single(p => p.Node.Id == matched.Id).LabelVisible);
        Assert.False(paints.Single(p => p.Node.ShortTitle == "火箭号速度").LabelVisible);
        Assert.Equal(13, SummaryNetworkGeometry.LabelSize(1.2));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Selecting_a_node_highlights_its_whole_source_trail_to_the_root()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var root = network.Nodes.Single(n => n.ShortTitle == "火车起源");
        var leaf = network.Nodes.Single(n => n.ShortTitle == "火箭号速度");
        var sibling = network.Nodes.Single(n => n.ShortTitle == "铁轨");

        Assert.Equal(EdgeState.Normal, canvas.PaintEdges().Single(e => e.Edge.To == leaf.Id).State);

        network.Select(leaf.Id);

        var middle = network.Nodes.Single(n => !n.HasSummary);
        Assert.Equal(new HashSet<Core.Summaries.SummaryNodeId> { leaf.Id, middle.Id, root.Id }, network.Trail);
        var nodes = canvas.PaintNodes();
        Assert.True(nodes.Single(p => p.Node.Id == root.Id).OnTrail);
        Assert.False(nodes.Single(p => p.Node.Id == sibling.Id).OnTrail);
        var leafPaint = nodes.Single(p => p.Node.Id == leaf.Id);
        Assert.Equal(NodeRing.Selected, leafPaint.Ring);
        Assert.True(leafPaint.Halo);
        var edges = canvas.PaintEdges();
        var trailEdge = edges.Single(e => e.Edge.To == leaf.Id);
        Assert.Equal(EdgeState.Trail, trailEdge.State);
        Assert.Equal(EdgeState.Trail, edges.Single(e => e.Edge.To == middle.Id).State);
        Assert.Equal(EdgeState.Normal, edges.Single(e => e.Edge.To == sibling.Id).State);

        // The details spell the trail out, root first, including the unsummarized conversation in between.
        var path = network.Selected!.Path.Split(" → ");
        Assert.Equal(3, path.Length);
        Assert.Equal("火车起源", path[0]);
        Assert.Equal("火箭号速度", path[^1]);

        network.Select(null);
        Assert.Empty(network.Trail);
        Assert.All(canvas.PaintEdges(), e => Assert.NotEqual(EdgeState.Trail, e.State));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_matches_get_the_amber_ring_and_the_selection_ring_wins()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var rails = network.Nodes.Single(n => n.ShortTitle == "铁轨");

        network.SearchText = "铁";
        var paints = canvas.PaintNodes();
        Assert.Equal(NodeRing.Match, paints.Single(p => p.Node.Id == rails.Id).Ring);
        Assert.All(paints.Where(p => p.Node.Id != rails.Id && !p.Node.DisplayName.Contains('铁', StringComparison.Ordinal)), p => Assert.Equal(NodeRing.None, p.Ring));

        network.FindCommand.Execute(null);
        Assert.Equal(NodeRing.Selected, canvas.PaintNodes().Single(p => p.Node.Id == rails.Id).Ring);

        network.SearchText = string.Empty;
        Assert.Empty(network.Matches);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hovering_a_node_enlarges_it_brightens_its_edges_and_the_pull_stops_when_idle()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var root = network.Nodes.Single(n => n.ShortTitle == "火车起源");
        var before = canvas.PaintNodes().Single(p => p.Node.Id == root.Id);

        canvas.SetPointer(canvas.ToScreen(root.X, root.Y) + new Vector(3, 0));

        Assert.Equal(root.Id, canvas.Hovered);
        var hovered = canvas.PaintNodes().Single(p => p.Node.Id == root.Id);
        Assert.Equal(before.Radius + SummaryNetworkCanvas.HoverGrowth, hovered.Radius);
        Assert.True(hovered.Halo && hovered.LabelVisible);
        Assert.All(canvas.PaintEdges(), e => Assert.Equal(e.Edge.From == root.Id ? EdgeState.Near : EdgeState.Normal, e.State));
        Assert.Same(SummaryNetworkCanvas.HandCursor, canvas.Cursor);

        // Decorative only: at most 1.5 px, never past 90 px, and no layout position changes.
        Assert.Equal(1.5, SummaryNetworkGeometry.Attraction(new Point(0, 0), new Point(45, 0)).Length, 3);
        Assert.Equal(default, SummaryNetworkGeometry.Attraction(new Point(0, 0), new Point(95, 0)));
        canvas.SetPointer(null);
        Assert.Same(Cursor.Default, canvas.Cursor);
        for (var i = 0; i < 500 && canvas.IsMoving; i++)
        {
            canvas.StepMotion();
        }

        Assert.False(canvas.IsMoving);
        Assert.Null(canvas.Hovered);
        Assert.Equal(before.Center, canvas.PaintNodes().Single(p => p.Node.Id == root.Id).Center);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_source_path_names_every_conversation_from_the_root_and_marks_the_unsummarized_ones()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var summaries = app.Services.GetRequiredService<SummaryService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;

        // A second exploration whose root and middle conversation have no summary: root → A → B, only B summarized.
        var photos = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "摄影是怎么发明的？");
        var plates = await SummaryNetworkScenario.BranchAsync(app, conversations, photos, "银版法怎么做？");
        var mercury = await SummaryNetworkScenario.BranchAsync(app, conversations, plates, "为什么用汞蒸气？");
        app.Client.Knowledge.Replies.Enqueue("标题：汞蒸气显影\n短名：汞蒸气\n\n正文");
        Assert.True((await summaries.SummarizeAsync(mercury, CancellationToken.None)).Accepted);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count(n => n.HasSummary) == 4);
        var titles = (await conversations.ListAsync(workspace, CancellationToken.None))
            .ToDictionary(c => c.Conversation.Id, c => UiText.ConversationTitle(c.Conversation, c.FirstQuestion, c.Quote));

        // Direct: the root's summary, then the node.
        network.Select(network.Nodes.Single(n => n.ShortTitle == "铁轨").Id);
        Assert.Equal([("火车起源", true), ("铁轨", true)], Steps(network));

        // Through an unsummarized conversation below a summarized ancestor.
        network.Select(network.Nodes.Single(n => n.ShortTitle == "火箭号速度").Id);
        var via = Steps(network);
        Assert.Equal(3, via.Count);
        Assert.Equal(("火车起源", true), via[0]);
        Assert.False(via[1].Summarized);
        Assert.Equal(("火箭号速度", true), via[2]);

        // No summarized ancestor at all: the whole chain still shows, root first, every conversation marked.
        network.Select(network.Nodes.Single(n => n.ShortTitle == "汞蒸气").Id);
        Assert.Equal([(titles[photos], false), (titles[plates], false), ("汞蒸气", true)], Steps(network));
        Assert.Equal(
            UiText.Unsummarized(titles[photos]) + " → " + UiText.Unsummarized(titles[plates]) + " → 汞蒸气",
            network.Selected!.Path);
        window.Close();

        static List<(string Name, bool Summarized)> Steps(SummaryNetworkHomeViewModel network) =>
            [.. network.Selected!.Steps.Select(s => (s.Name, s.Summarized))];
    }

    [AvaloniaFact]
    public void Drawn_labels_never_need_more_room_than_the_layout_reserved_including_the_candidate_mark()
    {
        Assert.True(SummaryNetworkLayout.LabelWidth(SummaryNetworkLayout.Label("标准轨距", true)) > SummaryNetworkLayout.LabelWidth("标准轨距"));

        // The reserved width is an estimate for the fonts of the supported platforms. Linux CI's fallback fonts draw
        // "MW-C12 ↻" wider (23.1 > 21.5 at the smallest zoom, 2026-11-13); Linux is not a supported platform yet
        // (architecture §1), so the measured check runs on macOS and Windows only, as in SourceHighlightThemeTests.
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Measures text in the fonts of the supported platforms.");
        var labels = new[] { "标准轨距 ↻", "火车的发明", "动力分散", "受电弓", "1435 毫米", "Rocket 号", "MW-C12 ↻", "Stephenson" };
        foreach (var zoom in new[] { Viewport.MinZoom, 0.46, 0.65, 0.8, 0.93, 1.2, Viewport.MaxZoom })
        {
            foreach (var label in labels)
            {
                foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold })
                {
                    var drawn = SummaryNetworkCanvas.Text(label, SummaryNetworkGeometry.LabelSize(zoom), Brushes.Black, weight).Width;
                    Assert.True(drawn <= SummaryNetworkLayout.LabelWidth(label) * zoom + 0.5, $"{label} at {zoom}: {drawn} > {SummaryNetworkLayout.LabelWidth(label) * zoom}");
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task At_the_fitted_zoom_no_label_overlaps_another_label_or_dot()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var network = await SeedThreeChildrenAsync(app, window);
        var canvas = CanvasOf(window);
        network.ShowAllCommand.Execute(null);
        window.UpdateLayout();
        var zoom = canvas.Current.Zoom;

        var paints = canvas.PaintNodes();
        Assert.All(paints, p =>
        {
            Assert.True(p.LabelVisible);
            Assert.True(p.LabelBounds.Width <= (p.Node.LabelWidth * zoom) + 0.5, $"{p.Label}: {p.LabelBounds.Width} > {p.Node.LabelWidth * zoom}");
        });
        foreach (var a in paints)
        {
            foreach (var b in paints.Where(b => b != a))
            {
                Assert.False(a.LabelBounds.Intersects(b.LabelBounds), $"labels {a.Label} and {b.Label} overlap at zoom {zoom}");
                Assert.False(a.LabelBounds.Intersects(Dot(b)), $"label {a.Label} covers the dot of {b.Label} at zoom {zoom}");
            }
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_highlighted_trail_stays_visible()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var network = await SeedThreeChildrenAsync(app, window);
        var canvas = CanvasOf(window);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            window.RequestedThemeVariant = theme;
            network.ShowAllCommand.Execute(null);

            // A child of the root: neither its own label nor a backdrop covers the line back to the root.
            var root = network.Nodes.Single(n => n.IsRoot);
            var child = network.Nodes.First(n => n.Parent == root.Id && n.HasSummary);
            network.Select(child.Id);
            window.UpdateLayout();
            var paint = canvas.PaintNodes().Single(p => p.Node.Id == child.Id);
            var edge = canvas.PaintEdges().Single(e => e.Edge.To == child.Id);
            Assert.Equal(EdgeState.Trail, edge.State);
            Assert.False(SummaryNetworkGeometry.Crosses(edge.From, edge.To, paint.LabelBounds), "the label is drawn across its own edge");

            // Every pixel of the edge between the two halos shows the trail colour: nothing is drawn over it.
            using var frame = window.CaptureRenderedFrame()!;
            var trail = ResourceColor(window, "SummaryNetworkEdgeTrail");
            var direction = (edge.From - edge.To) / Distance(edge.From, edge.To);
            var samples = 0;
            var shown = 0;
            for (var d = paint.Radius + 14; d < Distance(edge.From, edge.To) - 14 - 14 - SummaryNetworkCanvas.SummaryRingGap; d++)
            {
                samples++;
                shown += Near(Pixel(frame, canvas, edge.To + (direction * d)), trail, 40) ? 1 : 0;
            }

            Assert.True(samples > 20 && shown >= samples * 0.9, $"{theme}: {shown} of {samples} edge pixels show the trail");
        }

        window.Close();

        static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    }

    [AvaloniaFact]
    public async Task Hit_boxes_stay_apart_while_labels_show_and_every_dot_is_clickable_at_the_smallest_zoom()
    {
        // Two nodes as close as the layout allows, one above the other.
        var upper = new NodeView(new SummaryNodeId(Guid.NewGuid()), "上面的知识点", "上", 0, 0, true, false, SummaryNetworkLayout.LabelWidth("上面的知识点"));
        var lower = upper with { Id = new SummaryNodeId(Guid.NewGuid()), Y = SummaryNetworkLayout.NodeHeight };
        foreach (var zoom in new[] { 0.46, 1.0 })
        {
            var a = SummaryNetworkGeometry.HitBox(new Point(100, 100), upper, zoom, labelVisible: true);
            var b = SummaryNetworkGeometry.HitBox(new Point(100, 100 + (SummaryNetworkLayout.NodeHeight * zoom)), lower, zoom, labelVisible: true);
            Assert.False(a.Intersects(b), $"hit boxes overlap at zoom {zoom}: {a} and {b}");
        }

        // Side by side at the layout's padding: also apart.
        var right = upper with { Id = new SummaryNodeId(Guid.NewGuid()), X = SummaryNetworkLayout.BoxRight(0, upper.LabelWidth) + SummaryNetworkLayout.LabelPadding + SummaryNetworkLayout.NodeRadius };
        Assert.False(SummaryNetworkGeometry.HitBox(new Point(0, 0), upper, 0.46, true).Intersects(SummaryNetworkGeometry.HitBox(new Point(right.X * 0.46, 0), right, 0.46, true)));

        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var network = await SeedThreeChildrenAsync(app, window);
        var canvas = CanvasOf(window);
        network.Session.Viewport = canvas.Current with { Zoom = Viewport.MinZoom };

        // Zoomed far out the dots keep their size and may meet, but the nearest one takes the click.
        Assert.All(network.Nodes, n => Assert.Equal(n.Id, canvas.HitTest(canvas.ToScreen(n.X, n.Y))?.Id));

        // Where a hidden label would be, nothing is hit; the dot still is.
        network.Session.Viewport = canvas.Current with { Zoom = SummaryNetworkCanvas.LabelHideScale };
        var node = network.Nodes[0];
        var centre = canvas.ToScreen(node.X, node.Y);
        Assert.Equal(node.Id, canvas.HitTest(centre)?.Id);
        Assert.Null(canvas.HitTest(centre + new Vector(SummaryNetworkCanvas.RootRadius + SummaryNetworkLayout.LabelGap + 6, 0)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Wheel_zooms_by_default()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        Assert.True(network.WheelZooms);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hover_follows_what_is_under_the_pointer_after_a_pan()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        network.WheelZooms = false;
        var canvas = CanvasOf(window);
        var root = network.Nodes.Single(n => n.IsRoot);
        Point InWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;

        // Dragging empty canvas: when released, the pointer the hover and the pull follow is where it was let go.
        var start = new Point(12, 12);
        Assert.Null(canvas.HitTest(start));
        window.MouseMove(InWindow(start));
        window.MouseDown(InWindow(start), MouseButton.Left);
        window.MouseMove(InWindow(start + new Vector(60, 0)));
        window.MouseMove(InWindow(start + new Vector(120, 0)));
        window.MouseUp(InWindow(start + new Vector(120, 0)), MouseButton.Left);
        Assert.Equal(start + new Vector(120, 0), canvas.Pointer);
        Assert.Null(canvas.Hovered);

        // Scrolling pans the network under a pointer that does not move: the node that was hovered is no longer under it.
        var over = canvas.ToScreen(root.X, root.Y);
        window.MouseMove(InWindow(over));
        Assert.Equal(root.Id, canvas.Hovered);
        window.MouseWheel(InWindow(over), new Vector(0, -3));
        var now = canvas.HitTest(over);
        Assert.NotEqual(root.Id, now?.Id);
        Assert.Equal(now?.Id, canvas.Hovered);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_list_shows_read_names_as_secondary()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var summaries = app.Services.GetRequiredService<SummaryService>();
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var read = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var entry = (await summaries.GetAsync(read.Id, CancellationToken.None))!;
        await summaries.SetReadAsync(read.Id, entry.Current.Id, true, CancellationToken.None);
        await UiWait.UntilAsync(() => network.Items.Any(i => i.Node == read.Id && !i.Unread));
        T Named<T>(string name)
            where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            network.IsListView = true;
            window.UpdateLayout();
            var names = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("list-name")).ToList();
            var readName = names.Single(b => b.DataContext is SummaryListItem { HasSummary: true, Unread: false });
            var unreadName = names.First(b => b.DataContext is SummaryListItem { Unread: true });
            Assert.Equal(FontWeight.Normal, readName.FontWeight);
            Assert.Equal(ResourceColor(window, "MutedText"), Assert.IsAssignableFrom<ISolidColorBrush>(readName.Foreground).Color);
            Assert.Equal(FontWeight.SemiBold, unreadName.FontWeight);
            Assert.Equal(ResourceColor(window, "PrimaryText"), Assert.IsAssignableFrom<ISolidColorBrush>(unreadName.Foreground).Color);

            // Text fields take the theme's field colour, never Fluent's black in dark.
            var find = Named<TextBox>("FindBox");
            Assert.Equal(ResourceColor(window, "TextControlBackground"), Assert.IsAssignableFrom<ISolidColorBrush>(find.Background).Color);
        }

        Assert.Equal(Color.Parse("#222327"), ResourceColor(window, "TextControlBackground"));
        window.Close();
    }

    [AvaloniaFact]
    public void Every_canvas_colour_is_defined_as_a_brush_in_both_themes()
    {
        var window = new Window();
        var canvas = new SummaryNetworkCanvas();
        window.Content = canvas;
        window.Show();
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            Assert.All(SummaryNetworkPalette.Keys, key => Assert.NotNull(SummaryNetworkPalette.Resolve(canvas, key)));
        }

        Assert.Null(SummaryNetworkPalette.Resolve(canvas, "SummaryNetworkNoSuchColour"));
        window.Close();
    }

    /// <summary>The seeded network plus a third child of the root.</summary>
    private static async Task<SummaryNetworkHomeViewModel> SeedThreeChildrenAsync(DesktopHarness app, MainWindow window)
    {
        await SummaryNetworkScenario.SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var summaries = app.Services.GetRequiredService<SummaryService>();
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var root = network.Session.Projection.Nodes.Single(n => n.Conversation == n.Root).Conversation;
        var engines = await SummaryNetworkScenario.BranchAsync(app, conversations, root, "蒸汽机车怎么造？");
        app.Client.Knowledge.Replies.Enqueue("标题：蒸汽机车的构造\n短名：蒸汽机车\n\n正文");
        Assert.True((await summaries.SummarizeAsync(engines, CancellationToken.None)).Accepted);
        await UiWait.UntilAsync(() => network.Nodes.Count(n => n.HasSummary) == 4);
        window.UpdateLayout();
        return network;
    }

    private static Rect Dot(NodePaint node) => new(node.Center.X - node.Radius, node.Center.Y - node.Radius, 2 * node.Radius, 2 * node.Radius);

    /// <summary>Whether the segment passes through the rectangle (sampled every half pixel).</summary>
    private static bool Crosses(Point from, Point to, Rect rect)
    {
        var length = Math.Max(1, Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2)));
        for (var i = 0.0; i <= length; i += 0.5)
        {
            if (rect.Contains(from + ((to - from) * (i / length))))
            {
                return true;
            }
        }

        return false;
    }

    private static Color ResourceColor(MainWindow window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), key);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color Pixel(Avalonia.Media.Imaging.WriteableBitmap frame, SummaryNetworkCanvas canvas, Point point)
    {
        var at = canvas.TranslatePoint(point, TopLevel.GetTopLevel(canvas)!)!.Value;
        using var buffer = frame.Lock();
        var x = (int)Math.Round(at.X * frame.Dpi.X / 96);
        var y = (int)Math.Round(at.Y * frame.Dpi.Y / 96);
        var value = Marshal.ReadInt32(buffer.Address, (y * buffer.RowBytes) + (x * 4));
        var b = (byte)(value & 0xFF);
        var g = (byte)((value >> 8) & 0xFF);
        var r = (byte)((value >> 16) & 0xFF);
        return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromRgb(b, g, r) : Color.FromRgb(r, g, b);
    }

    private static bool Near(Color a, Color b, int tolerance = 12) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;
}
