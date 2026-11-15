using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Sqlite;

using static Mindweave.Desktop.Tests.SummaryNetworkScenario;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// S5 as the reader sees it (requirement §11 layout rows, §7.4 reliability): new summaries are placed and saved
/// directly; an arrangement is saved only explicitly and survives a restart; a failed save keeps everything; changes
/// made while saving stay unsaved; leaving asks first; the viewport survives reading a summary.
/// </summary>
public class SummaryNetworkUiTests
{
    [AvaloniaFact]
    public async Task New_summaries_are_placed_and_saved_directly_and_come_back_after_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N"));
        IReadOnlyDictionary<SummaryNodeId, NodePosition> first;
        await using (var app = DesktopHarness.Create(directory: dir))
        {
            var window = app.ShowWindow(1100, 720);
            await SeedAsync(app);
            var network = await OpenSummaryNetworkAsync(app, window);

            Assert.Equal(3, network.Nodes.Count);
            Assert.Equal(2, network.Edges.Count);
            Assert.Contains(network.Edges, e => !e.IsDirect && e.Label == UiText.ViaCount(1));
            Assert.False(network.IsDirty);
            first = await Layout(app);
            Assert.Equal(3, first.Count);
            Screenshots.Capture(window, "07-network");
            window.Close();
        }

