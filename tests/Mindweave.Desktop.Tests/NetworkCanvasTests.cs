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
using Mindweave.Desktop.Network;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

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
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var summaries = app.Services.GetRequiredService<SummaryService>();
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
        var read = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var entry = (await summaries.GetAsync(read.Id, CancellationToken.None))!;
        await summaries.SetReadAsync(read.Id, entry.Current.Id, true, CancellationToken.None);
        await UiWait.UntilAsync(() => !network.Nodes.Single(n => n.Id == read.Id).Unread);
        var canvas = CanvasOf(window);

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
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var viewport = canvas.Current;

        network.Session.Viewport = viewport with { Zoom = 0.5 };
        Assert.All(canvas.PaintNodes(), p => Assert.True(p.LabelVisible));
        Assert.Equal(NetworkLayout.LabelEm * 0.5, canvas.PaintNodes()[0].LabelSize, 3);

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
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var root = network.Nodes.Single(n => n.ShortTitle == "火车起源");
        var leaf = network.Nodes.Single(n => n.ShortTitle == "火箭号速度");
        var sibling = network.Nodes.Single(n => n.ShortTitle == "铁轨");

        // At rest the dashed edge already says it passes an unsummarized conversation, quietly (architecture §11).
        var resting = canvas.PaintEdges().Single(e => e.Edge.To == leaf.Id);
        Assert.Equal(EdgeState.Indirect, resting.State);
        Assert.Equal(UiText.ViaCount(1), resting.Label);
        Assert.False(resting.LabelEmphasized);

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
        Assert.Equal(UiText.ViaCount(1), trailEdge.Label);
        Assert.True(trailEdge.LabelEmphasized);
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
        await NetworkScenario.SeedAsync(app);
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
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
        await NetworkScenario.SeedAsync(app);
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var root = network.Nodes.Single(n => n.ShortTitle == "火车起源");
        var before = canvas.PaintNodes().Single(p => p.Node.Id == root.Id);

        canvas.SetPointer(canvas.ToScreen(root.X, root.Y) + new Vector(3, 0));

        Assert.Equal(root.Id, canvas.Hovered);
        var hovered = canvas.PaintNodes().Single(p => p.Node.Id == root.Id);
        Assert.Equal(before.Radius + NetworkCanvas.HoverGrowth, hovered.Radius);
        Assert.True(hovered.Halo && hovered.LabelVisible);
        Assert.All(canvas.PaintEdges(), e => Assert.Equal(EdgeState.Near, e.State));
        Assert.Same(NetworkCanvas.HandCursor, canvas.Cursor);

        // Decorative only: at most 1.5 px, never past 90 px, and no layout position changes.
        Assert.Equal(1.5, NetworkCanvas.Attraction(new Point(0, 0), new Point(45, 0)).Length, 3);
        Assert.Equal(default, NetworkCanvas.Attraction(new Point(0, 0), new Point(95, 0)));
        canvas.SetPointer(null);
        Assert.Same(Cursor.Default, canvas.Cursor);
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
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var photos = await NetworkScenario.AskAsync(app, conversations, null, app.Workbench.CurrentWorkspace!.Id, "摄影是怎么发明的？");
        var plate = await NetworkScenario.BranchAsync(app, conversations, photos, "银版法怎么做？");
        var summaries = app.Services.GetRequiredService<SummaryService>();
        app.Client.SummaryReplies.Enqueue("标题：银版法\n短名：银版法\n\n正文");
        Assert.True((await summaries.SummarizeAsync(plate, CancellationToken.None)).Accepted);
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
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

    [AvaloniaFact]
    public async Task The_source_path_names_every_conversation_from_the_root_and_marks_the_unsummarized_ones()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var summaries = app.Services.GetRequiredService<SummaryService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;

        // A second exploration whose root and middle conversation have no summary: root → A → B, only B summarized.
        var photos = await NetworkScenario.AskAsync(app, conversations, null, workspace, "摄影是怎么发明的？");
        var plates = await NetworkScenario.BranchAsync(app, conversations, photos, "银版法怎么做？");
        var mercury = await NetworkScenario.BranchAsync(app, conversations, plates, "为什么用汞蒸气？");
        app.Client.SummaryReplies.Enqueue("标题：汞蒸气显影\n短名：汞蒸气\n\n正文");
        Assert.True((await summaries.SummarizeAsync(mercury, CancellationToken.None)).Accepted);
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);
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

        static List<(string Name, bool Summarized)> Steps(NetworkHomeViewModel network) =>
            [.. network.Selected!.Steps.Select(s => (s.Name, s.Summarized))];
    }

    [AvaloniaFact]
    public async Task The_default_view_of_the_gallery_network_reads_well()
    {
        // The gallery's network: two explorations of a root and three children each, one child with a candidate.
        var nodes = new List<LayoutNode>();
        var trains = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var fast = Guid.Parse("00000000-0000-0000-0000-000000000002");
        foreach (var (group, labels) in new[] { (trains, new[] { "火车的发明", "缆索牵引", "标准轨距 ↻", "动轮与速度" }), (fast, new[] { "高铁速度", "无砟轨道", "动力分散", "受电弓" }) })
        {
            var root = new SummaryNodeId(Guid.NewGuid());
            nodes.Add(new LayoutNode(root, null, group, nodes.Count, NetworkLayout.LabelWidth(labels[0])));
            foreach (var label in labels[1..])
            {
                nodes.Add(new LayoutNode(new SummaryNodeId(Guid.NewGuid()), root, group, nodes.Count, NetworkLayout.LabelWidth(label)));
            }
        }

        var layout = NetworkLayout.Arrange(nodes);
        var views = nodes.Select(n => new NodeView(n.Id, "x", "x", layout[n.Id].X, layout[n.Id].Y, true, false, n.LabelWidth)).ToList();

        // The canvas beside the details panel in the gallery's 1100 × 720 window.
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        await NetworkScenario.OpenNetworkAsync(app, window);
        var size = CanvasOf(window).Bounds.Size;
        window.Close();
        var fitted = NetworkCanvas.FitTo(views, size);
        Assert.True(fitted.Zoom >= 0.8, $"default zoom {fitted.Zoom:0.00} in {size}");
        Assert.Equal(NetworkCanvas.LabelMaxSize, NetworkCanvas.LabelSize(fitted.Zoom), 0.5);
    }

    [AvaloniaFact]
    public void Drawn_labels_never_need_more_room_than_the_layout_reserved_including_the_candidate_mark()
    {
        Assert.True(NetworkLayout.LabelWidth(NetworkLayout.Label("标准轨距", true)) > NetworkLayout.LabelWidth("标准轨距"));
        var labels = new[] { "标准轨距 ↻", "火车的发明", "动力分散", "受电弓", "1435 毫米", "Rocket 号", "MW-C12 ↻", "Stephenson" };
        foreach (var zoom in new[] { Viewport.MinZoom, 0.46, 0.65, 0.8, 0.93, 1.2, Viewport.MaxZoom })
        {
            foreach (var label in labels)
            {
                foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold })
                {
                    var drawn = NetworkCanvas.Text(label, NetworkCanvas.LabelSize(zoom), Brushes.Black, weight).Width;
                    Assert.True(drawn <= NetworkLayout.LabelWidth(label) * zoom + 0.5, $"{label} at {zoom}: {drawn} > {NetworkLayout.LabelWidth(label) * zoom}");
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
    public async Task A_highlighted_trail_stays_visible_for_a_node_left_of_its_source()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var network = await SeedThreeChildrenAsync(app, window);
        var canvas = CanvasOf(window);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            window.RequestedThemeVariant = theme;
            network.ShowAllCommand.Execute(null);

            // The child level with the root on its left: its label would run along the edge back to the root.
            var root = network.Nodes.Single(n => n.IsRoot);
            var left = network.Nodes.Single(n => n.X < root.X - 1 && Math.Abs(n.Y - root.Y) < 1);
            network.Select(left.Id);
            window.UpdateLayout();
            var paint = canvas.PaintNodes().Single(p => p.Node.Id == left.Id);
            var edge = canvas.PaintEdges().Single(e => e.Edge.To == left.Id);
            Assert.Equal(EdgeState.Trail, edge.State);
            Assert.False(NetworkCanvas.Crosses(edge.From, edge.To, paint.LabelBounds), "the label is drawn across its own edge");

            // Every pixel of the edge between the two halos shows the trail colour: nothing is drawn over it.
            using var frame = window.CaptureRenderedFrame()!;
            var trail = ResourceColor(window, "NetworkEdgeTrail");
            var direction = (edge.From - edge.To) / Distance(edge.From, edge.To);
            var samples = 0;
            var shown = 0;
            for (var d = paint.Radius + 14; d < Distance(edge.From, edge.To) - NetworkCanvas.RootRadius - 14; d++)
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
    public async Task Captions_sit_above_their_group_and_no_edge_or_label_crosses_them()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var network = await SeedThreeChildrenAsync(app, window);
        var canvas = CanvasOf(window);
        foreach (var arrange in new[] { false, true })
        {
            if (arrange)
            {
                network.ArrangeCommand.Execute(null);
            }

            network.ShowAllCommand.Execute(null);
            window.UpdateLayout();
            var nodes = canvas.PaintNodes();
            var captions = canvas.PaintCaptions();
            Assert.Equal(network.Captions.Count, captions.Count);
            foreach (var caption in captions)
            {
                var members = nodes.Where(n => caption.Caption.Members.Contains(n.Node.Id)).ToList();
                Assert.All(members, m => Assert.True(caption.Bounds.Bottom <= Math.Min(Dot(m).Top, m.LabelBounds.Top), $"caption {caption.Caption.Text} is not above {m.Label}"));
                Assert.All(nodes, n => Assert.False(caption.Bounds.Intersects(n.LabelBounds) || caption.Bounds.Intersects(Dot(n)), $"caption crosses {n.Label}"));
                Assert.All(canvas.PaintEdges(), e => Assert.False(Crosses(e.From, e.To, caption.Bounds), $"an edge crosses the caption {caption.Caption.Text}"));

                // Fit leaves room for it.
                Assert.True(caption.Bounds.Top >= 0, $"caption {caption.Caption.Text} is cut off at the top ({caption.Bounds.Top})");
            }
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Hit_boxes_never_overlap_even_at_the_smallest_zoom_and_hidden_labels_are_not_clickable()
    {
        // Two nodes as close as the layout allows, one above the other.
        var upper = new NodeView(new SummaryNodeId(Guid.NewGuid()), "上面的知识点", "上", 0, 0, true, false, NetworkLayout.LabelWidth("上面的知识点"));
        var lower = upper with { Id = new SummaryNodeId(Guid.NewGuid()), Y = NetworkLayout.NodeHeight };
        foreach (var zoom in new[] { Viewport.MinZoom, 0.45, 1.0 })
        {
            var a = NetworkCanvas.HitBox(new Point(100, 100), upper, zoom, labelVisible: true);
            var b = NetworkCanvas.HitBox(new Point(100, 100 + (NetworkLayout.NodeHeight * zoom)), lower, zoom, labelVisible: true);
            Assert.False(a.Intersects(b), $"hit boxes overlap at zoom {zoom}: {a} and {b}");
        }

        // Side by side at the layout's padding: also apart.
        var right = upper with { Id = new SummaryNodeId(Guid.NewGuid()), X = NetworkLayout.BoxRight(0, upper.LabelWidth) + NetworkLayout.LabelPadding + NetworkLayout.NodeRadius };
        var z = Viewport.MinZoom;
        Assert.False(NetworkCanvas.HitBox(new Point(0, 0), upper, z, true).Intersects(NetworkCanvas.HitBox(new Point(right.X * z, 0), right, z, true)));

        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var network = await SeedThreeChildrenAsync(app, window);
        var canvas = CanvasOf(window);
        network.Session.Viewport = canvas.Current with { Zoom = Viewport.MinZoom };
        var boxes = network.Nodes.Select(n => NetworkCanvas.HitBox(canvas.ToScreen(n.X, n.Y), n, Viewport.MinZoom, false)).ToList();
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                Assert.False(boxes[i].Intersects(boxes[j]), $"hit boxes {i} and {j} overlap at the smallest zoom");
            }
        }

        // Where a hidden label would be, nothing is hit; the dot still is.
        network.Session.Viewport = canvas.Current with { Zoom = NetworkCanvas.LabelHideScale };
        var node = network.Nodes[0];
        var centre = canvas.ToScreen(node.X, node.Y);
        Assert.Equal(node.Id, canvas.HitTest(centre)?.Id);
        Assert.Null(canvas.HitTest(centre + new Vector(NetworkCanvas.RootRadius + NetworkLayout.LabelGap + 6, 0)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hover_follows_what_is_under_the_pointer_after_a_pan()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
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
    public async Task Saved_is_a_quiet_status_unsaved_an_accent_button_and_read_names_are_secondary()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var summaries = app.Services.GetRequiredService<SummaryService>();
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
        var read = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var entry = (await summaries.GetAsync(read.Id, CancellationToken.None))!;
        await summaries.SetReadAsync(read.Id, entry.Current.Id, true, CancellationToken.None);
        await UiWait.UntilAsync(() => network.Items.Any(i => i.Node == read.Id && !i.Unread));
        T Named<T>(string name)
            where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

        window.UpdateLayout();
        Assert.False(Named<Button>("SaveLayoutButton").IsVisible);
        Assert.True(Named<StackPanel>("LayoutSavedStatus").IsVisible);

        await NetworkScenario.MoveSomethingAsync(app, network);
        network.ArrangeCommand.Execute(null);
        window.UpdateLayout();
        var save = Named<Button>("SaveLayoutButton");
        Assert.True(save.IsVisible && save.Classes.Contains("primary"));
        Assert.False(Named<StackPanel>("LayoutSavedStatus").IsVisible);
        await network.SaveLayoutCommand.ExecuteAsync(null);

        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            network.IsListView = true;
            window.UpdateLayout();
            var names = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("list-name")).ToList();
            var readName = names.Single(b => b.DataContext is SummaryListItem { Unread: false });
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
        var canvas = new NetworkCanvas();
        window.Content = canvas;
        window.Show();
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            Assert.All(NetworkPalette.Keys, key => Assert.NotNull(NetworkPalette.Resolve(canvas, key)));
        }

        Assert.Null(NetworkPalette.Resolve(canvas, "NetworkNoSuchColour"));
        window.Close();
    }

    [AvaloniaFact]
    public void The_drag_shadow_is_translucent_in_both_themes()
    {
        // A lifted node's shadow is a soft hint, not a solid black crescent: well under half opaque in each theme.
        var window = new Window();
        var canvas = new NetworkCanvas();
        window.Content = canvas;
        window.Show();
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            // Dark may go deeper (black on a near-black canvas is otherwise invisible), but never fully opaque.
            var shadow = Assert.IsAssignableFrom<ISolidColorBrush>(NetworkPalette.Resolve(canvas, "NetworkDragShadow"));
            Assert.InRange(shadow.Color.A, (byte)0x20, theme == ThemeVariant.Light ? (byte)0x70 : (byte)0xE0);
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_dragged_node_stands_out_in_the_dark_theme_and_light_draws_no_glow()
    {
        // Dark: a black shadow hardly shows on the near-black canvas, so the lift must still read on pixels: a clearly
        // darker crescent below the dot than above it, and a glow just outside the selection halo where the canvas was.
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var node = network.Nodes.First(n => !n.IsRoot);
        var start = canvas.ToScreen(node.X, node.Y);
        Point InWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;
        window.MouseMove(InWindow(start));
        window.MouseDown(InWindow(start), MouseButton.Left);
        window.MouseMove(InWindow(start + new Vector(10, 10)));
        window.MouseMove(InWindow(start + new Vector(20, 20)));
        Assert.Equal(node.Id, canvas.Dragging);

        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.UpdateLayout();
        var lifted = canvas.PaintNodes().Single(p => p.Lifted);
        var (centre, r) = (lifted.Center, lifted.Radius);
        using (var frame = window.CaptureRenderedFrame()!)
        {
            var below = Luma(Pixel(frame, canvas, centre + new Vector(0, r + 3)));
            var above = Luma(Pixel(frame, canvas, centre - new Vector(0, r + 3)));
            Assert.True(above - below >= 30, $"shadow too faint: above the dot {above:F0}, below it {below:F0}");

            var background = Luma(ResourceColor(window, "NetworkBackground"));
            var lit = Enumerable.Range(0, 8)
                .Select(i => i * Math.PI / 4)
                .Count(a => Luma(Pixel(frame, canvas, centre + new Vector(Math.Cos(a), Math.Sin(a)) * (r + 13))) - background >= 8);
            Assert.True(lit >= 6, $"no glow around the lifted node: {lit} of 8 points lit");
        }

        // Light keeps its look (the shadow alone reads on the pale canvas): no glow at all.
        window.RequestedThemeVariant = ThemeVariant.Light;
        window.UpdateLayout();
        Assert.Equal(Colors.Transparent, ResourceColor(window, "NetworkDragGlow"));
        Assert.False(NetworkPalette.From(canvas).HasDragGlow);
        window.MouseUp(InWindow(start + new Vector(20, 20)), MouseButton.Left);
        network.Session.DiscardChanges();
        window.Close();
    }

    private static double Luma(Color c) => (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);

    /// <summary>The seeded network plus a third child of the root, arranged: one child sits left of the root.</summary>
    private static async Task<NetworkHomeViewModel> SeedThreeChildrenAsync(DesktopHarness app, MainWindow window)
    {
        await NetworkScenario.SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var summaries = app.Services.GetRequiredService<SummaryService>();
        var network = await NetworkScenario.OpenNetworkAsync(app, window);
        var root = network.Session.Projection.Nodes.Single(n => n.Conversation == n.Root).Conversation;
        var engines = await NetworkScenario.BranchAsync(app, conversations, root, "蒸汽机车怎么造？");
        app.Client.SummaryReplies.Enqueue("标题：蒸汽机车的构造\n短名：蒸汽机车\n\n正文");
        Assert.True((await summaries.SummarizeAsync(engines, CancellationToken.None)).Accepted);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);
        network.ArrangeCommand.Execute(null);
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
