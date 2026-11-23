using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Recommended nodes on the summary network (requirements §10.3.2, 2026-11-12) in the real window: drawn dashed with the
/// word "推荐" and a dotted line to the basis, placed beside it without becoming layout, counted by 显示全部 but not by
/// search; selected with details, 开始探索 into 新探索 without an empty conversation, 忽略, 隐藏推荐 remembered, and the list
/// view for the keyboard and screen readers.
/// </summary>
public class RecommendedNodesUiTests
{
    private const string Question = "蒸汽机车的锅炉为什么会爆炸？";

    [AvaloniaFact]
    public async Task One_recommended_node_is_drawn_beside_its_basis_without_becoming_layout()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var layoutRows = await LayoutRowsAsync(app);
        var revision = network.Session.Revision;
        Assert.Empty(network.RecommendedNodes);

        await PrepareAsync(app, network, window);

        // One (three summaries), extending the most recently updated summary; nothing about the layout changed.
        var node = Assert.Single(network.RecommendedNodes);
        Assert.Equal(Question, node.Question);
        Assert.Contains(network.Nodes, n => n.Id == node.Basis);
        Assert.False(network.IsDirty);
        Assert.Equal(revision, network.Session.Revision);
        Assert.Equal(layoutRows, await LayoutRowsAsync(app));
        Assert.Equal(network.Nodes.Count, network.Session.Positions.Count);

        // Beside its basis, clear of every summary and its label.
        var basis = network.Nodes.Single(n => n.Id == node.Basis);
        Assert.True(Math.Abs(node.X - basis.X) + Math.Abs(node.Y - basis.Y) <= 3 * RecommendationPlacement.StepX);
        var box = CanvasBox.OfNode(node.X, node.Y, node.LabelWidth);
        Assert.All(network.Nodes, n => Assert.False(box.Overlaps(CanvasBox.OfNode(n.X, n.Y, n.LabelWidth)), n.ShortTitle));

        // Drawn: a dashed ring, the word in a frame, a dotted line to the basis; told apart from both kinds of edge.
        var canvas = CanvasOf(window);
        var paint = Assert.Single(canvas.PaintRecommendations());
        Assert.Equal(canvas.PaintNodes().Single(p => p.Node.Id == node.Basis).Center, paint.Basis);
        Assert.True(paint.TagBounds.Width > 0);
        Assert.NotNull(paint.Text);
        var palette = SummaryNetworkPalette.From(canvas);
        Assert.NotNull(palette.RecommendationRing(false).DashStyle);
        var link = palette.RecommendationLink(false);
        Assert.Equal(PenLineCap.Round, link.LineCap);
        Assert.NotEqual(palette.EdgePen(EdgeState.Indirect, true).DashStyle!.Dashes, link.DashStyle!.Dashes);
        Assert.Null(palette.EdgePen(EdgeState.Normal, false).DashStyle);

        // The legend says what the ring and the line are; the toolbar offers 换一批推荐 and 隐藏推荐.
        Assert.True(LegendCard.Shows(window, "RecommendationLegend"));
        Assert.True(Find<Button>(window, "RefreshRecommendedNodesButton").IsEffectivelyVisible);
        Assert.Equal(UiText.HideRecommendedNodes, AutomationProperties.GetName(Find<Button>(window, "ToggleRecommendedNodesButton")));

        // 显示全部 brings it into view; title search never finds it.
        network.Session.Viewport = new Viewport(basis.X + 2000, basis.Y, 2);
        network.ShowAllCommand.Execute(null);
        window.UpdateLayout();
        var at = canvas.ToScreen(node.X, node.Y);
        Assert.True(new Rect(canvas.Bounds.Size).Contains(at), $"{at} in {canvas.Bounds.Size}");
        Assert.True(new Rect(canvas.Bounds.Size).Contains(canvas.ToScreen(node.X + node.LabelWidth, node.Y)));
        network.SearchText = "锅炉";
        Assert.Empty(network.Matches);
        network.FindCommand.Execute(null);
        Assert.Null(network.Selected);
        Assert.Null(network.SelectedRecommendation);

        // 整理布局 arranges summaries only; it follows its basis, and saving writes summary positions only.
        network.ArrangeCommand.Execute(null);
        Assert.Equal(network.Nodes.Select(n => n.Id).ToHashSet(), network.Session.Positions.Keys.ToHashSet());
        node = Assert.Single(network.RecommendedNodes);
        basis = network.Nodes.Single(n => n.Id == node.Basis);
        Assert.Equal((basis.X, basis.Y), (node.BasisX, node.BasisY));
        await network.SaveLayoutCommand.ExecuteAsync(null);
        Assert.False(network.IsDirty);
        Assert.Equal(network.Nodes.Count, await LayoutRowsAsync(app));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_click_selects_it_and_the_details_show_question_reason_and_basis_without_reading_anything()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await PrepareAsync(app, network, window);
        var unread = network.Nodes.ToDictionary(n => n.Id, n => n.Unread);
        var canvas = CanvasOf(window);
        network.Select(network.Nodes[0].Id);
        window.UpdateLayout();

