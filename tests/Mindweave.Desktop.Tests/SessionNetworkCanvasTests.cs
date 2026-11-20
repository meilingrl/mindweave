using Avalonia;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
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
        });

        // The timeline: the view switches, the page reads the network again. The branches leave the main line in curves;
        // the grandchild continues its parent's lane in a straight line.
        network.Session.SetViewMode(SummaryNetworkViewMode.Timeline);
        network.Session.DiscardChanges();
        await UiWait.UntilAsync(() => network.Nodes.All(n => network.Session.Positions.TryGetValue(n.Id, out var p) && p.X == n.X && p.Y == n.Y));
        window.UpdateLayout();

        Screenshots.Capture(window, "session-network-canvas-timeline");
        var timeline = canvas.PaintEdges();
        Assert.All(timeline, e =>
        {
            Assert.False(e.Dashed);
            Assert.False(e.Arrow);
            Assert.Equal(e.Edge.Kind == SummaryNetworkEdgeKind.Branch && e.Edge.FromY != e.Edge.ToY, e.Curved);
        });
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