        await using var restarted = DesktopHarness.Create(directory: dir);
        var again = restarted.ShowWindow(1100, 720);
        await restarted.StartAsync();
        var reopened = await OpenSummaryNetworkAsync(restarted, again);
        Assert.Equal(first, reopened.Nodes.ToDictionary(n => n.Id, n => new NodePosition(n.X, n.Y)));
        Assert.False(reopened.IsDirty);
        again.Close();
    }

    [AvaloniaFact]
    public async Task An_arrangement_is_unsaved_until_saved_and_a_failed_save_keeps_it_in_the_window()
    {
        var gate = new GatedStore();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<ISummaryNetworkStore>(sp => gate.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenSummaryNetworkAsync(app, window);
        await MoveSomethingAsync(app, network);

        network.ArrangeCommand.Execute(null);
        Assert.True(network.IsDirty);
        Assert.True(network.SaveLayoutCommand.CanExecute(null));
        var arranged = network.Nodes.ToDictionary(n => n.Id, n => new NodePosition(n.X, n.Y));

        gate.Fail = true;
        await network.SaveLayoutCommand.ExecuteAsync(null);
        Assert.True(network.SaveFailed);
        Assert.True(network.IsDirty);
        Assert.Equal(arranged, network.Nodes.ToDictionary(n => n.Id, n => new NodePosition(n.X, n.Y)));
        Assert.NotEqual(arranged, await Layout(app));

        gate.Fail = false;
        await network.SaveLayoutCommand.ExecuteAsync(null);
        Assert.False(network.SaveFailed);
        Assert.False(network.IsDirty);
        Assert.Equal(arranged, await Layout(app));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arranging_again_while_saving_stays_unsaved()
    {
        var gate = new GatedStore();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<ISummaryNetworkStore>(sp => gate.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenSummaryNetworkAsync(app, window);

        network.ArrangeCommand.Execute(null);
        gate.Hold = new TaskCompletionSource();
        var saving = network.SaveLayoutCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => network.IsSaving);
        network.ArrangeCommand.Execute(null);
        gate.Hold.TrySetResult();
        await saving;

        Assert.True(network.IsDirty);
        Assert.False(network.SaveFailed);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Leaving_the_workspace_with_an_unsaved_layout_asks_first()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenSummaryNetworkAsync(app, window);
        var trains = app.Workbench.CurrentWorkspace!;
        var other = await app.Services.GetRequiredService<Application.Workspaces.WorkspaceService>().CreateAsync(WorkspaceName.FromStored("摄影"), CancellationToken.None);
        app.Workbench.Workspaces.Add(other);

        await MoveSomethingAsync(app, network);
        var saved = await Layout(app);
        network.ArrangeCommand.Execute(null);
        app.ShellPage.SelectedWorkspace = other;
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        Assert.Equal(trains.Id, app.Workbench.CurrentWorkspace!.Id);

        app.Root.LayoutPrompt.CancelCommand.Execute(null);
        Assert.Equal(trains.Id, app.Workbench.CurrentWorkspace!.Id);
        Assert.True(network.IsDirty);

        app.ShellPage.SelectedWorkspace = other;
        app.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        Assert.Equal(other.Id, app.Workbench.CurrentWorkspace!.Id);
        Assert.Equal(saved, await Layout(app, trains.Id));

        // Back in the first workspace: the saved positions, fitted into view again (the viewport is not kept).
        app.ShellPage.SelectedWorkspace = trains;
        app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
        var back = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
        await back.Loaded;
        Assert.False(back.IsDirty);
        await UiWait.UntilAsync(() => back.Nodes.ToDictionary(n => n.Id, n => new NodePosition(n.X, n.Y)).OrderBy(p => p.Key.Value).SequenceEqual(saved.OrderBy(p => p.Key.Value)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quitting_with_an_unsaved_layout_asks_first_and_saving_then_quits()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenSummaryNetworkAsync(app, window);
        await MoveSomethingAsync(app, network);
        network.ArrangeCommand.Execute(null);
        var arranged = network.Nodes.ToDictionary(n => n.Id, n => new NodePosition(n.X, n.Y));
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        Assert.False(closed);
        app.Root.LayoutPrompt.CancelCommand.Execute(null);
        Assert.False(closed);

        window.Close();
        await app.Root.LayoutPrompt.SaveAndContinueCommand.ExecuteAsync(null);
        await app.Root.Exit.Running;
        Assert.Equal(1, app.Shell.ExitRequests);
        Assert.Equal(arranged, await Layout(app));

        // The coordinator's own shutdown closes the window; that close is let through.
        window.Close();
        Assert.True(closed);
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
        Assert.False(back.IsDirty);
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

    private static Task<IReadOnlyDictionary<SummaryNodeId, NodePosition>> Layout(DesktopHarness app, WorkspaceId? workspace = null) =>
        app.Services.GetRequiredService<ISummaryNetworkStore>().GetLayoutAsync(workspace ?? app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);

    /// <summary>The real layout store, with explicit saves that can fail or wait on demand.</summary>
    private sealed class GatedStore
    {
        public bool Fail { get; set; }

        public TaskCompletionSource? Hold { get; set; }

        public ISummaryNetworkStore Wrap(SqliteDatabase database) => new Store(new SqliteSummaryNetworkStore(database), this);

        private sealed class Store(ISummaryNetworkStore inner, GatedStore gate) : ISummaryNetworkStore
        {
            public Task<IReadOnlyDictionary<SummaryNodeId, NodePosition>> GetLayoutAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
                inner.GetLayoutAsync(workspace, cancellationToken);

            public Task<bool> PlaceAsync(WorkspaceId workspace, SummaryNodeId node, NodePosition position, DateTimeOffset at, CancellationToken cancellationToken) =>
                inner.PlaceAsync(workspace, node, position, at, cancellationToken);

            public async Task<int> SaveLayoutAsync(WorkspaceId workspace, IReadOnlyDictionary<SummaryNodeId, NodePosition> positions, DateTimeOffset at, CancellationToken cancellationToken)
            {
                if (gate.Hold is { } hold)
                {
                    await hold.Task;
                }

                return gate.Fail ? throw new IOException("simulated layout save failure") : await inner.SaveLayoutAsync(workspace, positions, at, cancellationToken);
            }

            public Task<IReadOnlyDictionary<ConversationId, IReadOnlyList<ConversationId>>> AncestorChainsAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
                inner.AncestorChainsAsync(workspace, cancellationToken);
        }
    }
}
