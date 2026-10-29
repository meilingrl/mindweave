using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Summaries;
using Mindweave.Desktop.Network;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The network's drawing (S7, the confirmed sample's visual language): read nodes hollow and unread nodes solid in
/// both themes, labels hidden when zoomed far out, the selected node's source trail, search rings, hover and the
/// exploration captions. Drawing parameters come from the same list the render pass uses; shapes are also checked
/// on rendered pixels.
/// </summary>
public class NetworkCanvasTests
{
    [AvaloniaFact]
    public async Task Read_nodes_are_hollow_rings_and_unread_nodes_solid_dots_in_both_themes()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await NetworkUiTests.SeedAsync(app);
        var summaries = app.Services.GetRequiredService<SummaryService>();
        var network = await NetworkUiTests.OpenNetworkAsync(app, window);
        var read = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var entry = (await summaries.GetAsync(read.Id, CancellationToken.None))!;
        await summaries.SetReadAsync(read.Id, entry.Current.Id, true, CancellationToken.None);
        await UiWait.UntilAsync(() => !network.Nodes.Single(n => n.Id == read.Id).Unread);
        var canvas = Canvas(window);

        var paints = canvas.PaintNodes();
        var hollow = paints.Single(p => p.Node.Id == read.Id);
        Assert.True(hollow.Hollow);
        Assert.False(hollow.UnreadDot);
        Assert.False(hollow.LabelBold);
        Assert.All(paints.Where(p => p.Node.Id != read.Id), solid =>
        {
            Assert.False(solid.Hollow);
            Assert.True(solid.UnreadDot);
            Assert.True(solid.LabelBold);
        });

        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            window.UpdateLayout();
            var unread = paints.First(p => !p.Hollow);
            using var frame = window.CaptureRenderedFrame()!;
            var background = ResourceColor(window, "NetworkBackground");
            var node = ResourceColor(window, "NetworkNode");

            // The centre of a read node shows the canvas through its ring; an unread node's centre is filled.
            Assert.True(Near(Pixel(frame, canvas, hollow.Center), background), $"{theme}: read centre {Pixel(frame, canvas, hollow.Center)} is not {background}");
            Assert.True(Near(Pixel(frame, canvas, unread.Center), node), $"{theme}: unread centre {Pixel(frame, canvas, unread.Center)} is not {node}");
            Assert.True(Near(Pixel(frame, canvas, hollow.Center + new Vector(hollow.Radius - 1, 0)), ResourceColor(window, "NetworkNodeRead"), 60), $"{theme}: no ring");
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Labels_are_hidden_at_045_and_below_except_the_selected_and_matching_ones()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await NetworkUiTests.SeedAsync(app);
        var network = await NetworkUiTests.OpenNetworkAsync(app, window);
        var canvas = Canvas(window);
        var viewport = canvas.Current;

        network.Session.Viewport = viewport with { Zoom = 0.5 };
        Assert.All(canvas.PaintNodes(), p => Assert.True(p.LabelVisible));
        Assert.Equal(10, canvas.PaintNodes()[0].LabelSize, 3);

        network.Session.Viewport = viewport with { Zoom = NetworkCanvas.LabelHideScale };
        Assert.All(canvas.PaintNodes(), p => Assert.False(p.LabelVisible));

        var selected = network.Nodes.Single(n => n.ShortTitle == "火车起源");
        var matched = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        network.Select(selected.Id);
        network.SearchText = "铁轨";
        network.Session.Viewport = viewport with { Zoom = NetworkCanvas.LabelHideScale };
        var paints = canvas.PaintNodes();
        Assert.True(paints.Single(p => p.Node.Id == selected.Id).LabelVisible);
        Assert.True(paints.Single(p => p.Node.Id == matched.Id).LabelVisible);
        Assert.False(paints.Single(p => p.Node.ShortTitle == "火箭号速度").LabelVisible);
        Assert.Equal(13, NetworkCanvas.LabelSize(1.2));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Selecting_a_node_highlights_its_whole_source_trail_to_the_root()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await NetworkUiTests.SeedAsync(app);
        var network = await NetworkUiTests.OpenNetworkAsync(app, window);
        var canvas = Canvas(window);
        var root = network.Nodes.Single(n => n.ShortTitle == "火车起源");
        var leaf = network.Nodes.Single(n => n.ShortTitle == "火箭号速度");
        var sibling = network.Nodes.Single(n => n.ShortTitle == "铁轨");

        network.Select(leaf.Id);

