using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Mindweave.Application.Languages;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The summary network across a language switch: the view shown, the pan and zoom and the selected dot
/// live in the workspace's network session, a root service, so the rebuilt page comes back to them; the page's own
/// listening to the conversations stops with the old view.
/// </summary>
public partial class LanguageSwitchTests
{
    [AvaloniaFact]
    public async Task Switch_keeps_the_session_network_view_viewport_and_selection()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 720);
            var (summarized, _, _) = await SummaryNetworkPageTests.SeedAsync(app);
            var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
            await UiWait.UntilAsync(() => network.Nodes.Count == 3);
            window.UpdateLayout();
            UiInput.Click(window, Find<RadioButton>(PageViewOf(window), "TimelineViewButton"));
            window.UpdateLayout();
            Assert.True(network.IsTimeline);
            var dot = network.Session.Projection.Nodes.Single(n => n.Conversation == summarized).Id;
            network.Select(dot);
            var panned = new Viewport(123, -45, 1.7);
            network.Session.Viewport = panned;

            await SwitchAsync(app, UiLanguage.English);

            var again = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(network, again);
            await again.Loaded;
            window.UpdateLayout();
            var view = PageViewOf(window);
            Assert.True(again.IsTimeline);
            Assert.Equal(SummaryNetworkViewMode.Timeline, again.Session.ViewMode);
            Assert.True(Find<RadioButton>(view, "TimelineViewButton").IsChecked);
            Assert.Equal(panned, CanvasOf(window).Current);
            Assert.Equal(dot, again.Selected?.Id);
            LegendCard.Toggle(window);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Solid line: branch" && t.IsEffectivelyVisible);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [AvaloniaFact]
    public async Task Switch_leaves_no_handler_of_the_gated_network_page_behind()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 720);
            await SummaryNetworkPageTests.SeedAsync(app);
            var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
            await network.Loaded;
            var before = ViewRebuildTests.HandlerCounts(app.Session.Root);

            await SwitchAsync(app, UiLanguage.English);
            var again = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
            await again.Loaded;

            // One page listens, as before: the old page let go of the conversations' changes it listened to behind the gate.
            ViewRebuildTests.AssertSame(before, ViewRebuildTests.HandlerCounts(app.Session.Root));
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    private static SummaryNetworkHomeView PageViewOf(Window window) => window.GetVisualDescendants().OfType<SummaryNetworkHomeView>().Single();
}
