using Avalonia;
using Avalonia.Headless.XUnit;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// What keeps clear of what in the session network, measured against what the canvas draws: recommended nodes avoid big
/// dots, their rings and the dates (final review M4).
/// </summary>
public class SessionNetworkClearanceTests
{
    [AvaloniaTheory]
    [InlineData(SummaryNetworkViewMode.Spatial)]
    [InlineData(SummaryNetworkViewMode.Timeline)]
    public async Task Recommendation_obstacles_cover_every_drawn_dot_ring_and_date(SummaryNetworkViewMode mode)
    {
        await using var app = SessionNetworkUiTests.Gated();
        var window = app.ShowWindow(1100, 720);
        var (network, canvas) = await HeavyTwoDaysAsync(app, window, mode);
        network.Session.Viewport = canvas.Current with { Zoom = 1 };

        var boxes = SummaryNetworkObstacles.Of(network.Nodes, network.Captions, network.IsSessionNetwork, network.IsTimeline);
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
        var (summarized, _, plain) = await SessionNetworkUiTests.SeedAsync(app);
        var year = DateTime.Now.Year;
        await SessionNetworkCanvasTests.SetCreatedAsync(app, summarized, new DateTime(year, 11, 19, 12, 0, 0, DateTimeKind.Unspecified));
        await SessionNetworkCanvasTests.SetCreatedAsync(app, plain, new DateTime(year, 11, 20, 21, 38, 0, DateTimeKind.Unspecified));
        await SessionNetworkCanvasTests.AddQuestionsAsync(app, summarized, 99);
        await SessionNetworkCanvasTests.AddQuestionsAsync(app, plain, 49);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 3 && network.Nodes.All(n => n.Weight > 1 || !n.IsRoot));
        network.SetViewModeCommand.Execute(mode);
        await UiWait.UntilAsync(() => network.IsTimeline == (mode == SummaryNetworkViewMode.Timeline));
        window.UpdateLayout();
        Assert.Equal(2, network.Captions.Count);
        return (network, CanvasOf(window));
    }
}
