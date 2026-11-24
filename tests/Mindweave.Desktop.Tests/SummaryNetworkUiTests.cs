using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
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
    public async Task Every_conversation_is_drawn_and_the_stored_places_come_back_after_a_restart()
    {
        const double Shift = 3000;
        var dir = Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N"));
        Dictionary<SummaryNodeId, NodePosition> first;
        SummaryNodeId moved;
        await using (var app = DesktopHarness.Create(directory: dir))
        {
            var window = app.ShowWindow(1100, 720);
            await SeedAsync(app, secondExploration: true);
            var network = await OpenSummaryNetworkAsync(app, window);

            Assert.Equal(5, network.Nodes.Count);
            Assert.Equal(4, network.Nodes.Count(n => n.HasSummary));
            Assert.Equal(4, network.Edges.Count);
            first = Positions(network);
            Screenshots.Capture(window, "07-network");

            // The second exploration's stored place is somewhere the layout would never put it.
            var root = network.Session.Projection.Nodes.Single(n => n.ShortTitle == "蒸汽机");
            moved = root.Id;
            await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
                (c, t) => c.Execute(t, "UPDATE exploration_place SET x = x + $shift WHERE root_conversation_id = $id;", ("$shift", Shift), ("$id", root.Conversation.ToString())),
                CancellationToken.None);
            window.Close();
        }

        await using var restarted = DesktopHarness.Create(directory: dir);
        var again = restarted.ShowWindow(1100, 720);
        await restarted.StartAsync();
        var reopened = await OpenSummaryNetworkAsync(restarted, again);
        Assert.Equal(first.Keys.ToHashSet(), reopened.Nodes.Select(n => n.Id).ToHashSet());
        Assert.All(reopened.Nodes, n =>
        {
            Assert.Equal(first[n.Id].X + (n.Id == moved ? Shift : 0), n.X, 6);
            Assert.Equal(first[n.Id].Y, n.Y, 6);
        });
        again.Close();
    }

    [AvaloniaFact]
    public async Task Entering_the_network_again_from_the_sidebar_opens_it_fitted()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenSummaryNetworkAsync(app, window);
        var canvas = window.GetVisualDescendants().OfType<SummaryNetworkCanvas>().Single();
        var opening = canvas.Current;
        network.ZoomInCommand.Execute(null);
        Assert.NotEqual(opening, canvas.Current);

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        await OpenSummaryNetworkAsync(app, window);

        Assert.Equal(opening, window.GetVisualDescendants().OfType<SummaryNetworkCanvas>().Single().Current);
        window.Close();
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
        var opening = window.GetVisualDescendants().OfType<SummaryNetworkCanvas>().Single().Current;
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
        window.UpdateLayout();
        Assert.Equal(opening, window.GetVisualDescendants().OfType<SummaryNetworkCanvas>().Single().Current);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quitting_after_opening_the_network_asks_nothing()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        await OpenSummaryNetworkAsync(app, window);
        Assert.Equal(1, await app.Services.GetRequiredService<SqliteDatabase>().ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM exploration_place;"), CancellationToken.None));

        window.Close();
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        Assert.False(app.Root.Exit.IsTaskPromptOpen);
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
        Assert.Contains(plain.Id, network.Matches);
        network.FindCommand.Execute(null);

        Assert.Equal(plain.Id, network.Selected?.Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_finds_a_summarized_conversation_by_every_title_it_has()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenSummaryNetworkAsync(app, window);
        var node = network.Session.Projection.Nodes.Single(n => n.ShortTitle == "铁轨");
        var title = (await app.Services.GetRequiredService<SummaryService>().GetAsync(node.Id, CancellationToken.None))!.Current.Title;
        await app.Services.GetRequiredService<ConversationService>().RenameAsync(node.Conversation, "会话自己的标题", CancellationToken.None);
        await app.Services.GetRequiredService<SummaryService>().RenameAsync(node.Id, "我起的名字", CancellationToken.None);
        await UiWait.UntilAsync(() => network.Session.Projection.Nodes.Any(n => n.DisplayName == "我起的名字"));

        // Its own name, the title of its conversation as the sidebar shows it, and the summary's own title (architecture §10.2).
        Assert.True(Found("我起的名字"));
        Assert.True(Found("会话自己的标题"));
        Assert.True(Found(title));

        // Enter goes to it too.
        network.Select(null);
        network.SearchText = "会话自己的标题";
        network.FindCommand.Execute(null);
        Assert.Equal(node.Id, network.Selected?.Id);

        bool Found(string text)
        {
            network.SearchText = text;
            return network.Matches.Contains(node.Id);
        }

        window.Close();
    }

    private static Dictionary<SummaryNodeId, NodePosition> Positions(SummaryNetworkHomeViewModel network) =>
        network.Nodes.ToDictionary(n => n.Id, n => new NodePosition(n.X, n.Y));
}
