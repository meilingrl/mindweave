using Avalonia;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Sqlite;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Where the timeline opens (ruling R-F1): fitted when the whole timeline fits at a readable zoom, as the spatial view;
/// otherwise at zoom 1 with the newest root's whole subtree in view, its last label ending at about 92% of the width,
/// or the newest root's dot at about 8% when that subtree is wider than the canvas. Label ends are the drawn ones.
/// </summary>
public class TimelineOpeningTests
{
    private static readonly Size Canvas = new(800, 600);

    private static NodeView Dot(double x, double y, bool root, SummaryNodeId? parent = null) =>
        new(SummaryNodeId.New(), "t", "t", x, y, false, false, 16, parent, root, HasSummary: false, Weight: 1);

    /// <summary>From a dot's centre to the end of its drawn label at zoom 1.</summary>
    private static double LabelEnd(NodeView node, double drawn) =>
        SummaryNetworkGeometry.DotRadius(node.Weight) + SummaryNetworkLayout.LabelGap + drawn;

    [Fact]
    public void A_timeline_that_fits_at_zoom_one_is_centred_at_zoom_one()
    {
        var a = Dot(0, 0, true);
        var b = Dot(200, 0, true);
        var opening = SummaryNetworkGeometry.TimelineOpening([a, b], Canvas, _ => 100);

        Assert.Equal(1, opening.Zoom);
        var left = 0 - SummaryNetworkGeometry.DotRadius(a.Weight) - SummaryNetworkCanvas.HoverGrowth;
        var right = 200 + LabelEnd(b, 100);
        Assert.Equal((left + right) / 2, opening.CenterX, 6);
    }

    [Fact]
    public void A_timeline_that_fits_only_below_zoom_one_is_fitted_at_a_readable_zoom()
    {
        var a = Dot(0, 0, true);
        var b = Dot(700, 0, true);
        var opening = SummaryNetworkGeometry.TimelineOpening([a, b], Canvas, _ => 100);

        Assert.InRange(opening.Zoom, SummaryNetworkGeometry.ReadableZoom, 0.999);
        var label = 100 * SummaryNetworkGeometry.LabelSize(opening.Zoom) / SummaryNetworkCanvas.LabelMaxSize;
        var left = -SummaryNetworkGeometry.DotRadius(a.Weight) - SummaryNetworkCanvas.HoverGrowth;
        var right = (700 * opening.Zoom) + SummaryNetworkGeometry.DotRadius(b.Weight) + SummaryNetworkLayout.LabelGap + label;
        Assert.Equal(Canvas.Width - (2 * Viewport.FitMargin), right - left, 0.5);
    }

    [Fact]
    public void A_wide_timeline_opens_with_the_newest_subtree_ending_at_92_percent()
    {
        var old = Dot(0, 0, true);
        var newest = Dot(2000, 0, true);
        var child = Dot(2200, 0, false, newest.Id);
        var high = Dot(2400, -60, false, child.Id);
        var low = Dot(2400, 0, false, child.Id);
        var widths = new Dictionary<SummaryNodeId, double> { [old.Id] = 50, [newest.Id] = 60, [child.Id] = 40, [high.Id] = 150, [low.Id] = 90 };
        var opening = SummaryNetworkGeometry.TimelineOpening([old, newest, child, high, low], Canvas, n => widths[n.Id]);

        Assert.Equal(1, opening.Zoom);
        double Screen(double x) => x - opening.CenterX + (Canvas.Width / 2);
        Assert.Equal(Canvas.Width * SummaryNetworkGeometry.TimelineLabelEndAt, Screen(2400) + LabelEnd(high, 150), 6);
        Assert.Equal(-30, opening.CenterY, 6);
    }

    [Fact]
    public void A_newest_subtree_wider_than_the_canvas_opens_with_its_root_at_8_percent()
    {
        var old = Dot(0, 0, true);
        var newest = Dot(2000, 0, true);
        var child = Dot(2600, 0, false, newest.Id);
        var opening = SummaryNetworkGeometry.TimelineOpening([old, newest, child], Canvas, _ => 300);

        Assert.Equal(1, opening.Zoom);
        Assert.Equal(Canvas.Width * SummaryNetworkGeometry.TimelineNarrowDotAt, 2000 - opening.CenterX + (Canvas.Width / 2), 6);
    }

