using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The network page: called Mindweave, a 空间 | 时间线 switch, no layout controls, a legend for
/// the new marks, and dots without a summary that open their conversation.
/// </summary>
public class SummaryNetworkPageTests
{
    public static TheoryData<string> Themes => new() { "Light", "Dark" };

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task The_page_is_called_Mindweave_and_switches_views(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720, Theme(theme));
        await SeedAsync(app);
        var network = await OpenAsync(app, window);
        var view = PageView(window);

        Assert.Equal("Mindweave", network.Title);
        Assert.Equal("Mindweave", app.ShellPage.Title);
        Assert.Equal("Mindweave", app.ShellPage.SummaryNetworkLabel);
        var entry = Part<Button>(window, "NavSummaryNetwork");
        Assert.Equal("Mindweave", AutomationProperties.GetName(entry));
        Assert.Contains(entry.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Mindweave");
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Mindweave" && t.IsEffectivelyVisible);
        Assert.False(network.IsTimeline);
        Assert.True(Find<RadioButton>(view, "SpatialViewButton").IsChecked);
        Screenshots.Capture(window, $"network-spatial-{theme.ToLowerInvariant()}");

        UiInput.Click(window, Find<RadioButton>(view, "TimelineViewButton"));
        window.UpdateLayout();

        Assert.True(network.IsTimeline);
        Assert.True(Find<RadioButton>(view, "TimelineViewButton").IsChecked);
        Assert.False(Find<RadioButton>(view, "SpatialViewButton").IsChecked);
        var roots = network.Nodes.Where(n => n.IsRoot).ToList();
        Assert.Equal(2, roots.Count);
        Assert.All(roots, r => Assert.Equal(0, r.Y));

        // The page was rebuilt for the new view: what it draws is where the session put it, and the branch leaves the main line in a curve.
        Assert.All(network.Nodes, n => Assert.Equal(network.Session.Positions[n.Id], new NodePosition(n.X, n.Y)));
        Assert.Contains(network.Edges, e => e.Curved);
        Screenshots.Capture(window, $"network-timeline-{theme.ToLowerInvariant()}");

        UiInput.Click(window, Find<RadioButton>(view, "SpatialViewButton"));
        window.UpdateLayout();
        Assert.False(network.IsTimeline);
        Assert.DoesNotContain(network.Edges, e => e.Curved);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_the_shown_view_again_keeps_pan_and_zoom()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await OpenAsync(app, window);
        var view = PageView(window);
        UiInput.Click(window, Find<RadioButton>(view, "TimelineViewButton"));
        window.UpdateLayout();
        Assert.True(network.IsTimeline);
        var panned = new Viewport(123, -45, 1.7);
        network.Session.Viewport = panned;
        var refits = 0;
        network.ViewChanged += (_, _) => refits++;

        UiInput.Click(window, Find<RadioButton>(view, "TimelineViewButton"));
        window.UpdateLayout();

        Assert.True(network.IsTimeline);
        Assert.Equal(panned, network.Session.Viewport);
        Assert.Equal(0, refits);

        // The other view still switches and fits.
        UiInput.Click(window, Find<RadioButton>(view, "SpatialViewButton"));
        Assert.False(network.IsTimeline);
        Assert.NotEqual(panned, CanvasOf(window).Current);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Nothing_offers_to_arrange_or_save_and_the_legend_explains_the_marks(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720, Theme(theme));
        await SeedAsync(app);
        await OpenAsync(app, window);
        var view = PageView(window);

        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Control>(), c => c.Name is "ArrangeButton" or "SaveLayoutButton" or "LayoutSavedStatus");
        Assert.True(Find<RadioButton>(view, "TimelineViewButton").IsEffectivelyVisible);

        // The legend is an icon until it is wanted; every mark it explains keeps its words (requirements §10.3).
        Assert.False(Find<Border>(view, "LegendCard").IsVisible);
        LegendCard.Toggle(window);
        var panel = Find<StackPanel>(view, "Legend");
        var texts = panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
        var legend = texts.Select(t => t.Text).ToList();

        // None of its words runs past the canvas it lies over.
        var frame = Find<Border>(view, "CanvasFrame");
        Assert.All(texts, t => Assert.True(t.TranslatePoint(new Point(t.Bounds.Width, 0), frame)!.Value.X <= frame.Bounds.Width + 0.5, $"{t.Text} runs past the canvas"));
        Assert.Contains("圆点大小：问了多少，不代表掌握程度", legend);
        Assert.Contains("外圈：有总结", legend);
        Assert.Contains("虚线箭头：时间先后", legend);
        Assert.Contains("实线：分支", legend);
        Assert.Contains(UiText.LegendUnread, legend);
        Assert.Contains(UiText.LegendCandidate, legend);