        var paint = Assert.Single(canvas.PaintRecommendations());
        UiInput.Click(window, canvas, at: paint.Center);

        var node = Assert.Single(network.RecommendedNodes);
        Assert.Equal(node, network.SelectedRecommendation);
        Assert.Null(network.Selected);
        Assert.Null(network.Session.Selected);
        Assert.True(canvas.PaintRecommendations()[0].Selected);
        Assert.True(Find<StackPanel>(window, "RecommendationDetailPanel").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(window, "DetailPanel").IsEffectivelyVisible);
        Assert.Equal(Question, Find<TextBlock>(window, "RecommendationQuestion").Text);
        Assert.Equal("从动力往安全延伸", Find<TextBlock>(window, "RecommendationReason").Text);
        Assert.Equal(node.BasisName, Find<TextBlock>(window, "RecommendationBasis").Text);
        Assert.Equal(UiText.RecommendedNodeName(Question, node.BasisName), AutomationProperties.GetName(Find<TextBlock>(window, "RecommendationQuestion")));
        Assert.Equal(UiText.IgnoreRecommendedNodeName(Question), AutomationProperties.GetName(Find<Button>(window, "IgnoreRecommendedNodeButton")));
        Assert.Equal(unread, network.Nodes.ToDictionary(n => n.Id, n => n.Unread));

        // A summary click selects that summary again.
        var summary = canvas.PaintNodes()[0];
        UiInput.Click(window, canvas, at: summary.Center);
        Assert.Null(network.SelectedRecommendation);
        Assert.Equal(summary.Node.Id, network.Selected?.Id);
        Assert.False(Find<StackPanel>(window, "RecommendationDetailPanel").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Start_exploration_fills_new_exploration_and_only_the_first_send_makes_the_conversation_and_uses_it()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await PrepareAsync(app, network, window);
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var conversations = app.Services.GetRequiredService<IConversations>();
        var before = (await conversations.ListAsync(workspace, CancellationToken.None)).Count;

        // The sidebar loads on its own; under load it may not have listed the seeded conversations yet.
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == before);
        var sidebar = before;
        var recommended = network.RecommendedNodes[0];
        network.SelectRecommendation(recommended);
        window.UpdateLayout();

        UiInput.Click(window, Find<Button>(window, "StartExplorationButton"));

        // 新探索 with the question in the input, not sent, and no conversation yet (none in the sidebar either).
        var page = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(MainSection.Explore, app.Workbench.CurrentSection);
        Assert.Equal(Question, page.Composer.Text);
        await page.Loaded;
        window.UpdateLayout();
        var input = Find<GhostTextBox>(window.GetVisualDescendants().OfType<NewConversationView>().Single(), "Input");
        await UiWait.UntilAsync(() => input.IsFocused);
        Assert.Equal(Question.Length, input.CaretIndex);
        Assert.DoesNotContain(app.Client.Requests, r => FakeChatClient.IsAnswer(r) && r.Turns[^1].Content == Question);
        Assert.Equal(before, (await conversations.ListAsync(workspace, CancellationToken.None)).Count);
        Assert.Equal(sidebar, app.ShellPage.Conversations.Items.Count);