    [AvaloniaFact]
    public async Task A_small_timeline_like_the_owners_opens_with_everything_in_view()
    {
        // The owner's Steins;Gate workspace: an older root, the newest root, its branch, and that branch's two branches, with
        // long Chinese titles. All of it fits at a readable zoom, so nothing is cut (trial finding A showed "LH").
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1400, 900);
        await app.StartWithWorkspaceAsync("命运石之门");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var older = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "现实黑客技术是什么？");
        var newest = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "命运石之门讲了什么？");
        var lhc = await SummaryNetworkScenario.BranchAsync(app, conversations, newest, "LHC是什么？");
        var higgs = await SummaryNetworkScenario.BranchAsync(app, conversations, lhc, "希格斯玻色子是什么？");
        var b = await SummaryNetworkScenario.BranchAsync(app, conversations, lhc, "B物理是什么？");
        await SetTitlesAsync(app, [(older, "现实黑客技术与影视夸张"), (newest, "《命运石之门》科学视角优缺点"), (lhc, "LHC是什么"), (higgs, "希格斯玻色子如何解释质量"), (b, "B物理与物质反物质不对称")]);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 5 && network.Nodes.Any(n => n.ShortTitle == "LHC是什么"));
        network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
        window.UpdateLayout();
        var canvas = CanvasOf(window);
        var bounds = new Rect(canvas.Bounds.Size);

        Assert.InRange(canvas.Current.Zoom, SummaryNetworkGeometry.ReadableZoom, 1);
        AssertInView(bounds, canvas.PaintNodes());
        Assert.All(canvas.PaintCaptions(), c => Assert.True(bounds.Contains(c.Bounds), $"the date at {c.Bounds} is outside {bounds}"));
        Screenshots.Capture(window, "session-network-timeline-small-opening");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_wide_timeline_opens_with_the_newest_subtree_whole()
    {
        // Five older explorations with long titles make the timeline too wide to fit; the newest exploration's root, its
        // branch and that branch's two branches still open whole, at zoom 1, ending at about 92% of the width.
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1400, 900);
        await app.StartWithWorkspaceAsync("命运石之门");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var titles = new List<(ConversationId, string)>();
        for (var i = 0; i < 5; i++)
        {
            titles.Add((await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, $"第{i}个问题？"), $"第{i:00}段蒸汽机车与工业革命如何改变城市与乡村生活方式的长标题"));
        }

        var newest = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "命运石之门讲了什么？");
        var lhc = await SummaryNetworkScenario.BranchAsync(app, conversations, newest, "LHC是什么？");
        var higgs = await SummaryNetworkScenario.BranchAsync(app, conversations, lhc, "希格斯玻色子是什么？");
        var b = await SummaryNetworkScenario.BranchAsync(app, conversations, lhc, "B物理是什么？");
        titles.AddRange([(newest, "《命运石之门》科学视角优缺点"), (lhc, "LHC是什么"), (higgs, "希格斯玻色子如何解释质量"), (b, "B物理与物质反物质不对称")]);
        await SetTitlesAsync(app, titles);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 9 && network.Nodes.Any(n => n.ShortTitle == "LHC是什么"));
        network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
        window.UpdateLayout();
        var canvas = CanvasOf(window);
        var bounds = new Rect(canvas.Bounds.Size);
        var subtree = new[] { newest, lhc, higgs, b }.Select(c => network.Session.Projection.Nodes.Single(n => n.Conversation == c).Id).ToHashSet();
        var paints = canvas.PaintNodes();

        Assert.Equal(1, canvas.Current.Zoom);
        Assert.Contains(paints, p => !bounds.Contains(p.LabelBounds));
        AssertInView(bounds, paints.Where(p => subtree.Contains(p.Node.Id)));
        Assert.Equal(bounds.Width * SummaryNetworkGeometry.TimelineLabelEndAt, paints.Where(p => subtree.Contains(p.Node.Id)).Max(p => p.LabelBounds.Right), 1);
        Screenshots.Capture(window, "session-network-timeline-wide-opening");
        window.Close();
    }

    private static void AssertInView(Rect bounds, IEnumerable<NodePaint> paints) => Assert.All(paints, p =>
    {
        Assert.True(bounds.Contains(p.Center), $"the dot of \"{p.Label}\" at {p.Center} is outside {bounds}");
        Assert.True(bounds.Contains(p.LabelBounds), $"the label \"{p.Label}\" at {p.LabelBounds} is outside {bounds}");
    });

    private static async Task SetTitlesAsync(DesktopHarness app, IEnumerable<(ConversationId Conversation, string Title)> titles)
    {
        var list = titles.ToList();
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => list.Sum(x => c.Execute(t, "UPDATE conversation SET title = $title, title_source = 'manual' WHERE id = $id;", ("$title", x.Title), ("$id", x.Conversation.ToString()))),
            CancellationToken.None);
    }
}