        // Each mark keeps its words: the unread badge and 未读 are one item, never split across rows.
        var unread = texts.Single(t => t.Text == UiText.LegendUnread);
        var item = Assert.IsType<StackPanel>(unread.Parent);
        Assert.Equal(Avalonia.Layout.Orientation.Horizontal, item.Orientation);
        Assert.Same(panel, item.Parent);
        Assert.Contains(item.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>(), e => e.IsEffectivelyVisible);
        Screenshots.Capture(window, $"network-legend-{theme.ToLowerInvariant()}");
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task A_dot_without_a_summary_opens_its_conversation(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720, Theme(theme));
        var (summarized, _, plain) = await SeedAsync(app);
        var network = await OpenAsync(app, window);
        var view = PageView(window);
        SummaryNetworkNode NodeOf(ConversationId conversation) => network.Session.Projection.Nodes.Single(n => n.Conversation == conversation);

        // The summarized dot offers its summary, links and reading state.
        network.Select(NodeOf(summarized).Id);
        window.UpdateLayout();
        Assert.True(network.Selected!.HasSummary);
        Assert.True(Find<Button>(view, "OpenSelectedButton").IsEffectivelyVisible);
        Assert.True(Find<Button>(view, "LinkToButton").IsEffectivelyVisible);

        // A dot without a summary: only the way back to its conversation.
        var dot = NodeOf(plain);
        network.Select(dot.Id);
        window.UpdateLayout();
        Assert.False(network.Selected!.HasSummary);
        Assert.False(Find<Button>(view, "OpenSelectedButton").IsEffectivelyVisible);
        Assert.False(Find<Button>(view, "LinkToButton").IsEffectivelyVisible);
        Assert.False(Find<Border>(view, "UnreadBadge").IsEffectivelyVisible);
        Assert.False(Find<Border>(view, "ReadBadge").IsEffectivelyVisible);
        Assert.True(Find<Button>(view, "ReturnSelectedButton").IsEffectivelyVisible);
        Screenshots.Capture(window, $"network-dot-details-{theme.ToLowerInvariant()}");

        // The list view lists every dot; one without a summary has no markers.
        Assert.Equal(3, network.Items.Count);
        var row = network.Items.Single(i => i.Node == dot.Id);
        Assert.Equal(string.Empty, row.Markers);
        Assert.False(row.HasMarkers);
        Assert.False(row.ShowsRead);
        Assert.True(network.Items.Single(i => i.Node == NodeOf(summarized).Id).HasMarkers);

        network.OpenCommand.Execute(dot.Id);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(plain, page.Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task New_conversations_and_questions_appear_without_reopening()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (_, _, plain) = await SeedAsync(app);
        var network = await OpenAsync(app, window);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        int WeightOf(ConversationId conversation) => network.Nodes.Single(n => network.Session.Projection.Nodes.Single(p => p.Id == n.Id).Conversation == conversation).Weight;
        Assert.Equal(1, WeightOf(plain));

        // Away in the conversation: one more question there, and a new exploration.
        network.OpenCommand.Execute(network.Session.Projection.Nodes.Single(n => n.Conversation == plain).Id);
        Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await SummaryNetworkScenario.AskAsync(app, conversations, plain, workspace, "用什么相机好？");
        var started = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "蒸汽机怎么工作？");

