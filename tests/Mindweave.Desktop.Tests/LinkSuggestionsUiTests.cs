using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// AI-suggested links the user confirms (requirements §10.3.4, 2026-11-16) in the real window: the toolbar's 关联 menu keeps
/// one row at 1100 px and offers 建议关联 only with three summaries, never by itself; pending suggestions drawn light and
/// dash-dotted with "建议" and a legend, apart from every other line and hidden with 隐藏关联; the review list with the
/// note edited before 接受, 忽略 and 全部忽略; the whole path by keyboard; the "AI 建议，经你确认" marker wherever links are
/// listed; and the quiet line with 重试 when a request stores nothing.
/// </summary>
public class LinkSuggestionsUiTests
{
    private const string Root = "火车起源";
    private const string Leaf = "火箭号速度";
    private const string Sibling = "铁轨";

    [AvaloniaFact]
    public async Task The_toolbar_keeps_one_row_at_1100_px_with_links_recommendations_and_an_unsaved_layout()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await RecommendedNodesUiTests.PrepareAsync(app, network, window);
        await app.Services.GetRequiredService<ISummaryLinks>().CreateAsync(NodeOf(network, Root), NodeOf(network, Sibling), "对比", CancellationToken.None);
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 1);
        window.UpdateLayout();

        // Every tool there: find, show all, zoom, arrange, list, 换一批推荐, 隐藏推荐, 关联 — on one row.
        Assert.True(network.ShowsRecommendationTools && network.ShowsLinkTools);
        AssertOneRow(window);

        // Also with the layout unsaved, when the wider accent 保存布局 takes the saved status's place.
        network.ArrangeCommand.Execute(null);
        await SummaryNetworkScenario.MoveSomethingAsync(app, network);
        network.ArrangeCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(network.IsDirty);
        AssertOneRow(window);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Suggest_links_is_only_ever_manual_and_offered_with_at_least_three_summaries()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await SettledAsync(network);

        // Opening the network, its background looks and a restart's worth of waiting never ask for suggestions.
        app.PreparationTime.Advance(TimeSpan.FromSeconds(30));
        await app.Services.GetRequiredService<Application.Recommendations.RecommendationService>().SettledAsync();
        await app.Services.GetRequiredService<LinkSuggestionService>().SettledAsync();
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsLinkSuggestion);
        Assert.Empty(network.SuggestionEdges);

        // Three summaries: offered, with what it does.
        LinksMenu.Open(window);
        var item = LinksMenu.Entry(window, "SuggestLinksMenuItem");
        Assert.True(item.IsEffectivelyEnabled);
        Assert.Equal(UiText.SuggestLinks, item.Header);
        Assert.Equal(UiText.SuggestLinksTip, AutomationProperties.GetHelpText(item));
        Assert.False(LinksMenu.Shows(window, "SuggestLinksUnavailable"));
        LinksMenu.Close(window);

        // Two: unavailable, and the menu says why in words (and the entry's help text and tip).
        await DeleteSummaryAsync(app, NodeOf(network, Sibling));
        await UiWait.UntilAsync(() => network.Nodes.Count == 2 && !network.Suggestions!.CanSuggest);
        LinksMenu.Open(window);
        item = LinksMenu.Entry(window, "SuggestLinksMenuItem");
        Assert.False(item.IsEffectivelyEnabled);
        Assert.Equal(UiText.SuggestLinksNeedsSummaries, AutomationProperties.GetHelpText(item));
        Assert.Equal(UiText.SuggestLinksNeedsSummaries, ToolTip.GetTip(item));
        Assert.True(ToolTip.GetShowOnDisabled(item));
        Assert.Equal(UiText.SuggestLinksNeedsSummaries, LinksMenu.Entry(window, "SuggestLinksUnavailable").Header);
        LinksMenu.Close(window);
        Assert.False(network.Suggestions!.SuggestCommand.CanExecute(null));
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsLinkSuggestion);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pending_suggestions_are_drawn_light_and_dash_dotted_with_their_word_and_a_legend_and_follow_hide_links()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await RecommendedNodesUiTests.PrepareAsync(app, network, window);
        await app.Services.GetRequiredService<ISummaryLinks>().CreateAsync(NodeOf(network, Root), NodeOf(network, Leaf), null, CancellationToken.None);
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 1);
        var revision = network.Session.Revision;
        var layoutRows = await LayoutRowsAsync(app);
        Assert.False(Find<StackPanel>(window, "SuggestionLegend").IsEffectivelyVisible);

        await SuggestAsync(app, window, network, (Root, Sibling, "对比", "都讲铁路的起点"), (Leaf, Sibling, null, "轨道决定速度"));

        // Drawn between the two summaries; nothing about the layout changed; never part of 整理布局.
        Assert.Equal(2, network.SuggestionEdges.Count);
        Assert.False(network.IsDirty);
        Assert.Equal(revision, network.Session.Revision);
        Assert.Equal(layoutRows, await LayoutRowsAsync(app));

        // The words on each line: "建议", with the note when there is one, in a dashed frame at the middle.
        var canvas = CanvasOf(window);
        var paints = canvas.PaintSuggestions();
        Assert.Equal(new HashSet<string?> { UiText.LinkSuggestionLabel("对比"), UiText.LinkSuggestionTag }, paints.Select(p => p.Label).ToHashSet());
        Assert.All(paints, p => Assert.True(OnLine(p.LabelBounds.Center, p.From, p.To)));

        // Dash-dot, unlike the solid link and source edge, the dashed indirect edge and the round-dotted recommendation line;
        // a lighter tone than the link; the words are not the link's ("关联" or a bare note).
        var palette = SummaryNetworkPalette.From(canvas);
        var pen = palette.LinkSuggestionPen(false);
        Assert.Equal(4, pen.DashStyle!.Dashes!.Count);
        Assert.Null(palette.LinkPen(false).DashStyle);
        Assert.Null(palette.EdgePen(EdgeState.Normal, false).DashStyle);
        Assert.NotEqual(pen.DashStyle.Dashes, palette.EdgePen(EdgeState.Indirect, true).DashStyle!.Dashes);
        Assert.NotEqual(pen.DashStyle.Dashes, palette.RecommendationLink(false).DashStyle!.Dashes);
        Assert.Equal(PenLineCap.Flat, pen.LineCap);
        var colour = ((ISolidColorBrush)pen.Brush!).Color;
        foreach (var other in new[] { palette.Link, palette.Edge, palette.EdgeIndirect, palette.EdgeTrail, palette.Recommendation })
        {
            Assert.NotEqual(colour, ((ISolidColorBrush)other).Color);
        }

        Assert.DoesNotContain(paints, p => p.Label == UiText.LinkTag);
        Assert.All(canvas.PaintLinks(), l => Assert.DoesNotContain(UiText.LinkSuggestionTag, l.Label!, StringComparison.Ordinal));

        // The legend says what the line is, in words.
        window.UpdateLayout();
        var legend = Find<StackPanel>(window, "SuggestionLegend");
        Assert.True(legend.IsEffectivelyVisible);
        Assert.Contains(legend.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == UiText.LegendLinkSuggestion);

        // 整理布局 is the same with or without them.
        network.ArrangeCommand.Execute(null);
        var arranged = network.Session.Positions.ToDictionary(p => p.Key, p => p.Value);
        await network.Suggestions!.IgnoreAllCommandHelperAsync();
        network.ArrangeCommand.Execute(null);
        Assert.Equal(arranged, network.Session.Positions.ToDictionary(p => p.Key, p => p.Value));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Words_of_suggestions_whose_middles_meet_move_along_their_lines_instead_of_covering_each_other_or_a_note()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await app.Services.GetRequiredService<ISummaryLinks>().CreateAsync(NodeOf(network, Leaf), NodeOf(network, Sibling), "原因", CancellationToken.None);
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 1);
        await SuggestAsync(app, window, network, (Root, Leaf, "对比", "一"), (Root, Sibling, "例子", "二"));

        // The sibling dragged onto the leaf: both suggestions (and the link between them) have the same middle.
        var leaf = network.Nodes.Single(n => n.ShortTitle == Leaf);
        network.MoveNode(NodeOf(network, Sibling), leaf.X + 1, leaf.Y + 1);
        window.UpdateLayout();
        var canvas = CanvasOf(window);
        var paints = canvas.PaintSuggestions();
        var notes = canvas.PaintLinks().Select(l => l.NoteBounds).ToList();
        Assert.Equal(2, paints.Count);
        Assert.False(paints[0].LabelBounds.Intersects(paints[1].LabelBounds));
        Assert.All(paints, p => Assert.DoesNotContain(notes, n => n.Intersects(p.LabelBounds)));
        Assert.All(paints, p => Assert.True(OnLine(p.LabelBounds.Center, p.From, p.To)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hiding_links_hides_the_suggestion_lines_and_legend_but_the_review_list_stays()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await app.Services.GetRequiredService<ISummaryLinks>().CreateAsync(NodeOf(network, Root), NodeOf(network, Leaf), null, CancellationToken.None);
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 1);
        await SuggestAsync(app, window, network, (Root, Sibling, "对比", "都讲铁路的起点"));
        Assert.Single(network.SuggestionEdges);

        LinksMenu.Choose(window, "ToggleLinksMenuItem");
        await UiWait.UntilAsync(() => !network.ToggleLinksCommand.IsRunning);
        window.UpdateLayout();
        Assert.Empty(network.SuggestionEdges);
        Assert.Empty(CanvasOf(window).PaintSuggestions());
        Assert.False(Find<StackPanel>(window, "SuggestionLegend").IsEffectivelyVisible);
        Assert.True(Find<StackPanel>(window, "SuggestionsSection").IsEffectivelyVisible);
        Assert.Single(network.Suggestions!.Items);

        LinksMenu.Choose(window, "ToggleLinksMenuItem");
        await UiWait.UntilAsync(() => network.SuggestionEdges.Count == 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Accepting_with_an_edited_note_makes_a_confirmed_link_marked_in_the_details_the_list_view_and_the_summary_page()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await SuggestAsync(app, window, network, (Root, Sibling, "对比", "都讲铁路的起点"), (Leaf, Sibling, "原因", "轨道决定速度"));
        Assert.True(Find<StackPanel>(window, "SuggestionsSection").IsEffectivelyVisible);
        var row = Row(window, Root);

        // 改说明: over 16 characters is said while typing and 接受 waits; 完成 keeps the edited note for 接受.
        UiInput.Click(window, Find<Button>(row, "EditSuggestedNoteButton"));
        var box = Find<TextBox>(row, "SuggestedNoteBox");
        await UiWait.UntilAsync(() => box.IsFocused);
        Assert.Equal(UiText.SuggestedNoteEditorName(NameOf(network, Root), NameOf(network, Sibling)), AutomationProperties.GetName(box));
        box.Text = new string('长', 17);
        window.UpdateLayout();
        Assert.True(Find<TextBlock>(row, "SuggestedNoteError").IsEffectivelyVisible);
        Assert.Equal(UiText.LinkNoteTooLong(17), Find<TextBlock>(row, "SuggestedNoteError").Text);
        Assert.False(Find<Button>(row, "AcceptSuggestionButton").IsEffectivelyEnabled);
        box.Text = "起点对比";
        window.UpdateLayout();
        UiInput.Click(window, Find<Button>(row, "SaveSuggestedNoteButton"));
        Assert.Equal("起点对比", Find<Border>(row, "SuggestedNote").GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Assert.Empty(await app.Services.GetRequiredService<ISummaryLinks>().ListAsync(network.Workspace.Id, CancellationToken.None));

        UiInput.Click(window, Find<Button>(row, "AcceptSuggestionButton"));
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 1 && network.Suggestions!.Items.Count == 1);
        var link = Assert.Single(await app.Services.GetRequiredService<ISummaryLinks>().ListAsync(network.Workspace.Id, CancellationToken.None));
        Assert.Equal(("起点对比", SummaryLinkOrigin.Confirmed), (link.Note, link.Origin));
        Assert.Single(network.SuggestionEdges);

        // The details of either end list it with the marker, in words and for a screen reader.
        network.Select(NodeOf(network, Sibling));
        await network.LinksReloaded;
        await network.Links!.Loaded;
        window.UpdateLayout();
        var linkRow = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LinkRow" && b.IsEffectivelyVisible && ((SummaryLinkItem)b.DataContext!).IsConfirmed);
        Assert.True(Find<TextBlock>(linkRow, "LinkConfirmedMarker").IsEffectivelyVisible);
        Assert.Equal(UiText.LinkConfirmedMarker, Find<TextBlock>(linkRow, "LinkConfirmedMarker").Text);
        Assert.Equal(UiText.LinkItemName(NameOf(network, Root), "起点对比", confirmed: true), AutomationProperties.GetName(linkRow));

        // The list view says it after the note.
        network.ToggleViewCommand.Execute(null);
        await UiWait.UntilAsync(() => network.Items.Any(i => i.Links.Contains(UiText.LinkConfirmedMarker, StringComparison.Ordinal)));
        Assert.Equal(UiText.LinksLine([(NameOf(network, Root), "起点对比", true)]), network.Items.Single(i => i.Node == NodeOf(network, Sibling)).Links);
        network.ToggleViewCommand.Execute(null);

        // The summary page too.
        app.Workbench.Navigate(new NavigationRequest(MainSection.SummaryNetwork, new SummaryRoute(NodeOf(network, Root))));
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        await UiWait.UntilAsync(() => summary.Shown is not null);
        await summary.Links!.Loaded;
        window.UpdateLayout();
        var pageMarker = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LinkConfirmedMarker" && t.IsEffectivelyVisible);
        Assert.Equal(UiText.LinkConfirmedMarker, pageMarker.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ignore_removes_one_and_remembers_it_and_ignore_all_asks_once()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await SuggestAsync(app, window, network, (Root, Sibling, "对比", "一"), (Leaf, Sibling, "原因", "二"), (Root, Leaf, null, "三"));
        Assert.Equal(3, network.SuggestionEdges.Count);

        var row = Row(window, Leaf, Sibling);
        Assert.Equal(UiText.IgnoreLinkSuggestionName(NameOf(network, Leaf), NameOf(network, Sibling)), AutomationProperties.GetName(Find<Button>(row, "IgnoreSuggestionButton")));
        UiInput.Click(window, Find<Button>(row, "IgnoreSuggestionButton"));
        await UiWait.UntilAsync(() => network.SuggestionEdges.Count == 2);
        Assert.Equal(1, await CountAsync(app, "state = 'ignored'"));

        // 全部忽略 asks once (how many, and that they are not suggested again), 取消 keeps them, the second time it ignores.
        UiInput.Click(window, Find<Button>(window, "IgnoreAllSuggestionsButton"));
        window.UpdateLayout();
        Assert.True(Find<Border>(window, "IgnoreAllConfirmation").IsEffectivelyVisible);
        Assert.Equal(UiText.IgnoreAllLinkSuggestionsConfirm(2), network.Suggestions!.IgnoreAllConfirmText);
        UiInput.Click(window, Find<Button>(window, "CancelIgnoreAllButton"));
        Assert.Equal(2, network.Suggestions.Items.Count);
        UiInput.Click(window, Find<Button>(window, "IgnoreAllSuggestionsButton"));
        window.UpdateLayout();
        UiInput.Click(window, Find<Button>(window, "ConfirmIgnoreAllButton"));
        await UiWait.UntilAsync(() => network.SuggestionEdges.Count == 0 && network.Suggestions.Items.Count == 0);
        Assert.Equal(3, await CountAsync(app, "state = 'ignored'"));
        window.UpdateLayout();
        Assert.False(Find<StackPanel>(window, "SuggestionsSection").IsEffectivelyVisible);
        Assert.Empty(await app.Services.GetRequiredService<ISummaryLinks>().ListAsync(network.Workspace.Id, CancellationToken.None));

        // Asked again with the same pairs: nothing new, said quietly.
        app.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (NameOf(network, Root), NameOf(network, Sibling), "对比", "一"));
        await network.Suggestions.SuggestCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.Equal(UiText.LinkSuggestionsNoneNew, Find<TextBlock>(window, "LinkSuggestionsNote").Text);
        Assert.False(Find<Button>(window, "RetryLinkSuggestionsButton").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_whole_path_works_from_the_keyboard_with_names_for_a_screen_reader()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await SettledAsync(network);
        var gate = app.Client.LinkSuggestions.Hold();
        app.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (NameOf(network, Root), NameOf(network, Sibling), "对比", "都讲铁路的起点"));

        // The 关联 button by keyboard: Enter opens its menu, Enter on 建议关联 asks; a second ask while it runs does nothing.
        var button = LinksMenu.Button(window);
        Assert.Equal(UiText.LinksMenuName, AutomationProperties.GetName(button));
        button.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(button.Flyout!.IsOpen);
        var entry = LinksMenu.Entry(window, "SuggestLinksMenuItem");
        await UiWait.UntilAsync(() => entry.IsFocused || entry.IsSelected);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => network.Suggestions!.IsRunning && app.Client.Requests.Any(FakeChatClient.IsLinkSuggestion));
        window.UpdateLayout();
        Assert.True(Find<StackPanel>(window, "LinkSuggestionsBusyLine").IsEffectivelyVisible);
        Assert.False(network.Suggestions!.SuggestCommand.CanExecute(null));
        gate.TrySetResult();
        await UiWait.UntilAsync(() => network.Suggestions.Items.Count == 1 && !network.Suggestions.IsRunning);
        await network.SuggestionsReloaded;
        window.UpdateLayout();
        Assert.Single(app.Client.Requests, FakeChatClient.IsLinkSuggestion);

        // Tab reaches each control of the row, each named; Enter on 改说明 opens the editor; typing and Enter keep the note;
        // Tab to 接受 and Enter accepts.
        var row = Row(window, Root);
        Assert.Equal(UiText.LinkSuggestionName(NameOf(network, Root), NameOf(network, Sibling), "对比", "都讲铁路的起点"), AutomationProperties.GetName(row));
        Find<Button>(row, "SuggestedNameA").Focus(NavigationMethod.Tab);
        Assert.Equal(UiText.ShowSuggestedSummaryName(NameOf(network, Root)), AutomationProperties.GetName(Find<Button>(row, "SuggestedNameA")));
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(Find<Button>(row, "SuggestedNameB").IsFocused);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        var edit = Find<Button>(row, "EditSuggestedNoteButton");
        Assert.True(edit.IsFocused);
        Assert.Equal(UiText.EditSuggestedNoteName(NameOf(network, Root), NameOf(network, Sibling)), AutomationProperties.GetName(edit));
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        var box = Find<TextBox>(row, "SuggestedNoteBox");
        await UiWait.UntilAsync(() => box.IsFocused);
        box.SelectAll();
        window.KeyTextInput("起点");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.UpdateLayout();
        Assert.Equal("起点", network.Suggestions.Items[0].Note);
        var accept = Find<Button>(row, "AcceptSuggestionButton");
        accept.Focus(NavigationMethod.Tab);
        Assert.Equal(UiText.AcceptLinkSuggestionName(NameOf(network, Root), NameOf(network, Sibling)), AutomationProperties.GetName(accept));
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(Find<Button>(row, "IgnoreSuggestionButton").IsFocused);
        accept.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 1);
        var link = Assert.Single(await app.Services.GetRequiredService<ISummaryLinks>().ListAsync(network.Workspace.Id, CancellationToken.None));
        Assert.Equal(("起点", SummaryLinkOrigin.Confirmed), (link.Note, link.Origin));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_list_view_lists_the_suggestions_and_a_name_shows_its_summary_on_the_network()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await SuggestAsync(app, window, network, (Root, Sibling, "对比", "都讲铁路的起点"));

        network.ToggleViewCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(Find<StackPanel>(window, "LinkSuggestionsList").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(window, "SuggestionsSection").IsEffectivelyVisible);
        var row = Row(window, Root);
        UiInput.Click(window, Find<Button>(row, "SuggestedNameB"));
        Assert.False(network.IsListView);
        Assert.Equal(NodeOf(network, Sibling), network.Selected!.Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_click_on_a_suggestions_words_shows_its_row_with_accept_focused()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await SuggestAsync(app, window, network, (Root, Sibling, "对比", "一"), (Leaf, Sibling, "原因", "二"));
        network.Select(NodeOf(network, Leaf));
        window.UpdateLayout();
        Assert.False(Find<StackPanel>(window, "SuggestionsSection").IsEffectivelyVisible);

        var canvas = CanvasOf(window);
        var target = canvas.PaintSuggestions().Single(p => p.Label == UiText.LinkSuggestionLabel("原因"));
        UiInput.Click(window, canvas, at: target.LabelBounds.Center);
        window.UpdateLayout();
        Assert.Null(network.Selected);
        Assert.Equal(target.Suggestion.Id, network.HighlightedSuggestion);
        Assert.True(Find<StackPanel>(window, "SuggestionsSection").IsEffectivelyVisible);
        var row = Row(window, Leaf, Sibling);
        Assert.True(((LinkSuggestionItem)row.DataContext!).IsHighlighted);
        Assert.Contains("highlighted", row.Classes);
        await UiWait.UntilAsync(() => Find<Button>(row, "AcceptSuggestionButton").IsFocused);
        Assert.True(canvas.PaintSuggestions().Single(p => p.Suggestion.Id == target.Suggestion.Id).Active);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failure_is_said_quietly_with_retry_and_a_refused_key_or_no_model_points_to_the_settings()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedSeenAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await SettledAsync(network);
        var dialogs = window.GetVisualDescendants().OfType<Window>().Count();

        app.Client.LinkSuggestions.FailNext(GenerationErrorKind.ServerError);
        LinksMenu.Choose(window, "SuggestLinksMenuItem");
        await UiWait.UntilAsync(() => network.Suggestions!.HasNote);
        window.UpdateLayout();
        Assert.True(Find<WrapPanel>(window, "LinkSuggestionsNoteLine").IsEffectivelyVisible);
        Assert.Equal(UiText.LinkSuggestionsProblemText(Application.Recommendations.RecommendationProblem.Failed, GenerationErrorKind.ServerError), Find<TextBlock>(window, "LinkSuggestionsNote").Text);
        var retry = Find<Button>(window, "RetryLinkSuggestionsButton");
        Assert.True(retry.IsEffectivelyVisible);
        Assert.Equal(UiText.RetryLinkSuggestionsName, AutomationProperties.GetName(retry));
        Assert.False(Find<Button>(window, "LinkSuggestionsSettingsButton").IsEffectivelyVisible);
        Assert.Equal(dialogs, window.GetVisualDescendants().OfType<Window>().Count());

        // 重试 asks again; stored, the line goes.
        UiInput.Click(window, retry);
        await UiWait.UntilAsync(() => network.Suggestions!.Items.Count == 2 && !network.Suggestions.IsRunning);
        window.UpdateLayout();
        Assert.False(Find<WrapPanel>(window, "LinkSuggestionsNoteLine").IsEffectivelyVisible);

        // A refused key: said, with a way to the settings, and the stored suggestions stay.
        app.Client.LinkSuggestions.FailNext(GenerationErrorKind.Unauthorized);
        await network.Suggestions!.SuggestCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.Equal(UiText.LinkSuggestionsProblemText(Application.Recommendations.RecommendationProblem.Credentials, GenerationErrorKind.Unauthorized), Find<TextBlock>(window, "LinkSuggestionsNote").Text);
        Assert.Equal(2, network.Suggestions.Items.Count);
        var settings = Find<Button>(window, "LinkSuggestionsSettingsButton");
        Assert.True(settings.IsEffectivelyVisible);
        Assert.False(retry.IsEffectivelyVisible);

        // Reopened, the page says the same (the request was the user's own).
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await SettledAsync(network);
        Assert.Equal(UiText.LinkSuggestionsProblemText(Application.Recommendations.RecommendationProblem.Credentials, GenerationErrorKind.Unauthorized), network.Suggestions!.Note);

        // No model: why, and the settings.
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync((c, t) => c.Execute(t, "DELETE FROM app_state WHERE key = 'active_model_config_id';"), CancellationToken.None);
        await network.Suggestions.SuggestCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.Equal(UiText.LinkSuggestionsProblemText(Application.Recommendations.RecommendationProblem.NotConfigured, null), Find<TextBlock>(window, "LinkSuggestionsNote").Text);
        UiInput.Click(window, Find<Button>(window, "LinkSuggestionsSettingsButton"));
        Assert.Equal(MainSection.Settings, app.Workbench.CurrentSection);
        window.Close();
    }

    /// <summary>Asks through the toolbar's menu with the model naming these pairs (by short names), and waits for them on the network.</summary>
    internal static async Task SuggestAsync(DesktopHarness app, Window window, SummaryNetworkHomeViewModel network, params (string A, string B, string? Note, string? Reason)[] pairs)
    {
        await SettledAsync(network);
        var named = pairs.Select(p => (NameOf(network, p.A), NameOf(network, p.B), p.Note, p.Reason)).ToArray();
        app.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, named);
        LinksMenu.Choose(window, "SuggestLinksMenuItem");
        await UiWait.UntilAsync(() => network.Suggestions!.Items.Count == pairs.Length && !network.Suggestions.IsRunning);
        await UiWait.UntilAsync(() => network.SuggestionsReloaded.IsCompleted && network.SuggestionEdges.Count == (network.LinksHidden ? 0 : pairs.Length));
        window.UpdateLayout();
    }

    private static async Task SettledAsync(SummaryNetworkHomeViewModel network)
    {
        await network.Loaded;
        await network.Suggestions!.Loaded;
        await UiWait.UntilAsync(() => network.SuggestionsReloaded.IsCompleted);
    }

    /// <summary>Whether <paramref name="point"/> lies on the segment from <paramref name="from"/> to <paramref name="to"/> (within half a pixel).</summary>
    private static bool OnLine(Avalonia.Point point, Avalonia.Point from, Avalonia.Point to)
    {
        var (dx, dy) = (to.X - from.X, to.Y - from.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        var distance = Math.Abs((dx * (from.Y - point.Y)) - ((from.X - point.X) * dy)) / length;
        var t = (((point.X - from.X) * dx) + ((point.Y - from.Y) * dy)) / (length * length);
        return distance < 0.5 && t is > 0 and < 1;
    }

    /// <summary>The toolbar's tools all sit on one row: the same top for each one shown.</summary>
    private static void AssertOneRow(Window window)
    {
        var toolbar = Assert.IsType<WrapPanel>(Find<TextBox>(window, "FindBox").GetVisualParent());
        var shown = toolbar.Children.Where(c => c.IsVisible).ToList();
        Assert.True(shown.Count >= 8);
        var row = shown.Max(c => c.Bounds.Height);
        Assert.True(
            shown.All(c => c.Bounds.Top < row / 2) && toolbar.Bounds.Height <= row,
            "Toolbar wraps: " + string.Join(", ", shown.Select(c => $"{c.Name}@{c.Bounds}")) + $" in {toolbar.Bounds}");
    }

    private static SummaryNodeId NodeOf(SummaryNetworkHomeViewModel network, string shortTitle) => network.Nodes.Single(n => n.ShortTitle == shortTitle).Id;

    private static string NameOf(SummaryNetworkHomeViewModel network, string shortTitle) => network.Nodes.Single(n => n.ShortTitle == shortTitle).DisplayName;

    /// <summary>The shown suggestion row with the summary of <paramref name="shortTitle"/> (and of <paramref name="other"/>, when given).</summary>
    private static Border Row(Window window, string shortTitle, string? other = null)
    {
        var network = Assert.IsType<SummaryNetworkHomeViewModel>(((Control)window.Content!).GetVisualDescendants().OfType<SummaryNetworkHomeView>().Single().DataContext);
        var node = NodeOf(network, shortTitle);
        SummaryNodeId? second = other is null ? null : NodeOf(network, other);
        return window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SuggestionRow" && b.IsEffectivelyVisible
            && b.DataContext is LinkSuggestionItem item && item.Suggestion.Touches(node) && (second is not { } s || item.Suggestion.Touches(s)));
    }

    private static Task<long> CountAsync(DesktopHarness app, string where) =>
        app.Services.GetRequiredService<SqliteDatabase>().ReadAsync(c => c.Scalar<long>(null, $"SELECT count(*) FROM link_suggestion WHERE {where};"), CancellationToken.None);

    private static Task<long> LayoutRowsAsync(DesktopHarness app) =>
        app.Services.GetRequiredService<SqliteDatabase>().ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM node_layout;"), CancellationToken.None);

    private static async Task DeleteSummaryAsync(DesktopHarness app, SummaryNodeId node)
    {
        var summaries = app.Services.GetRequiredService<Application.Summaries.SummaryService>();
        var detail = (await summaries.GetAsync(node, CancellationToken.None))!;
        await summaries.DeleteVersionsAsync(node, detail.Versions.Select(v => v.Id).ToHashSet(), CancellationToken.None);
    }
}

internal static class LinkSuggestionsPanelTestExtensions
{
    /// <summary>全部忽略 as the two clicks do it: ask, then confirm; waits for the reload.</summary>
    public static async Task IgnoreAllCommandHelperAsync(this LinkSuggestionsPanel panel)
    {
        panel.AskIgnoreAllCommand.Execute(null);
        await panel.ConfirmIgnoreAllCommand.ExecuteAsync(null);
        await panel.Loaded;
    }
}