        Assert.Equal(new HashSet<Core.Summaries.SummaryNodeId> { leaf.Id, root.Id }, network.Trail);
        var nodes = canvas.PaintNodes();
        Assert.True(nodes.Single(p => p.Node.Id == root.Id).OnTrail);
        Assert.False(nodes.Single(p => p.Node.Id == sibling.Id).OnTrail);
        var leafPaint = nodes.Single(p => p.Node.Id == leaf.Id);
        Assert.Equal(NodeRing.Selected, leafPaint.Ring);
        Assert.True(leafPaint.Halo);
        var edges = canvas.PaintEdges();
        var trailEdge = edges.Single(e => e.Edge.To == leaf.Id);
        Assert.Equal(EdgeState.Trail, trailEdge.State);
        Assert.True(trailEdge.Dashed, "the edge passes an unsummarized conversation and stays dashed when highlighted");
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
        var window = NetworkUiTests.Show(app);
        await NetworkUiTests.SeedAsync(app);
        var network = await NetworkUiTests.OpenNetworkAsync(app, window);
        var canvas = Canvas(window);
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
        var window = NetworkUiTests.Show(app);
        await NetworkUiTests.SeedAsync(app);
        var network = await NetworkUiTests.OpenNetworkAsync(app, window);
        var canvas = Canvas(window);
        var root = network.Nodes.Single(n => n.ShortTitle == "火车起源");
        var before = canvas.PaintNodes().Single(p => p.Node.Id == root.Id);

        canvas.SetPointer(canvas.ToScreen(root.X, root.Y) + new Vector(3, 0));

        Assert.Equal(root.Id, canvas.Hovered);
        var hovered = canvas.PaintNodes().Single(p => p.Node.Id == root.Id);
        Assert.Equal(before.Radius + NetworkCanvas.HoverGrowth, hovered.Radius);
        Assert.True(hovered.Halo && hovered.LabelVisible);
        Assert.All(canvas.PaintEdges(), e => Assert.Equal(EdgeState.Near, e.State));
        Assert.NotNull(canvas.Cursor);

        // Decorative only: at most 1.5 px, never past 90 px, and no layout position changes.
        Assert.Equal(1.5, NetworkCanvas.Attraction(new Point(0, 0), new Point(45, 0)).Length, 3);
        Assert.Equal(default, NetworkCanvas.Attraction(new Point(0, 0), new Point(95, 0)));
        canvas.SetPointer(null);
        for (var i = 0; i < 500 && canvas.IsMoving; i++)
        {
            canvas.StepMotion();
        }

        Assert.False(canvas.IsMoving);
        Assert.Null(canvas.Hovered);
        Assert.Equal(before.Center, canvas.PaintNodes().Single(p => p.Node.Id == root.Id).Center);
        Assert.False(network.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Each_exploration_is_named_above_its_root_or_above_its_top_left_node_when_the_root_has_no_summary()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await NetworkUiTests.SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var photos = await NetworkUiTests.AskAsync(app, conversations, null, app.Workbench.CurrentWorkspace!.Id, "摄影是怎么发明的？");
        var plate = await NetworkUiTests.BranchAsync(app, conversations, photos, "银版法怎么做？");
        var summaries = app.Services.GetRequiredService<SummaryService>();
        app.Client.SummaryReplies.Enqueue("标题：银版法\n短名：银版法\n\n正文");
        Assert.True((await summaries.SummarizeAsync(plate, CancellationToken.None)).Accepted);
        var network = await NetworkUiTests.OpenNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);

        Assert.Equal(2, network.Captions.Count);
        var trains = network.Captions.Single(c => c.Anchor == network.Nodes.Single(n => n.ShortTitle == "火车起源").Id);
        Assert.StartsWith(UiText.Exploration + " · ", trains.Text, StringComparison.Ordinal);
        Assert.True(network.Nodes.Single(n => n.ShortTitle == "火车起源").IsRoot);
        var lone = network.Captions.Single(c => c.Anchor == network.Nodes.Single(n => n.ShortTitle == "银版法").Id);
        var listed = (await conversations.ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None)).Single(c => c.Conversation.Id == photos);
        Assert.Equal(UiText.ExplorationCaption(UiText.ConversationTitle(listed.Conversation, listed.FirstQuestion, listed.Quote)), lone.Text);
        Assert.False(network.Nodes.Single(n => n.ShortTitle == "银版法").IsRoot);
        window.Close();
    }

    private static NetworkCanvas Canvas(MainWindow window) => window.GetVisualDescendants().OfType<NetworkCanvas>().Single();

    private static Color ResourceColor(MainWindow window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), key);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color Pixel(Avalonia.Media.Imaging.WriteableBitmap frame, NetworkCanvas canvas, Point point)
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