        // Back on the same page, without a summary changing: the new dot is there and the asked one has grown.
        app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
        Assert.Same(network, app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4 && WeightOf(plain) == 2);
        Assert.Contains(network.Session.Projection.Nodes, n => n.Conversation == started);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task An_empty_workspace_shows_the_empty_state_in_both_views(string theme)
    {
        // Failed page reads are reported process-wide; another test's page that reloads while its harness tears down (its
        // database already closed) is not this page's failure.
        var failures = new List<Exception>();
        void OnFailed(object? sender, Exception e)
        {
            if (e.StackTrace?.Contains("SummaryNetwork", StringComparison.Ordinal) == true && e is not InvalidOperationException { Message: "The database is not open." })
            {
                failures.Add(e);
            }
        }

        SerialRefresh.Failed += OnFailed;
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 720, Theme(theme));
            await app.StartWithWorkspaceAsync("火车");
            app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
            var network = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
            await network.Loaded;
            window.UpdateLayout();
            var view = PageView(window);
            TextBlock EmptyText() => view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == UiText.SummaryNetworkEmpty);

            Assert.True(network.IsEmpty);
            Assert.True(EmptyText().IsEffectivelyVisible);
            Assert.Empty(CanvasOf(window).PaintNodes());

            network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
            await network.Session.LoadAsync(CancellationToken.None);
            window.UpdateLayout();

            Assert.True(network.IsTimeline);
            Assert.True(network.IsEmpty);
            Assert.True(EmptyText().IsEffectivelyVisible);
            Assert.Empty(network.Nodes);
            Assert.Empty(CanvasOf(window).PaintNodes());
            Screenshots.Capture(window, $"network-empty-timeline-{theme.ToLowerInvariant()}");

            network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Spatial);
            window.UpdateLayout();
            Assert.False(network.IsTimeline);
            Assert.True(network.IsEmpty);
            window.Close();
        }
        finally
        {
            SerialRefresh.Failed -= OnFailed;
        }

        Assert.Empty(failures);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task The_details_show_the_title_as_heading_and_the_question_below(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720, Theme(theme));
        var (_, _, plain) = await SeedAsync(app);
        await app.Services.GetRequiredService<ConversationService>().RenameAsync(plain, "摄影入门", CancellationToken.None);
        var network = await OpenAsync(app, window);
        var view = PageView(window);

        network.Select(network.Session.Projection.Nodes.Single(n => n.Conversation == plain).Id);
        window.UpdateLayout();

        var heading = Find<TextBlock>(view, "DetailTitle");
        Assert.Equal("摄影入门", heading.Text);
        Assert.Equal(Avalonia.Media.FontWeight.Normal, heading.FontWeight);
        var block = Find<StackPanel>(view, "DetailQuestionBlock");
        Assert.True(block.IsEffectivelyVisible);
        Assert.Equal("摄影怎么入门？", Find<TextBlock>(view, "DetailQuestion").Text);
        Assert.Contains(block.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == UiText.DetailQuestion);
        Assert.False(Find<Button>(view, "ToggleQuestionButton").IsEffectivelyVisible);
        Screenshots.Capture(window, $"network-details-title-{theme.ToLowerInvariant()}");
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task A_very_long_question_stays_bounded_and_expands(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720, Theme(theme));
        var (_, _, plain) = await SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var question = string.Concat(Enumerable.Range(0, 200).Select(i => $"第{i}个问题。"));
        var longOne = await SummaryNetworkScenario.AskAsync(app, conversations, null, app.Workbench.CurrentWorkspace!.Id, question);
        await conversations.RenameAsync(longOne, "一个很长的提问", CancellationToken.None);
        var network = await OpenAsync(app, window, 4);
        var view = PageView(window);

        network.Select(network.Session.Projection.Nodes.Single(n => n.Conversation == longOne).Id);
        window.UpdateLayout();
        window.UpdateLayout();

        var text = Find<TextBlock>(view, "DetailQuestion");
        var toggle = Find<Button>(view, "ToggleQuestionButton");
        var panel = Find<ScrollViewer>(view, "DetailScroller");
        Assert.Equal(question, text.Text);
        Assert.Equal(4, text.MaxLines);
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.Equal(UiText.ShowWholeQuestion, toggle.Content);
        Assert.True(text.TextLayout.TextLines.Count <= 4, $"{text.TextLayout.TextLines.Count} lines collapsed");
        Assert.True(text.Bounds.Height < 4 * text.FontSize * 2, "collapsed question is bounded");
        AssertInside(Find<Button>(view, "ReturnSelectedButton"), panel);
        AssertInside(toggle, panel);
        Screenshots.Capture(window, $"network-details-long-collapsed-{theme.ToLowerInvariant()}");

        UiInput.Click(window, toggle);
        window.UpdateLayout();
        window.UpdateLayout();
        Assert.Equal(0, text.MaxLines);
        Assert.Equal(UiText.HideWholeQuestion, toggle.Content);
        Assert.True(text.TextLayout.TextLines.Count > 4);

        // Expanded, the question scrolls in its own box of at most 60% of the panel: the buttons stay in view without scrolling the panel.
        var box = Find<ScrollViewer>(view, "QuestionScroller");
        Assert.True(box.Bounds.Height <= panel.Bounds.Height * 0.6 + 0.5, $"question box {box.Bounds.Height} of {panel.Bounds.Height}");
        Assert.True(box.Bounds.Height > 4 * text.FontSize, "the expanded box shows more than the collapsed lines");
        Assert.Equal(0, panel.Offset.Y);
        AssertInside(Find<Button>(view, "ReturnSelectedButton"), panel);
        AssertInside(toggle, panel);
        Screenshots.Capture(window, $"network-details-long-expanded-{theme.ToLowerInvariant()}");

        UiInput.Click(window, toggle);
        window.UpdateLayout();
        Assert.Equal(4, text.MaxLines);
        Assert.Equal(UiText.ShowWholeQuestion, toggle.Content);

        // Another session's details start collapsed again, with no toggle for a short question.
        network.Select(network.Session.Projection.Nodes.Single(n => n.Conversation == plain).Id);
        window.UpdateLayout();
        window.UpdateLayout();
        Assert.False(toggle.IsEffectivelyVisible);
        window.Close();
    }

    private static void AssertInside(Control control, Control container)
    {
        var origin = control.TranslatePoint(default, container)!.Value;
        Assert.True(control.IsEffectivelyVisible, $"{control.Name} is hidden");
        Assert.True(origin.X >= -0.5 && origin.X + control.Bounds.Width <= container.Bounds.Width + 0.5, $"{control.Name} is outside the panel horizontally");
        Assert.True(origin.Y >= -0.5 && origin.Y + control.Bounds.Height <= container.Bounds.Height + 0.5, $"{control.Name} is outside the panel vertically ({origin.Y}+{control.Bounds.Height} of {container.Bounds.Height})");
    }

    [AvaloniaFact]
    public async Task A_summary_dot_keeps_its_summary_name_and_shows_the_question()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (summarized, _, _) = await SeedAsync(app);
        var network = await OpenAsync(app, window);
        var view = PageView(window);

        network.Select(network.Session.Projection.Nodes.Single(n => n.Conversation == summarized).Id);
        window.UpdateLayout();

        Assert.Equal("火车起源的详细说明", Find<TextBlock>(view, "DetailTitle").Text);
        Assert.True(Find<StackPanel>(view, "DetailQuestionBlock").IsEffectivelyVisible);
        Assert.Equal("火车是怎么发明的？", Find<TextBlock>(view, "DetailQuestion").Text);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Texts_speak_of_conversations(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720, Theme(theme));
        var (summarized, _, _) = await SeedAsync(app);
        var network = await OpenAsync(app, window);
        var view = PageView(window);
        IEnumerable<string?> Visible() => view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text);

        Assert.Contains(UiText.SummaryNetworkSelectHint, Visible());

        network.Select(network.Session.Projection.Nodes.Single(n => n.Conversation == summarized).Id);
        window.UpdateLayout();
        Assert.Contains(Visible(), t => t is not null && t.StartsWith("开始于 ", StringComparison.Ordinal));
        Assert.DoesNotContain(Visible(), t => t is not null && t.StartsWith("生成于 ", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_plain_dot_has_a_primary_action()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (_, _, plain) = await SeedAsync(app);
        var network = await OpenAsync(app, window);
        var view = PageView(window);

        network.Select(network.Session.Projection.Nodes.Single(n => n.Conversation == plain).Id);
        window.UpdateLayout();

        var back = Find<Button>(view, "ReturnSelectedButton");
        Assert.True(back.IsEffectivelyVisible);
        Assert.Equal(UiText.ReturnToConversation, Assert.IsType<TextBlock>(back.Content).Text);
        Assert.Contains("primary", back.Classes);
        Assert.DoesNotContain("secondary", back.Classes);
        window.Close();
    }

    private static ThemeVariant Theme(string theme) => theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static SummaryNetworkHomeView PageView(MainWindow window) => window.GetVisualDescendants().OfType<SummaryNetworkHomeView>().Single();

    private static async Task<SummaryNetworkHomeViewModel> OpenAsync(DesktopHarness app, MainWindow window, int dots = 3)
    {
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == dots);
        window.UpdateLayout();
        return network;
    }

    /// <summary>Two roots on the same day: the first with a branch and a summary, the second with neither.</summary>
    internal static async Task<(ConversationId Summarized, ConversationId Branch, ConversationId Plain)> SeedAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var root = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "火车是怎么发明的？");
        var branch = await SummaryNetworkScenario.BranchAsync(app, conversations, root, "谁造了第一台？");
        var plain = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "摄影怎么入门？");
        var summaries = app.Services.GetRequiredService<SummaryService>();
        app.Client.Knowledge.Replies.Enqueue("标题：火车起源的详细说明\n短名：火车起源\n\n正文");
        var outcome = await summaries.SummarizeAsync(root, CancellationToken.None);
        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        await UiWait.UntilAsync(() => summaries.LatestTaskAsync(root, CancellationToken.None).Result is { State: GenerationTaskState.Completed });

        // Every answer seen: the hints they popped up go, so none covers the details panel in the screenshots.
        var marks = app.Services.GetRequiredService<ConversationMarksService>();
        await UiWait.UntilAsync(() => new[] { root, branch, plain }.All(marks.IsUnread));
        foreach (var id in new[] { root, branch, plain })
        {
            marks.MarkSeen(id);
        }

        await UiWait.UntilAsync(() => app.ShellPage.WorkspaceNews!.Toasts.Count == 0);
        return (root, branch, plain);
    }
}
