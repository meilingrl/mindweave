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
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// What keeps clear of what in the network, measured against what the canvas draws: recommended nodes avoid big
/// dots, their rings and the dates (final review M4), and a big dot
/// with its ring is clickable at every zoom (M5), and
/// a hovered label with its date covers no other dot (M6).
/// </summary>
public class SummaryNetworkClearanceTests
{
    [AvaloniaTheory]
    [InlineData(SummaryNetworkViewMode.Spatial)]
    [InlineData(SummaryNetworkViewMode.Timeline)]
    public async Task Recommendation_obstacles_cover_every_drawn_dot_ring_and_date(SummaryNetworkViewMode mode)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (network, canvas) = await HeavyTwoDaysAsync(app, window, mode);
        network.Session.Viewport = canvas.Current with { Zoom = 1 };

        var boxes = SummaryNetworkObstacles.Of(network.Nodes, network.Captions, network.IsTimeline);
        var captions = canvas.PaintCaptions();
        Assert.NotEmpty(captions);
        foreach (var caption in captions)
        {
            AssertCovered(boxes, canvas, caption.Bounds, $"the date \"{caption.Caption.Text}\"");
        }

        foreach (var dot in canvas.PaintNodes())
        {
            var reach = dot.Radius + (dot.SummaryRing ? SummaryNetworkCanvas.SummaryRingGap + 1 : 0);
            AssertCovered(boxes, canvas, new Rect(dot.Center - new Vector(reach, reach), new Size(2 * reach, 2 * reach)), $"the dot of \"{dot.Label}\"");
        }

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(SummaryNetworkViewMode.Spatial)]
    [InlineData(SummaryNetworkViewMode.Timeline)]
    public async Task A_big_dot_and_its_ring_are_clickable_at_every_zoom(SummaryNetworkViewMode mode)
    {
        // Final review M5: below zoom 0.64 the hit box shrank with the zoom while a heavy dot is still drawn 14 px wide
        // with its ring at 18.5 px, so a click on the outer part of the dot or ring missed.
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (network, canvas) = await HeavyTwoDaysAsync(app, window, mode);

        foreach (var zoom in new[] { Viewport.MinZoom, SummaryNetworkCanvas.LabelHideScale, 0.6, SummaryNetworkGeometry.ReadableZoom, 1, Viewport.MaxZoom })
        {
            network.Session.Viewport = canvas.Current with { Zoom = zoom };
            var paints = canvas.PaintNodes();
            double Reach(NodePaint p) => p.Radius + (p.SummaryRing ? SummaryNetworkCanvas.SummaryRingGap + 1 : 0);
            foreach (var dot in paints)
            {
                for (var step = 0; step < 8; step++)
                {
                    var angle = step * Math.PI / 4;
                    var point = dot.Center + (new Vector(Math.Cos(angle), Math.Sin(angle)) * (Reach(dot) - 0.5));
                    if (paints.Any(o => o.Node.Id != dot.Node.Id && Distance(point, o.Center) <= Reach(o)))
                    {
                        continue;
                    }

                    Assert.True(dot.Node.Id == canvas.HitTest(point)?.Id, $"zoom {zoom}: a click at {point} on the edge of \"{dot.Label}\" (r {Reach(dot)}) misses it");
                }
            }
        }

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(SummaryNetworkViewMode.Timeline)]
    [InlineData(SummaryNetworkViewMode.Spatial)]
    public async Task A_hovered_label_with_its_date_never_covers_another_dot(SummaryNetworkViewMode mode)
    {
        // Final review M6: one-character titles get the narrowest timeline columns, so " · 11月20日 21:38" after a hovered
        // title ran over the next column's dot. At zoom 1 and above a hovered label, date included, covers no other dot.
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var roots = new List<ConversationId>();
        foreach (var title in new[] { "甲", "乙", "丙", "丁" })
        {
            var root = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, title + "是什么？");
            await conversations.RenameAsync(root, title, CancellationToken.None);
            roots.Add(root);
        }

        roots.Add(await SummaryNetworkScenario.BranchAsync(app, conversations, roots[1], "乙的分支？"));
        await conversations.RenameAsync(roots[^1], "戊", CancellationToken.None);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 5 && network.Nodes.All(n => n.ShortTitle.Length == 1));
        network.SetViewModeCommand.Execute(mode);
        await UiWait.UntilAsync(() => network.IsTimeline == (mode == SummaryNetworkViewMode.Timeline));
        window.UpdateLayout();
        var canvas = CanvasOf(window);

        foreach (var zoom in new[] { 1.0, 1.6, Viewport.MaxZoom })
        {
            network.Session.Viewport = canvas.Current with { Zoom = zoom };
            foreach (var node in network.Nodes)
            {
                canvas.SetPointer(canvas.ToScreen(node.X, node.Y));
                Assert.Equal(node.Id, canvas.Hovered);
                var paints = canvas.PaintNodes();
                var hovered = paints.Single(p => p.Node.Id == node.Id);
                if (zoom == 1 && node == network.Nodes[0])
                {
                    Screenshots.Capture(window, $"session-network-hover-date-{mode.ToString().ToLowerInvariant()}");
                }

                // The date is shown: after the title, or on a line of its own.
                var dated = hovered.Label.Contains(UiText.DotHoverDate(node.CreatedAt), StringComparison.Ordinal);
                Assert.True(dated || hovered.DateText is not null, $"zoom {zoom}: \"{hovered.Label}\" shows no date");
                var rects = hovered.DateText is null ? new[] { hovered.LabelBounds } : [hovered.LabelBounds, hovered.DateBounds];
                foreach (var (dot, rect) in paints.Where(p => p.Node.Id != node.Id).SelectMany(dot => rects.Select(rect => (dot, rect))))
                {
                    var reach = dot.Radius + (dot.SummaryRing ? SummaryNetworkCanvas.SummaryRingGap + 1 : 0);
                    var nearest = new Point(Math.Clamp(dot.Center.X, rect.Left, rect.Right), Math.Clamp(dot.Center.Y, rect.Top, rect.Bottom));
                    Assert.True(Distance(nearest, dot.Center) > reach, $"zoom {zoom}: the hovered label \"{hovered.Label}\" at {rect} covers the dot of \"{dot.Label}\" at {dot.Center}");
                }
            }
        }

        canvas.SetPointer(null);
        window.Close();
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>A screen rectangle lies inside one obstacle box (canvas units).</summary>
    private static void AssertCovered(List<CanvasBox> boxes, SummaryNetworkCanvas canvas, Rect screen, string what)
    {
        var (left, top) = canvas.ToCanvas(screen.TopLeft);
        var (right, bottom) = canvas.ToCanvas(screen.BottomRight);
        Assert.True(
            boxes.Any(b => b.Left <= left + 0.01 && b.Top <= top + 0.01 && b.Right >= right - 0.01 && b.Bottom >= bottom - 0.01),
            $"{what} at ({left:F1}, {top:F1}, {right:F1}, {bottom:F1}) is in no obstacle");
    }

    /// <summary>
    /// A heavy summarised root on 11-19 with a branch, and a heavy plain root on 11-20: big dots, a ring and two dates.
    /// </summary>
    private static async Task<(SummaryNetworkHomeViewModel Network, SummaryNetworkCanvas Canvas)> HeavyTwoDaysAsync(DesktopHarness app, MainWindow window, SummaryNetworkViewMode mode)
    {
        var (summarized, _, plain) = await SummaryNetworkPageTests.SeedAsync(app);
        var year = DateTime.Now.Year;
        await SummaryNetworkDotCanvasTests.SetCreatedAsync(app, summarized, new DateTime(year, 11, 19, 12, 0, 0, DateTimeKind.Unspecified));
        await SummaryNetworkDotCanvasTests.SetCreatedAsync(app, plain, new DateTime(year, 11, 20, 21, 38, 0, DateTimeKind.Unspecified));
        await SummaryNetworkDotCanvasTests.AddQuestionsAsync(app, summarized, 99);
        await SummaryNetworkDotCanvasTests.AddQuestionsAsync(app, plain, 49);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 3 && network.Nodes.All(n => n.Weight > 1 || !n.IsRoot));
        network.SetViewModeCommand.Execute(mode);
        await UiWait.UntilAsync(() => network.IsTimeline == (mode == SummaryNetworkViewMode.Timeline));
        window.UpdateLayout();
        Assert.Equal(2, network.Captions.Count);
        return (network, CanvasOf(window));
    }
}