        // Sent (edited or not): the root conversation is made now, with no branch source; the recommendation is used.
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel);
        await page.Marking;
        var made = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Id;
        Assert.Equal(before + 1, (await conversations.ListAsync(workspace, CancellationToken.None)).Count);
        Assert.Equal(0, await app.Services.GetRequiredService<SqliteDatabase>().ReadAsync(
            c => c.Scalar<long>(null, "SELECT count(*) FROM branch_source WHERE child_id = $id;", ("$id", made.ToString())), CancellationToken.None));
        var row = Assert.Single(await app.Services.GetRequiredService<IRecommendationStore>().ListAsync(workspace, RecommendationKind.Node, CancellationToken.None));
        Assert.Equal(RecommendationState.Used, row.State);

        // The conversation remembers the node and its basis (requirements §10.3.5).
        Assert.Equal((recommended.Id.ToString(), "node", recommended.Basis.ToString()), await RecommendedQuestionsUiTests.StartingAsync(app, made));

        // Gone from the network, and its slot is not filled by itself.
        await UiWait.UntilAsync(() => network.RecommendedNodes.Count == 0);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await app.Services.GetRequiredService<RecommendationService>().SettledAsync();
        Assert.Single(app.Client.Requests, FakeChatClient.IsNodeRecommendation);
        Assert.Empty(network.RecommendedNodes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ignore_removes_and_remembers_it_until_a_new_batch_is_asked_for()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await PrepareAsync(app, network, window);
        network.SelectRecommendation(network.RecommendedNodes[0]);
        window.UpdateLayout();

        UiInput.Click(window, Find<Button>(window, "IgnoreRecommendedNodeButton"));
        await UiWait.UntilAsync(() => !network.IgnoreRecommendationCommand.IsRunning);

        Assert.Empty(network.RecommendedNodes);
        Assert.Null(network.SelectedRecommendation);
        Assert.False(LegendCard.Shows(window, "RecommendationLegend"));
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var store = app.Services.GetRequiredService<IRecommendationStore>();
        Assert.Equal(RecommendationState.Ignored, Assert.Single(await store.ListAsync(workspace, RecommendationKind.Node, CancellationToken.None)).State);
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await app.Services.GetRequiredService<RecommendationService>().SettledAsync();
        Assert.Single(app.Client.Requests, FakeChatClient.IsNodeRecommendation);

        // 换一批推荐: a different one (the ignored question is not offered again).
        UiInput.Click(window, Find<Button>(window, "RefreshRecommendedNodesButton"));
        await UiWait.UntilAsync(() => network.RecommendedNodes.Count == 1 && !network.IsRefreshingRecommendations);
        Assert.Equal("铁路信号灯是怎样发展起来的？", network.RecommendedNodes[0].Question);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hiding_is_display_only_remembered_across_a_restart_and_prepares_nothing_meanwhile()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await PrepareAsync(app, network, window);
        network.SelectRecommendation(network.RecommendedNodes[0]);

        UiInput.Click(window, Find<Button>(window, "ToggleRecommendedNodesButton"));
        await UiWait.UntilAsync(() => !network.ToggleRecommendationsCommand.IsRunning);

        Assert.Empty(network.RecommendedNodes);
        Assert.Null(network.SelectedRecommendation);
        Assert.Empty(CanvasOf(window).PaintRecommendations());
        Assert.Equal(UiText.ShowRecommendedNodesButton, Find<TextBlock>(window, "ToggleRecommendedNodesText").Text);
        Assert.Equal(UiText.ShowRecommendedNodes, AutomationProperties.GetName(Find<Button>(window, "ToggleRecommendedNodesButton")));
        Assert.False(Find<Button>(window, "RefreshRecommendedNodesButton").IsEffectivelyEnabled);
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var store = app.Services.GetRequiredService<IRecommendationStore>();
        Assert.Equal(RecommendationState.Pending, Assert.Single(await store.ListAsync(workspace, RecommendationKind.Node, CancellationToken.None)).State);

        // While hidden, its basis summary goes: nothing is prepared for the free slot.
        var basis = (await store.ListAsync(workspace, RecommendationKind.Node, CancellationToken.None))[0].Basis!.Value;
        await DeleteSummaryAsync(app, basis);
        app.Services.GetRequiredService<IRecommendations>().SummaryNetworkOpened(workspace);
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await app.Services.GetRequiredService<RecommendationService>().SettledAsync();
        Assert.Single(app.Client.Requests, FakeChatClient.IsNodeRecommendation);

        // Remembered for the workspace across a restart; shown again, the free slot is filled then.
        window.Close();
        await app.RestartAsync();
        window = app.ShowWindow(1100, 720);
        network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        Assert.True(network.RecommendationsHidden);
        Assert.Equal(UiText.ShowRecommendedNodes, network.ToggleRecommendationsLabel);
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await app.Services.GetRequiredService<RecommendationService>().SettledAsync();
        Assert.Single(app.Client.Requests, FakeChatClient.IsNodeRecommendation);

        await network.ToggleRecommendationsCommand.ExecuteAsync(null);
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await app.Services.GetRequiredService<RecommendationService>().SettledAsync();
        await UiWait.UntilAsync(() => network.RecommendedNodes.Count == 1);
        Assert.Equal(UiText.HideRecommendedNodes, network.ToggleRecommendationsLabel);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_list_view_reaches_it_from_the_keyboard_and_says_what_it_extends()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await PrepareAsync(app, network, window);
        network.ToggleViewCommand.Execute(null);
        window.UpdateLayout();
        Assert.False(LegendCard.Shows(window, "RecommendationLegend"));

        var row = Find<Button>(window, "RecommendedNodeRow");
        var node = network.RecommendedNodes[0];
        Assert.Equal($"推荐：{Question}，延伸自 {node.BasisName}", AutomationProperties.GetName(row));
        Assert.True(Find<StackPanel>(window, "RecommendedNodesList").IsEffectivelyVisible);

        // Tab from the last summary's row lands on it; Enter selects it; its actions are buttons in the details.
        var summaries = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("list-name") && b.Name != "RecommendedNodeRow").ToList();
        summaries[^1].Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(row.IsFocused);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(node, network.SelectedRecommendation);
        window.UpdateLayout();
        var start = Find<Button>(window, "StartExplorationButton");
        Assert.True(start.IsEffectivelyVisible && start.IsEffectivelyEnabled && start.Focusable);
        start.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(Question, Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage).Composer.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Failures_are_said_quietly_and_off_or_without_a_model_nothing_is_shown()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        app.Client.NodeRecommendations.FailNext(GenerationErrorKind.Network);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var service = app.Services.GetRequiredService<RecommendationService>();
        await UiWait.UntilAsync(() => service.IsWaiting(network.Workspace.Id, RecommendationKind.Node));
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await service.SettledAsync();

        // The background failed: one quiet line with 重试, nothing drawn; 重试 asks again and the node comes.
        await UiWait.UntilAsync(() => network.RecommendationNote == UiText.RecommendedNodesQuietFailure);
        window.UpdateLayout();
        Assert.True(Find<WrapPanel>(window, "RecommendedNodesNoteLine").IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "RecommendedNodesSettingsButton").IsEffectivelyVisible);
        Assert.Empty(network.RecommendedNodes);
        UiInput.Click(window, Find<Button>(window, "RetryRecommendedNodesButton"));
        await UiWait.UntilAsync(() => network.RecommendedNodes.Count == 1 && !network.IsRefreshingRecommendations);
        Assert.False(network.HasRecommendationNote);

        // No model: 换一批推荐 says why and offers the settings; the background never says anything.
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync((c, t) => c.Execute(t, "DELETE FROM app_state WHERE key = 'active_model_config_id';"), CancellationToken.None);
        await network.RefreshRecommendationsCommand.ExecuteAsync(null);
        Assert.Equal(UiText.RecommendedNodesProblemText(RecommendationProblem.NotConfigured, null), network.RecommendationNote);
        window.UpdateLayout();
        Assert.True(Find<Button>(window, "RecommendedNodesSettingsButton").IsEffectivelyVisible);

        // Without a model none is drawn (§10.3 共同规则; review 2026-11-13), the kept one included.
        Assert.Empty(network.RecommendedNodes);

        // Turned off in the settings: no tools, no nodes, no legend.
        await app.Services.GetRequiredService<Application.Preferences.IRecommendationSettings>().SetNodesAsync(false, CancellationToken.None);
        await UiWait.UntilAsync(() => network.RecommendedNodes.Count == 0 && !network.ShowsRecommendationTools);
        window.UpdateLayout();
        Assert.False(Find<Button>(window, "RefreshRecommendedNodesButton").IsEffectivelyVisible);
        Assert.False(LegendCard.Shows(window, "RecommendationLegend"));
        Assert.False(network.HasRecommendationNote);
        Assert.Empty(CanvasOf(window).PaintRecommendations());
        window.Close();
    }

    /// <summary>The network's background look runs (on the standing clock) and its node is drawn.</summary>
    internal static async Task PrepareAsync(DesktopHarness app, SummaryNetworkHomeViewModel network, Window window, int expected = 1)
    {
        var service = app.Services.GetRequiredService<RecommendationService>();
        await UiWait.UntilAsync(() => service.IsWaiting(network.Workspace.Id, RecommendationKind.Node));
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await service.SettledAsync();
        await UiWait.UntilAsync(() => network.RecommendedNodes.Count == expected);

        // The reload the request's end posted has run too, so nothing rebuilds the rows after this.
        await UiWait.UntilAsync(() => network.RecommendationsReloaded.IsCompleted);
        await network.RecommendationsReloaded;
        window.UpdateLayout();
    }

    private static Task<long> LayoutRowsAsync(DesktopHarness app) =>
        app.Services.GetRequiredService<SqliteDatabase>().ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM node_layout;"), CancellationToken.None);

    private static Task<int> DeleteSummaryAsync(DesktopHarness app, Core.Summaries.SummaryNodeId node) =>
        app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => c.Execute(t, "DELETE FROM node_layout WHERE node_id = $id;", ("$id", node.ToString()))
                + c.Execute(t, "DELETE FROM summary_version WHERE node_id = $id;", ("$id", node.ToString()))
                + c.Execute(t, "DELETE FROM summary_node WHERE id = $id;", ("$id", node.ToString())),
            CancellationToken.None);
}
