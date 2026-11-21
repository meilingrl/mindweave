using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Sqlite;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The session network's drawing behind its gate: dots sized by how much was asked, summaries wearing a ring, the arrowed
/// dotted time thread in the spatial view, the main line and curved lanes on the timeline, and day labels. Without the gate
/// the drawing is today's (<see cref="SummaryNetworkCanvasTests"/>).
/// </summary>
public class SessionNetworkCanvasTests
{
    private static DesktopHarness Gated() => DesktopHarness.Create(s => s.AddSingleton(new SessionNetworkGate(true)));

    [Fact]
    public void Dot_radius_grows_with_the_square_root_of_the_questions_and_is_clamped()
    {
        Assert.Equal(5, SummaryNetworkGeometry.DotRadius(1));
        Assert.Equal(5, SummaryNetworkGeometry.DotRadius(0));
        Assert.Equal(11, SummaryNetworkGeometry.DotRadius(20), 0.05);
        Assert.Equal(14, SummaryNetworkGeometry.DotRadius(500));
    }

    [Fact]
    public void Day_labels_and_hover_labels_are_written_in_chinese()
    {
        Assert.Equal("11月20日", UiText.DayLabel(new DateOnly(2026, 11, 20), today: new DateOnly(2026, 11, 21)));
        Assert.Equal("2025年11月20日", UiText.DayLabel(new DateOnly(2025, 11, 20), today: new DateOnly(2026, 1, 2)));
        var local = new DateTime(2026, 11, 20, 21, 38, 0, DateTimeKind.Unspecified);
        var at = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
        Assert.Equal("蒸汽机 · 11月20日 21:38", UiText.DotHoverLabel("蒸汽机", at));
        Assert.Equal("Mindweave", UiText.SessionNetworkTitle);
    }

