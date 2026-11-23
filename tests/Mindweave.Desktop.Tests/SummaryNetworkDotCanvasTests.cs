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

using Mindweave.Application.SummaryLinks;

using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
namespace Mindweave.Desktop.Tests;

/// <summary>
/// The network's drawing: dots sized by how much was asked, summaries wearing a ring, the arrowed
/// dotted time thread in the spatial view, the main line and curved lanes on the timeline, and day labels. Without the gate
/// the drawing is today's (<see cref="SummaryNetworkCanvasTests"/>).
/// </summary>
public class SummaryNetworkDotCanvasTests
{
    [Fact]
    public void Dot_radius_grows_with_the_square_root_of_the_questions_and_is_clamped()
    {
        Assert.Equal(5, SummaryNetworkGeometry.DotRadius(1));
        Assert.Equal(5, SummaryNetworkGeometry.DotRadius(0));
        Assert.Equal(11, SummaryNetworkGeometry.DotRadius(20), 0.05);
        Assert.Equal(14, SummaryNetworkGeometry.DotRadius(500));
        Assert.Equal(5, SummaryNetworkGeometry.DotFootprintRadius(1, hasSummary: false));
        Assert.Equal(5 + SummaryNetworkCanvas.SummaryRingGap + 1, SummaryNetworkGeometry.DotFootprintRadius(1, hasSummary: true));
    }

    [Fact]
    public void Day_labels_and_hover_labels_are_written_in_chinese()
    {
        Assert.Equal("11月20日", UiText.DayLabel(new DateOnly(2026, 11, 20), today: new DateOnly(2026, 11, 21)));
        Assert.Equal("2025年11月20日", UiText.DayLabel(new DateOnly(2025, 11, 20), today: new DateOnly(2026, 1, 2)));
        var local = new DateTime(2026, 11, 20, 21, 38, 0, DateTimeKind.Unspecified);
        var at = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
        Assert.Equal("蒸汽机 · 11月20日 21:38", UiText.DotHoverLabel("蒸汽机", at));
        Assert.Equal("Mindweave", UiText.SummaryNetworkTitle);
    }

    [AvaloniaFact]
    public async Task Dot_size_follows_weight()
    {
        await using var app = DesktopHarness.Create();
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
    public async Task One_conversation_opens_centred_at_zoom_one_in_the_spatial_view()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        await SummaryNetworkScenario.AskAsync(app, conversations, null, app.Workbench.CurrentWorkspace!.Id, "火车是怎么发明的？");
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);

        var opening = CanvasOf(window).Current;

        var only = Assert.Single(network.Nodes);
        Assert.Equal(1, opening.Zoom);
        Assert.Equal(only.X, opening.CenterX, 6);
        Assert.Equal(only.Y, opening.CenterY, 6);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_opening_view_follows_the_dots_until_the_reader_moves_the_view()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var conversation = await SummaryNetworkScenario.AskAsync(app, conversations, null, app.Workbench.CurrentWorkspace!.Id, "火车是怎么发明的？");
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        _ = canvas.Current;

        async Task RetitleAsync(string title)
        {
            var before = network.Nodes[0].LabelWidth;
            await app.Services.GetRequiredService<ConversationService>().RenameAsync(conversation, title, CancellationToken.None);
            await UiWait.UntilAsync(() => network.Nodes[0].LabelWidth != before);
            window.UpdateLayout();
        }

        await RetitleAsync("一个比之前长得多的新标题用来改变标签宽度");
        var opening = canvas.Current;
        Assert.Equal(1, opening.Zoom);
        Assert.Equal(network.Nodes[0].X, opening.CenterX, 6);
        Assert.Equal(network.Nodes[0].Y, opening.CenterY, 6);

        // Selecting a dot ends the following too: the dot the reader is looking at does not move away on a reload.
        network.Select(network.Nodes[0].Id);
        await RetitleAsync("选中之后再改标题");
        Assert.Equal(opening, canvas.Current);

