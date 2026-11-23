using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Sqlite;

using static Mindweave.Desktop.Tests.SummaryNetworkScenario;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The network as the reader sees it (requirements §7.3–7.4): the picture comes back after a restart, leaving and
/// quitting never ask about it, the viewport survives reading a summary, and search reaches every conversation.
/// </summary>
public class SummaryNetworkUiTests
{
    [AvaloniaFact]
    public async Task Every_conversation_is_drawn_and_the_picture_comes_back_after_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N"));
        Dictionary<SummaryNodeId, NodePosition> first;
        await using (var app = DesktopHarness.Create(directory: dir))
        {
            var window = app.ShowWindow(1100, 720);
            await SeedAsync(app);
            var network = await OpenSummaryNetworkAsync(app, window);

            Assert.Equal(4, network.Nodes.Count);
            Assert.Equal(3, network.Nodes.Count(n => n.HasSummary));
            Assert.Equal(3, network.Edges.Count);
            first = Positions(network);
            Screenshots.Capture(window, "07-network");
            window.Close();
        }

        await using var restarted = DesktopHarness.Create(directory: dir);
        var again = restarted.ShowWindow(1100, 720);
        await restarted.StartAsync();
        var reopened = await OpenSummaryNetworkAsync(restarted, again);
        Assert.Equal(first.Keys.ToHashSet(), reopened.Nodes.Select(n => n.Id).ToHashSet());
        Assert.All(reopened.Nodes, n =>
        {
            Assert.Equal(first[n.Id].X, n.X, 6);
            Assert.Equal(first[n.Id].Y, n.Y, 6);
        });
        again.Close();
    }

    [AvaloniaFact]
    public async Task Switching_workspace_after_opening_the_network_asks_nothing_and_coming_back_fits_again()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenSummaryNetworkAsync(app, window);
        var trains = app.Workbench.CurrentWorkspace!;
        var before = Positions(network);
        var other = await app.Services.GetRequiredService<Application.Workspaces.WorkspaceService>().CreateAsync(WorkspaceName.FromStored("摄影"), CancellationToken.None);
        app.Workbench.Workspaces.Add(other);
        network.ZoomInCommand.Execute(null);
        Assert.NotNull(network.Session.Viewport);

        app.ShellPage.SelectedWorkspace = other;
        Assert.Equal(other.Id, app.Workbench.CurrentWorkspace!.Id);
        Assert.Null(app.Services.GetRequiredService<SummaryNetworkSessions>().Get(trains.Id).Viewport);

        app.ShellPage.SelectedWorkspace = trains;
        app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
        var back = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
        await back.Loaded;
        Assert.Equal(before, Positions(back));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quitting_after_opening_the_network_asks_nothing()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        await OpenSummaryNetworkAsync(app, window);

        window.Close();
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Selecting_a_node_does_not_read_it_and_the_viewport_survives_reading_the_summary()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenSummaryNetworkAsync(app, window);
        var canvas = window.GetVisualDescendants().OfType<SummaryNetworkCanvas>().Single();
        var node = network.Nodes[0];

        network.Select(node.Id);
        Assert.Equal(node.DisplayName, network.Selected!.DisplayName);
        Assert.Contains(UiText.MarkerUnread, network.Selected.Markers, StringComparison.Ordinal);
        Assert.Same(node, canvas.HitTest(canvas.ToScreen(node.X, node.Y)));

        network.ZoomInCommand.Execute(null);
        var viewport = network.Session.Viewport;
        network.OpenSelectedCommand.Execute(null);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        app.ShellPage.GoBackCommand.Execute(null);

        var back = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(viewport, back.Session.Viewport);
        Assert.Equal(node.Id, back.Selected?.Id);
        Assert.True((await app.Services.GetRequiredService<SummaryService>().GetAsync(node.Id, CancellationToken.None))!.Markers.Unread);

        // Show all fits the view again without moving any node.
        var before = back.Nodes.ToDictionary(n => n.Id, n => new NodePosition(n.X, n.Y));
        back.ShowAllCommand.Execute(null);
        window.UpdateLayout();
        Assert.NotEqual(viewport, canvas.Current);
        Assert.Equal(before, back.Nodes.ToDictionary(n => n.Id, n => new NodePosition(n.X, n.Y)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_selects_the_first_match_and_brings_it_to_the_centre()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenSummaryNetworkAsync(app, window);
        var target = network.Nodes.Single(n => n.ShortTitle == "铁轨");

        network.SearchText = "铁轨";
        network.FindCommand.Execute(null);

        Assert.Equal(target.Id, network.Selected?.Id);
        Assert.Equal((target.X, target.Y), (network.Session.Viewport!.Value.CenterX, network.Session.Viewport.Value.CenterY));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_also_finds_a_conversation_without_a_summary()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenSummaryNetworkAsync(app, window);
        var plain = network.Nodes.Single(n => !n.HasSummary);

        network.SearchText = plain.ShortTitle;
        network.FindCommand.Execute(null);

        Assert.Equal(plain.Id, network.Selected?.Id);
        window.Close();
    }

    private static Dictionary<SummaryNodeId, NodePosition> Positions(SummaryNetworkHomeViewModel network) =>
        network.Nodes.ToDictionary(n => n.Id, n => new NodePosition(n.X, n.Y));
}