    [AvaloniaFact]
    public async Task Dot_size_follows_weight()
    {
        await using var app = Gated();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var one = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "火车是怎么发明的？");
        var twenty = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "摄影怎么入门？");
        var many = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "蒸汽机怎么工作？");
        await AddQuestionsAsync(app, twenty, 19);
        await AddQuestionsAsync(app, many, 499);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var paints = CanvasOf(window).PaintNodes();

        double RadiusOf(ConversationId conversation) =>
            paints.Single(p => p.Node.Id == network.Session.Projection.Nodes.Single(n => n.Conversation == conversation).Id).Radius;
        Assert.Equal(5, RadiusOf(one));
        Assert.Equal(11, RadiusOf(twenty), 0.05);
        Assert.Equal(14, RadiusOf(many));
        window.Close();
    }

    [AvaloniaFact]
    public async Task One_session_opens_centred_at_zoom_one_in_the_spatial_view()
    {
        await using var app = Gated();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        await SummaryNetworkScenario.AskAsync(app, conversations, null, app.Workbench.CurrentWorkspace!.Id, "火车是怎么发明的？");
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);

        // The first drawing may precede the title that arrives a moment later: open again from what is there now.
        await UiWait.UntilAsync(() => network.Session.Positions.Count == 1);
        network.Session.Viewport = null;
        var opening = CanvasOf(window).Current;

        var only = Assert.Single(network.Nodes);
        Assert.Equal(1, opening.Zoom);
        Assert.Equal(only.X, opening.CenterX, 6);
        Assert.Equal(only.Y, opening.CenterY, 6);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_show_all_request_does_not_outlive_an_empty_picture()
    {
        await using var app = Gated();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(Navigation.MainSection.SummaryNetwork);
        var network = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
        await network.Loaded;
        window.UpdateLayout();
        Assert.Empty(network.Nodes);

        network.ShowAllCommand.Execute(null);
        _ = CanvasOf(window).Current;

        Assert.False(network.Session.FitAllRequested);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Summaries_wear_a_ring_and_every_dot_is_filled()
    {
        await using var app = Gated();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);
        var paints = CanvasOf(window).PaintNodes();

        Assert.Contains(paints, p => p.SummaryRing);
        Assert.Contains(paints, p => !p.SummaryRing);
        Assert.All(paints, p =>
        {
            Assert.Equal(p.Node.HasSummary, p.SummaryRing);
            Assert.False(p.Hollow);
        });

        // The unread badge stays clear of the ring (its outer edge: the gap plus half the 1.5 px pen).
        var badged = paints.Where(p => p.SummaryRing && p.UnreadDot).ToList();
        Assert.NotEmpty(badged);
        Assert.All(badged, p =>
        {
            var offset = p.UnreadDotCenter - p.Center;
            var distance = Math.Sqrt((offset.X * offset.X) + (offset.Y * offset.Y));
            Assert.True(distance - NodePaint.UnreadDotRadius > p.Radius + SummaryNetworkCanvas.SummaryRingGap + 0.75, $"badge at {distance} touches the ring of a {p.Radius} px dot");
        });
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_time_thread_is_fainter_than_a_branch_in_both_themes()
    {
        await using var app = Gated();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(Navigation.MainSection.SummaryNetwork);
        window.UpdateLayout();
        var canvas = CanvasOf(window);

        foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            window.UpdateLayout();
            var palette = SummaryNetworkPalette.From(canvas);
            var branch = palette.EdgePen(EdgeState.Normal, false);
            foreach (var thread in new[] { palette.TimeThreadPen(false), palette.TimeArrowPen(false) })
            {
                Assert.True(thread.Thickness < branch.Thickness, $"{theme}: {thread.Thickness} px is not thinner than a {branch.Thickness} px branch");
                Assert.Same(branch.Brush, thread.Brush);
            }
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_time_thread_is_dotted_with_an_arrow_in_space_and_the_main_line_on_the_timeline()
    {
        await using var app = Gated();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var root = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "火车是怎么发明的？");
        var first = await SummaryNetworkScenario.BranchAsync(app, conversations, root, "谁造了第一台？");
        await SummaryNetworkScenario.BranchAsync(app, conversations, first, "它跑多快？");
        await SummaryNetworkScenario.BranchAsync(app, conversations, root, "铁轨用什么做？");
        await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "摄影怎么入门？");
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 5);
        var canvas = CanvasOf(window);

        foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            window.UpdateLayout();
            Screenshots.Capture(window, $"session-network-canvas-spatial-{theme.ToString().ToLowerInvariant()}");
        }

        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var spatial = canvas.PaintEdges();
        Assert.Single(spatial, e => e.Edge.Kind == SummaryNetworkEdgeKind.TimeThread);
        Assert.Equal(3, spatial.Count(e => e.Edge.Kind == SummaryNetworkEdgeKind.Branch));
        Assert.All(spatial, e =>
        {
            var thread = e.Edge.Kind == SummaryNetworkEdgeKind.TimeThread;
            Assert.Equal(thread, e.Dashed);
            Assert.Equal(thread, e.Arrow);
            Assert.False(e.Curved);
            Assert.False(e.Timeline);
        });

        // The timeline, switched as the page's 时间线 button does: the page rebuilds what it draws at once. The branches leave
        // the main line in curves; the grandchild continues its parent's lane in a straight line.
        network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
        Assert.All(network.Nodes, n => Assert.Equal(network.Session.Positions[n.Id], new Application.Storage.NodePosition(n.X, n.Y)));
        window.UpdateLayout();

        Screenshots.Capture(window, "session-network-canvas-timeline");
        var timeline = canvas.PaintEdges();
        Assert.All(timeline, e =>
        {
            Assert.False(e.Dashed);
            Assert.False(e.Arrow);
            Assert.Equal(e.Edge.Kind == SummaryNetworkEdgeKind.Branch && e.Edge.FromY != e.Edge.ToY, e.Curved);
            Assert.True(e.Timeline);
        });

        // Bolder on the timeline (owner, 2026-11-21): the main line 2 px, branches 1.5 px, never thinner when hovered.
        var palette = SummaryNetworkPalette.From(canvas);
        Assert.Equal(2.0, palette.TimelinePen(EdgeState.Normal, mainLine: true).Thickness);
        Assert.Equal(1.5, palette.TimelinePen(EdgeState.Normal, mainLine: false).Thickness);
        Assert.All(Enum.GetValues<EdgeState>(), state => Assert.True(palette.TimelinePen(state, mainLine: true).Thickness >= 2.0));
        Assert.Contains(timeline, e => e.Curved);
        Assert.Contains(timeline, e => e.Edge.Kind == SummaryNetworkEdgeKind.Branch && !e.Curved);
        Assert.Single(timeline, e => e.Edge.Kind == SummaryNetworkEdgeKind.TimeThread);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Captions_are_day_labels()
    {
        await using var app = Gated();
        var window = app.ShowWindow(1100, 720);
        var (_, _, network) = await TwoDaysAsync(app, window);
        var canvas = CanvasOf(window);
        var today = SummaryNetworkSession.LocalDay(DateTimeOffset.Now);
        var expected = network.Session.DayStarts
            .Select(id => UiText.DayLabel(SummaryNetworkSession.LocalDay(network.Nodes.Single(n => n.Id == id).CreatedAt), today)).ToList();

        Assert.Equal(expected, network.Captions.Select(c => c.Text));
        Assert.Equal(["11月19日", "11月20日"], network.Captions.Select(c => c.Text));
        Assert.All(network.Captions, c => Assert.Equal([c.Anchor], c.Members));
        network.Session.Viewport = canvas.Current with { Zoom = 1 };
        Assert.Equal(expected, canvas.PaintCaptions().Select(c => c.Caption.Text));

        // Zoomed far out the day labels hide with the other labels.
        network.Session.Viewport = canvas.Current with { Zoom = SummaryNetworkCanvas.LabelHideScale };
        Assert.Empty(canvas.PaintCaptions());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hover_shows_the_date()
    {
        await using var app = Gated();
        var window = app.ShowWindow(1100, 720);
        var (_, evening, network) = await TwoDaysAsync(app, window);
        var canvas = CanvasOf(window);
        var dot = network.Nodes.Single(n => network.Session.Projection.Nodes.Single(p => p.Id == n.Id).Conversation == evening);
        var label = SummaryNetworkLayout.Label(dot.ShortTitle, dot.HasCandidate);

        Assert.Equal(label, canvas.PaintNodes().Single(p => p.Node.Id == dot.Id).Label);
        canvas.SetPointer(canvas.ToScreen(dot.X, dot.Y) + new Vector(2, 0));
        Assert.Equal(dot.Id, canvas.Hovered);

        var hovered = canvas.PaintNodes().Single(p => p.Node.Id == dot.Id);
        Assert.Equal(UiText.DotHoverLabel(label, dot.CreatedAt), hovered.Label);
        Assert.EndsWith("· 11月20日 21:38", hovered.Label, StringComparison.Ordinal);
        canvas.SetPointer(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_dot_is_not_dragged()
    {
        // Gated positions are computed, never saved: pressing a dot and moving leaves it where it is drawn.
        await using var app = Gated();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);
        var canvas = CanvasOf(window);
        var dot = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var start = canvas.ToScreen(dot.X, dot.Y);
        Point InWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;

        window.MouseMove(InWindow(start));
        window.MouseDown(InWindow(start), MouseButton.Left);
        window.MouseMove(InWindow(start + new Vector(30, 20)));
        window.MouseMove(InWindow(start + new Vector(60, 40)));

        Assert.Null(canvas.Dragging);
        var drawn = canvas.PaintNodes().Single(p => p.Node.Id == dot.Id);
        Assert.False(drawn.Lifted);
        AssertAtRest(start, drawn.Center);

        window.MouseUp(InWindow(start + new Vector(60, 40)), MouseButton.Left);

        Assert.Equal((dot.X, dot.Y), (network.Nodes.Single(n => n.Id == dot.Id).X, network.Nodes.Single(n => n.Id == dot.Id).Y));
        AssertAtRest(start, canvas.PaintNodes().Single(p => p.Node.Id == dot.Id).Center);
        Assert.False(network.IsDirty);
        Assert.Equal(dot.Id, network.Selected?.Id);
        window.Close();

        // Where it is drawn, give or take the decorative pull towards the pointer (at most 1.5 px); a drag would be 72 px off.
        static void AssertAtRest(Point expected, Point actual)
        {
            var offset = actual - expected;
            Assert.True(Math.Sqrt((offset.X * offset.X) + (offset.Y * offset.Y)) <= (5 * SummaryNetworkGeometry.AttractionStrength) + 0.01, $"the dot is drawn at {actual}, not {expected}");
        }
    }

    [AvaloniaFact]
    public async Task Timeline_labels_are_whole_and_never_meet_at_zoom_one()
    {
        // Review Focus 1: thirty-character titles on a shape like the owner's 数据挖掘 workspace (roots on two days, a
        // root with a fan and a chain, heavy dots, a summary ring). Every column is as wide as its widest label, so at
        // zoom 1 and above each label is whole and touches neither another label nor another dot.
        await using var app = Gated();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var all = new List<ConversationId>();
        for (var i = 0; i < 6; i++)
        {
            all.Add(await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, $"第{i}个关于蒸汽机车发明历史的问题？"));
        }

        var branch = await SummaryNetworkScenario.BranchAsync(app, conversations, all[0], "谁造了第一台？");
        all.Add(branch);
        all.Add(await SummaryNetworkScenario.BranchAsync(app, conversations, branch, "它跑多快？"));
        all.Add(await SummaryNetworkScenario.BranchAsync(app, conversations, branch, "铁轨用什么做？"));
        all.Add(await SummaryNetworkScenario.BranchAsync(app, conversations, all[0], "煤从哪里来？"));
        all.Add(await SummaryNetworkScenario.BranchAsync(app, conversations, all[2], "车厢怎么连接？"));
        await AddQuestionsAsync(app, all[1], 60);
        await AddQuestionsAsync(app, all[6], 30);
        await AddQuestionsAsync(app, all[3], 12);
        var year = DateTime.Now.Year;
        foreach (var (root, i) in all.Take(6).Select((c, i) => (c, i)))
        {
            await SetCreatedAsync(app, root, new DateTime(year, 11, i < 3 ? 19 : 20, 9 + i, 0, 0, DateTimeKind.Unspecified));
        }

        var titles = all.Select((c, i) => (c, $"第{i:00}段蒸汽机车与工业革命如何改变城市与乡村生活方式的长标题")).ToList();
        Assert.All(titles, t => Assert.Equal(30, t.Item2.Length));
        await SetTitlesAsync(app, titles);
        var summaries = app.Services.GetRequiredService<SummaryService>();
        app.Client.Knowledge.Replies.Enqueue("标题：铁路网络如何改变城市与乡村的生活\n短名：铁路网络如何改变城市与乡村\n\n正文");
        Assert.True((await summaries.SummarizeAsync(all[4], CancellationToken.None)).Accepted);
        await UiWait.UntilAsync(() => summaries.LatestTaskAsync(all[4], CancellationToken.None).Result is { State: GenerationTaskState.Completed });
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == all.Count && network.Nodes.Any(n => n.HasSummary));
        network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
        window.UpdateLayout();
        var canvas = CanvasOf(window);
        Assert.Null(network.Session.Selected);
        Assert.Contains(network.Nodes, n => n.Y < 0);
        Assert.Equal(2, network.Session.DayStarts.Count);
        Assert.Equal(1, canvas.Current.Zoom);

        // A canvas too narrow for a long title beside the right edge: the newest root's dot opens at 20% of the width and
        // its whole title is still in view (ruling R-T1a).
        var bounds = new Rect(canvas.Bounds.Size);
        var newest = network.Nodes.Where(n => n.IsRoot).MaxBy(n => n.X)!;
        Assert.True(newest.LabelWidth > bounds.Width * 0.6, $"canvas {bounds.Width} is wide enough for the end anchor");
        Assert.Equal(bounds.Width * SummaryNetworkGeometry.TimelineNarrowDotAt, canvas.ToScreen(newest.X, newest.Y).X, 3);
        Assert.True(bounds.Contains(canvas.PaintNodes().Single(p => p.Node.Id == newest.Id).LabelBounds), "the newest root's title is cut by the edge");
        Screenshots.Capture(window, "session-network-timeline-whole-titles");

        foreach (var zoom in new[] { 1.0, 1.6, Viewport.MaxZoom })
        {
            network.Session.Viewport = canvas.Current with { Zoom = zoom };
            var paints = canvas.PaintNodes();
            Assert.All(paints, p =>
            {
                Assert.NotNull(p.Text);
                Assert.DoesNotContain('…', p.Label);
                Assert.Equal(SummaryNetworkLayout.Label(p.Node.ShortTitle, p.Node.HasCandidate), p.Label);
            });
            Assert.Contains(paints, p => p.Label.Length == 30);
            AssertNoLabelCoversADot(canvas, $"zoom {zoom}");
            foreach (var (a, b) in paints.SelectMany((a, i) => paints.Skip(i + 1).Select(b => (a, b))))
            {
                Assert.False(a.LabelBounds.Intersects(b.LabelBounds), $"zoom {zoom}: \"{a.Label}\" at {a.LabelBounds} meets \"{b.Label}\" at {b.LabelBounds}");
            }
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_timeline_opens_at_zoom_one_on_the_newest_root()
    {
        // Review Focus 2 for the timeline (spec §5.3): a readable zoom on the newest end, not everything squeezed in.
        await using var app = Gated();

        // Wide enough that 显示全部 can fit six long-titled roots above the smallest zoom.
        var window = app.ShowWindow(1600, 900);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var first = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "火车是怎么发明的？");
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 1);
        network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
        window.UpdateLayout();
        var canvas = CanvasOf(window);
        var bounds = new Rect(canvas.Bounds.Size);

        // One session: zoom 1, the dot and its whole label in view.
        Assert.Equal(1, canvas.Current.Zoom);
        var only = canvas.PaintNodes().Single();
        Assert.True(bounds.Contains(only.Center), $"the dot at {only.Center} is outside {bounds}");
        Assert.True(bounds.Contains(only.LabelBounds), $"the label at {only.LabelBounds} is outside {bounds}");

        // Several sessions with long titles, wider than the canvas: zoom 1, the main line across the middle, and the newest
        // root's whole title in view, ending at about 92% of the width (ruling R-T1a).
        var roots = new List<ConversationId> { first };
        for (var i = 1; i < 6; i++)
        {
            roots.Add(await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, $"第{i}个问题？"));
        }

        await SetTitlesAsync(app, roots.Select((c, i) => (c, $"第{i:00}段蒸汽机车与工业革命如何改变城市与乡村生活方式的长标题")));
        app.ShellPage.ShowSectionCommand.Execute(Navigation.MainSection.Explore);
        network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == roots.Count && network.Nodes.All(n => n.ShortTitle.Length == 30));
        Assert.True(network.IsTimeline);
        canvas = CanvasOf(window);
        network.Session.Viewport = null;
        var opening = canvas.Current;
        var newest = network.Nodes.Where(n => n.IsRoot).MaxBy(n => n.X)!;
        var at = canvas.ToScreen(newest.X, newest.Y);
        Assert.Equal(1, opening.Zoom);
        Assert.True(newest.LabelWidth < bounds.Width * 0.6);
        Assert.Equal(bounds.Width * SummaryNetworkGeometry.TimelineLabelEndAt, at.X + SummaryNetworkGeometry.TimelineLabelReach + newest.LabelWidth, 3);
        Assert.True(bounds.Contains(canvas.PaintNodes().Single(p => p.Node.Id == newest.Id).LabelBounds), "the newest root's title is cut by the edge");
        Assert.True(at.X > bounds.Width * SummaryNetworkGeometry.TimelineNarrowDotAt);
        Assert.Equal(bounds.Height / 2, at.Y, 3);
        Assert.Contains(network.Nodes, n => !bounds.Contains(canvas.ToScreen(n.X, n.Y)));
        Screenshots.Capture(window, "session-network-timeline-opening");

        // 显示全部 still fits everything into view.
        network.ShowAllCommand.Execute(null);
        Assert.True(canvas.Current.Zoom < 1, $"show all at zoom {canvas.Current.Zoom}");
        Assert.All(canvas.PaintNodes(), p => Assert.True(bounds.Contains(p.Center), $"{p.Label} at {p.Center} is outside {bounds}"));

        // Leaving for the spatial view and coming back opens the timeline the same way again.
        network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Spatial);
        network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
        Assert.Equal(opening, canvas.Current);
        window.Close();
    }

    /// <summary>No resting label rectangle touches another node's dot, its summary ring included.</summary>
    private static void AssertNoLabelCoversADot(SummaryNetworkCanvas canvas, string when)
    {
        var paints = canvas.PaintNodes();
        Assert.Contains(paints, p => p.Text is not null);
        foreach (var label in paints.Where(p => p.Text is not null && !p.Hovered && p.Ring != NodeRing.Selected))
        {
            foreach (var dot in paints.Where(p => p.Node.Id != label.Node.Id))
            {
                var reach = dot.Radius + (dot.SummaryRing ? SummaryNetworkCanvas.SummaryRingGap + 0.75 : 0);
                var bounds = label.LabelBounds;
                var nearX = Math.Clamp(dot.Center.X, bounds.Left, bounds.Right);
                var nearY = Math.Clamp(dot.Center.Y, bounds.Top, bounds.Bottom);
                var distance = Math.Sqrt(Math.Pow(dot.Center.X - nearX, 2) + Math.Pow(dot.Center.Y - nearY, 2));
                Assert.True(distance > reach, $"{when}: the label \"{label.Label}\" at {bounds} covers the dot at {dot.Center} (r {reach})");
            }
        }
    }

    private static async Task SetTitlesAsync(DesktopHarness app, IEnumerable<(ConversationId Conversation, string Title)> titles)
    {
        var list = titles.ToList();
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => list.Sum(x => c.Execute(t, "UPDATE conversation SET title = $title, title_source = 'manual' WHERE id = $id;", ("$title", x.Title), ("$id", x.Conversation.ToString()))),
            CancellationToken.None);
    }

    /// <summary>
    /// Two explorations started on two days of this year: one at noon on 11-19, one at 21:38 on 11-20 (local), set on the
    /// conversations themselves so the day labels and the hover date are known. The network page is opened afterwards.
    /// </summary>
    private static async Task<(ConversationId Morning, ConversationId Evening, SummaryNetworkHomeViewModel Network)> TwoDaysAsync(DesktopHarness app, MainWindow window)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var first = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "火车是怎么发明的？");
        var second = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "蒸汽机怎么工作？");
        var year = DateTime.Now.Year;
        await SetCreatedAsync(app, first, new DateTime(year, 11, 19, 12, 0, 0, DateTimeKind.Unspecified));
        await SetCreatedAsync(app, second, new DateTime(year, 11, 20, 21, 38, 0, DateTimeKind.Unspecified));
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 2);
        return (first, second, network);
    }

    private static async Task SetCreatedAsync(DesktopHarness app, ConversationId conversation, DateTime local)
    {
        var at = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => c.Execute(t, "UPDATE conversation SET created_at = $at WHERE id = $id;", ("$at", at), ("$id", conversation.ToString())),
            CancellationToken.None);
    }

    /// <summary>Adds answered-looking user questions to a conversation directly in storage, so its weight grows without asking the model.</summary>
    private static async Task AddQuestionsAsync(DesktopHarness app, ConversationId conversation, int count) =>
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => Enumerable.Range(0, count).Sum(i => c.Execute(
                t,
                "INSERT INTO message (id, conversation_id, seq, role, state, created_at) VALUES ($id, $c, $seq, 'user', 'complete', 0);",
                ("$id", Guid.NewGuid().ToString()), ("$c", conversation.ToString()), ("$seq", 1000 + i))),
            CancellationToken.None);
}