        // A pan (what dragging sets) ends the following: the view stays where the reader put it.
        var panned = new Viewport(opening.CenterX + 120, opening.CenterY - 40, 1);
        network.Session.Viewport = panned;
        await RetitleAsync("短");
        Assert.Equal(panned, canvas.Current);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_timeline_keeps_its_day_captions()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);

        network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
        await UiWait.UntilAsync(() => network.IsTimeline);
        window.UpdateLayout();

        Assert.NotEmpty(CanvasOf(window).PaintCaptions());
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_show_all_request_does_not_outlive_an_empty_picture()
    {
        await using var app = DesktopHarness.Create();
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
    public async Task Summaries_wear_a_ring_and_the_unread_badge_stays_clear_of_it()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);
        var paints = CanvasOf(window).PaintNodes();

        Assert.Contains(paints, p => p.SummaryRing);
        Assert.Contains(paints, p => !p.SummaryRing);
        Assert.All(paints, p => Assert.Equal(p.Node.HasSummary, p.SummaryRing));

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
        await using var app = DesktopHarness.Create();
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
            var branch = palette.EdgePen(EdgeState.Normal);
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
        await using var app = DesktopHarness.Create();
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
            Screenshots.Capture(window, $"network-canvas-spatial-{theme.ToString().ToLowerInvariant()}");
        }

        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var spatial = canvas.PaintEdges();
        Assert.Single(spatial, e => e.Edge.Kind == SummaryNetworkEdgeKind.TimeThread);
        Assert.Equal(3, spatial.Count(e => e.Edge.Kind == SummaryNetworkEdgeKind.Branch));
        Assert.All(spatial, e =>
        {
            var thread = e.Edge.Kind == SummaryNetworkEdgeKind.TimeThread;
            Assert.Equal(thread, e.Arrow);
            Assert.False(e.Curved);
            Assert.False(e.Timeline);
        });

        // The timeline, switched as the page's 时间线 button does: the page rebuilds what it draws at once. The branches leave
        // the main line in curves; the grandchild continues its parent's lane in a straight line.
        network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
        Assert.All(network.Nodes, n => Assert.Equal(network.Session.Positions[n.Id], new Application.Storage.NodePosition(n.X, n.Y)));
        window.UpdateLayout();

        Screenshots.Capture(window, "network-canvas-timeline");
        var timeline = canvas.PaintEdges();
        Assert.All(timeline, e =>
        {
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
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (_, _, network) = await TwoDaysAsync(app, window);
        network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
        await UiWait.UntilAsync(() => network.IsTimeline);
        window.UpdateLayout();
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
        await using var app = DesktopHarness.Create();
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
    public async Task Placed_labels_lie_inside_their_hit_boxes_at_every_zoom()
    {
        // The spatial layout places each label round its dot and wraps it; the hit box must cover the label as drawn, at
        // zoom 1, zoomed in, and zoomed far out where only a hovered or selected label shows (review 2026-11-22).
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);
        var canvas = CanvasOf(window);
        Assert.All(network.Nodes, n => Assert.NotNull(n.Placed));

        foreach (var zoom in new[] { 1.0, 1.8, 0.6, SummaryNetworkCanvas.LabelHideScale })
        {
            network.Session.Viewport = new Viewport(network.Nodes.Average(n => n.X), network.Nodes.Average(n => n.Y), zoom);
            if (zoom <= SummaryNetworkCanvas.LabelHideScale)
            {
                network.Session.Selected = network.Nodes[0].Id;
            }

            foreach (var paint in canvas.PaintNodes().Where(p => p.Text is not null))
            {
                var box = SummaryNetworkGeometry.HitBox(paint.Center, paint.Node, zoom, labelVisible: true);
                Assert.True(box.Inflate(0.5).Contains(paint.LabelBounds), $"At zoom {zoom}, '{paint.Label}' drawn at {paint.LabelBounds} leaves its hit box {box}.");
            }
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_link_is_quiet_until_active_and_a_linked_dot_brings_out_its_partner()
    {
        // Design §8: a thin neutral dash with no word on it at rest; its note shows while it is active (an end selected or
        // hovered, or the line hovered); selecting a dot with links brings out its partner and path and fades the rest.
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);
        var speed = network.Nodes.Single(n => n.ShortTitle == "火箭号速度");
        var rails = network.Nodes.Single(n => n.ShortTitle == "铁轨");
        var middle = network.Nodes.Single(n => !n.HasSummary);
        await app.Services.GetRequiredService<SummaryLinkService>().CreateAsync(speed.Id, rails.Id, "对比", CancellationToken.None);
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 1);
        var canvas = CanvasOf(window);

        var resting = Assert.Single(canvas.PaintLinks());
        Assert.False(resting.Active);
        Assert.Null(resting.Label);
        Assert.DoesNotContain(canvas.PaintNodes(), p => p.Dimmed);

        // Selecting 铁轨: its partner and its path (the root) come out; the middle conversation, on neither, fades.
        network.Select(rails.Id);
        var linked = Assert.Single(canvas.PaintLinks());
        Assert.Equal("对比", linked.Label);
        Assert.False(linked.Dimmed);
        var nodes = canvas.PaintNodes().ToDictionary(p => p.Node.Id);
        Assert.False(nodes[speed.Id].Dimmed);
        Assert.True(nodes[speed.Id].Halo);
        Assert.True(nodes[middle.Id].Dimmed);
        Assert.Contains(canvas.PaintEdges(), e => e.Dimmed);

        // Hovering the line: its note shows and both ends come out; nothing fades without a selection.
        network.Select(null);
        var line = canvas.PaintLinks()[0];
        canvas.SetPointer(line.From + ((line.To - line.From) * 0.5));
        Assert.Equal(network.LinkEdges[0].Id, canvas.HoveredLink);
        Assert.Equal("对比", canvas.PaintLinks()[0].Label);
        var hovered = canvas.PaintNodes().ToDictionary(p => p.Node.Id);
        Assert.True(hovered[speed.Id].Halo && hovered[rails.Id].Halo);
        Assert.DoesNotContain(hovered.Values, p => p.Dimmed);
        canvas.SetPointer(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_change_in_one_workspace_reloads_only_that_workspaces_network()
    {
        // Design §15.3: with the switch on, every conversation change used to reload every visited workspace's network page.
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var trains = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => trains.Nodes.Count == 4);

        var other = await app.Services.GetRequiredService<WorkspaceService>().CreateAsync(WorkspaceName.FromStored("天文"), CancellationToken.None);
        app.Workbench.AddWorkspace(other);
        app.Workbench.Navigate(new NavigationRequest(MainSection.SummaryNetwork));
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is SummaryNetworkHomeViewModel { } page && page != trains);
        var stars = (SummaryNetworkHomeViewModel)app.Workbench.CurrentPage!;
        await stars.Loaded;
        Assert.NotSame(trains, stars);
        var trainLoads = trains.Loads;
        var starLoads = stars.Loads;

        // A question in the other workspace: its page reloads; the train workspace's page only learns it is not its own.
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var asked = await SummaryNetworkScenario.AskAsync(app, conversations, null, other.Id, "月亮为什么有阴晴圆缺？");
        await UiWait.UntilAsync(() => stars.Loads > starLoads && stars.Nodes.Count == 1 && trains.Knows(asked));
        Assert.Equal(trainLoads, trains.Loads);

        // Archiving it (a visibility change) reloads its own page only.
        starLoads = stars.Loads;
        Assert.True((await conversations.ArchiveAsync(asked, CancellationToken.None)).Archived);
        await UiWait.UntilAsync(() => stars.Loads > starLoads);
        Assert.Equal(trainLoads, trains.Loads);

        // A question in the train workspace still reloads its page.
        var root = trains.Session.Projection.Nodes.Single(n => n.ShortTitle == "火车起源").Conversation;
        await SummaryNetworkScenario.AskAsync(app, conversations, root, default, "再说说蒸汽机？");
        await UiWait.UntilAsync(() => trains.Loads > trainLoads);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_click_on_the_canvas_gives_it_the_keyboard()
    {
        // A click gives the canvas the keyboard, so Ctrl+S and Escape reach the page (review 2026-11-23: a helper named Focus()
        // had hidden the control's own; the press handler's call now reaches InputElement.Focus again).
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var canvas = CanvasOf(window);
        var corner = canvas.TranslatePoint(new Point(6, 6), window)!.Value;

        window.MouseDown(corner, MouseButton.Left);
        window.MouseUp(corner, MouseButton.Left);

        Assert.True(canvas.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Timeline_labels_are_whole_and_never_meet_at_zoom_one()
    {
        // Review Focus 1: thirty-character titles on a shape like the owner's 数据挖掘 workspace (roots on two days, a
        // root with a fan and a chain, heavy dots, a summary ring). Every column is as wide as its widest label, so at
        // zoom 1 and above each label is whole and touches neither another label nor another dot.
        await using var app = DesktopHarness.Create();
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

        // Too wide to fit: zoom 1 with the newest root's whole title in view, ending at about 92% of the width (ruling R-F1).
        var bounds = new Rect(canvas.Bounds.Size);
        var newest = canvas.PaintNodes().Where(p => p.Node.IsRoot).MaxBy(p => p.Node.X)!;
        Assert.True(bounds.Contains(newest.LabelBounds), "the newest root's title is cut by the edge");
        Assert.Equal(bounds.Width * SummaryNetworkGeometry.TimelineLabelEndAt, newest.LabelBounds.Right, 1);
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
        await using var app = DesktopHarness.Create();

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
        // root's whole title in view, ending at about 92% of the width (ruling R-F1).
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
        var title = canvas.PaintNodes().Single(p => p.Node.Id == newest.Id).LabelBounds;
        Assert.Equal(bounds.Width * SummaryNetworkGeometry.TimelineLabelEndAt, title.Right, 1);
        Assert.True(bounds.Contains(title), "the newest root's title is cut by the edge");
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

    /// <summary>
    /// Renames through the service, so the page hears ConversationChanged and reloads. Writing the table directly left the
    /// page stale unless a later auto-title event happened to reload it (a race that failed on the slower Windows runner).
    /// </summary>
    private static async Task SetTitlesAsync(DesktopHarness app, IEnumerable<(ConversationId Conversation, string Title)> titles)
    {
        var conversations = app.Services.GetRequiredService<ConversationService>();
        foreach (var (conversation, title) in titles)
        {
            await conversations.RenameAsync(conversation, title, CancellationToken.None);
        }
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

    internal static async Task SetCreatedAsync(DesktopHarness app, ConversationId conversation, DateTime local)
    {
        var at = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => c.Execute(t, "UPDATE conversation SET created_at = $at WHERE id = $id;", ("$at", at), ("$id", conversation.ToString())),
            CancellationToken.None);
    }

    /// <summary>Adds answered-looking user questions to a conversation directly in storage, so its weight grows without asking the model.</summary>
    internal static async Task AddQuestionsAsync(DesktopHarness app, ConversationId conversation, int count) =>
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => Enumerable.Range(0, count).Sum(i => c.Execute(
                t,
                "INSERT INTO message (id, conversation_id, seq, role, state, created_at) VALUES ($id, $c, $seq, 'user', 'complete', 0);",
                ("$id", Guid.NewGuid().ToString()), ("$c", conversation.ToString()), ("$seq", 1000 + i))),
            CancellationToken.None);
}
