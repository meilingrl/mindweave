using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
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
/// User-made content links between two summaries (requirements §10.3.3, 2026-11-16) in the real window: drawn solid in their
/// own colour with the note and a legend, apart from the arrangement and the layout; "关联到…" by a click on the network
/// (with Esc, 取消 and the reasons it refuses) and by a keyboard picker; the details' list with jump, inline note editing
/// and delete; 隐藏关联 remembered across a restart; the summary page's action and list; the counts in the deletion
/// confirmations.
/// </summary>
public class SummaryLinksUiTests
{
    private const string Root = "火车起源";
    private const string Leaf = "火箭号速度";
    private const string Sibling = "铁轨";

    [AvaloniaFact]
    public async Task Links_are_quiet_dashes_with_the_note_while_active_and_a_legend_and_never_touch_the_layout()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var positions = network.Session.Positions.ToDictionary(p => p.Key, p => p.Value);
        Assert.Empty(network.LinkEdges);
        Assert.False(network.ShowsLinkTools);
        LinksMenu.Open(window);
        Assert.False(LinksMenu.Shows(window, "ToggleLinksMenuItem"));
        LinksMenu.Close(window);
        Assert.False(LegendCard.Shows(window, "LinkLegend"));

        await LinkAsync(app, network, NodeOf(network, Leaf), NodeOf(network, Sibling), "对比");
        await LinkAsync(app, network, NodeOf(network, Root), NodeOf(network, Sibling), null);
        window.UpdateLayout();

        // Two link edges, at the summaries' positions; nothing about the layout changed.
        Assert.Equal(2, network.LinkEdges.Count);
        Assert.Equal(positions, network.Session.Positions.ToDictionary(p => p.Key, p => p.Value));

        // At rest a thin dash in the branch lines' colour, told apart from the solid branch line and the round-dotted
        // recommendation line by its dashes, and without words (requirements §10.3.3).
        var canvas = CanvasOf(window);
        var palette = SummaryNetworkPalette.From(canvas);
        var pen = palette.LinkPen(false);
        Assert.NotNull(pen.DashStyle);
        Assert.Null(palette.EdgePen(EdgeState.Normal).DashStyle);
        Assert.NotEqual(pen.DashStyle.Dashes, palette.RecommendationLink(false).DashStyle!.Dashes);
        Assert.Equal(2, canvas.PaintLinks().Count);
        Assert.All(canvas.PaintLinks(), p => Assert.Null(p.Label));

        // Selecting an end makes its links stronger, in the link colour; the note shows, and a link without one has no words.
        network.Select(NodeOf(network, Sibling));
        var paints = canvas.PaintLinks();
        Assert.All(paints, p => Assert.True(p.Active));
        Assert.True(palette.LinkPen(true).Thickness > pen.Thickness);
        Assert.Equal(((ISolidColorBrush)palette.Link).Color, ((ISolidColorBrush)palette.LinkPen(true).Brush!).Color);
        var noted = Assert.Single(paints, p => p.Link.Note is not null);
        Assert.Equal("对比", noted.Label);
        Assert.Null(Assert.Single(paints, p => p.Link.Note is null).Label);

        // On its line, near the middle.
        var centre = noted.NoteBounds.Center;
        var along = ((centre.X - noted.From.X) * (noted.To.X - noted.From.X)) + ((centre.Y - noted.From.Y) * (noted.To.Y - noted.From.Y));
        var length = Math.Pow(noted.To.X - noted.From.X, 2) + Math.Pow(noted.To.Y - noted.From.Y, 2);
        Assert.InRange(along / length, 0.25, 0.75);
        var onLine = noted.From + ((noted.To - noted.From) * (along / length));
        Assert.True(Math.Abs(onLine.X - centre.X) < 0.5 && Math.Abs(onLine.Y - centre.Y) < 0.5);
        var rootRing = canvas.PaintNodes().Single(n => n.Node.ShortTitle == Root);
        Assert.False(noted.NoteBounds.Intersects(new Avalonia.Rect(rootRing.Center.X - rootRing.Radius, rootRing.Center.Y - rootRing.Radius, rootRing.Radius * 2, rootRing.Radius * 2)));
        var leafPaint = canvas.PaintNodes().Single(n => n.Node.ShortTitle == Leaf);
        Assert.Contains(paints, p => p.From == leafPaint.Center || p.To == leafPaint.Center);

        // The legend says what the line is, in words; the toolbar offers 隐藏关联.
        window.UpdateLayout();
        Assert.True(LegendCard.Shows(window, "LinkLegend"));
        Assert.Contains(Find<StackPanel>(window, "LinkLegend").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == UiText.LegendLink);
        LinksMenu.Open(window);
        Assert.Equal(UiText.HideLinks, LinksMenu.Entry(window, "ToggleLinksMenuItem").Header);
        LinksMenu.Close(window);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Linking_by_a_click_says_why_it_refuses_and_Esc_or_cancel_leave_the_pick_mode()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await RecommendedNodesUiTests.PrepareAsync(app, network, window);
        var root = NodeOf(network, Root);
        var leaf = NodeOf(network, Leaf);
        var sibling = NodeOf(network, Sibling);
        await LinkAsync(app, network, root, sibling, null);
        var canvas = CanvasOf(window);
        UiInput.Click(window, canvas, at: canvas.PaintNodes().Single(n => n.Node.Id == root).Center);
        await network.LinksReloaded;
        window.UpdateLayout();
        var unread = network.Nodes.ToDictionary(n => n.Id, n => n.Unread);

        // 关联到… waits for a click, says so, and offers the keyboard way first.
        UiInput.Click(window, Find<Button>(window, "LinkToButton"));
        Assert.True(network.IsPicking);
        Assert.True(Find<Border>(window, "LinkPickLine").IsEffectivelyVisible);
        Assert.Equal(UiText.LinkPickHint(network.Selected!.DisplayName), Find<TextBlock>(window, "LinkPickHint").Text);
        await UiWait.UntilAsync(() => Find<Button>(window, "PickFromListButton").IsFocused);
        Assert.Equal(UiText.LinkPickFromListName, AutomationProperties.GetName(Find<Button>(window, "PickFromListButton")));
        Assert.Equal(UiText.LinkPickCancelName, AutomationProperties.GetName(Find<Button>(window, "CancelPickButton")));

        // Itself, a recommended node, a conversation without a summary and an already linked summary are refused with the reason; nothing is selected or made.
        UiInput.Click(window, canvas, at: canvas.PaintNodes().Single(n => n.Node.Id == root).Center);
        Assert.Equal(UiText.LinkPickRefusedSelf, network.PickRefusal);
        Assert.Equal(UiText.LinkPickRefusedSelf, Find<TextBlock>(window, "LinkPickHint").Text);
        UiInput.Click(window, canvas, at: Assert.Single(canvas.PaintRecommendations()).Center);
        Assert.Equal(UiText.LinkPickRefusedRecommended, network.PickRefusal);
        Assert.Null(network.SelectedRecommendation);
        var plain = canvas.PaintNodes().Single(n => !n.Node.HasSummary);
        UiInput.Click(window, canvas, at: plain.Center);
        Assert.Equal(UiText.LinkPickRefusedNoSummary(plain.Node.DisplayName), network.PickRefusal);
        UiInput.Click(window, canvas, at: canvas.PaintNodes().Single(n => n.Node.Id == sibling).Center);
        Assert.Equal(UiText.LinkPickRefusedLinked(network.Nodes.Single(n => n.Id == sibling).DisplayName), network.PickRefusal);
        Assert.Equal(root, network.Selected!.Id);
        Assert.True(network.IsPicking);
        Assert.Single(await app.Services.GetRequiredService<ISummaryLinks>().ListAsync(network.Workspace.Id, CancellationToken.None));

        // Esc leaves it; so does 取消; a click on empty canvas keeps it and keeps the selection.
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(network.IsPicking);
        window.UpdateLayout();
        Assert.False(Find<Border>(window, "LinkPickLine").IsEffectivelyVisible);
        network.BeginLinkCommand.Execute(null);
        UiInput.Click(window, Find<Button>(window, "CancelPickButton"));
        Assert.False(network.IsPicking);
        network.BeginLinkCommand.Execute(null);
        UiInput.Click(window, canvas, at: new Avalonia.Point(4, 4));
        Assert.True(network.IsPicking);
        Assert.Equal(root, network.Selected?.Id);

        // A click on another summary links it; the note editor opens, focused, and can be filled in or skipped.
        await UiInput.PastDoubleClickAsync();
        UiInput.Click(window, canvas, at: canvas.PaintNodes().Single(n => n.Node.Id == leaf).Center);
        await UiWait.UntilAsync(() => !network.IsPicking && network.Links!.Items.Count == 2 && network.Links.Items.Any(i => i.IsEditing));
        Assert.Equal(root, network.Selected!.Id);
        var added = network.Links!.Items.Single(i => i.Other == leaf);
        Assert.True(added.IsEditing && added.IsNew);
        Assert.Equal(UiText.LinkNoteSkip, added.CancelEditLabel);
        window.UpdateLayout();
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "LinkNoteBox" && b.IsEffectivelyVisible);
        await UiWait.UntilAsync(() => box.IsFocused);
        Assert.Equal(added.EditorName, AutomationProperties.GetName(box));
        window.KeyTextInput("原因");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => !added.IsEditing);
        await UiWait.UntilAsync(() => network.LinkEdges.Any(e => e.Note == "原因"));
        Assert.Equal(unread, network.Nodes.ToDictionary(n => n.Id, n => n.Unread));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_double_click_in_pick_mode_links_once_and_keeps_the_note_editor_instead_of_opening_the_summary()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var root = NodeOf(network, Root);
        var leaf = NodeOf(network, Leaf);
        var sibling = NodeOf(network, Sibling);
        var canvas = CanvasOf(window);
        UiInput.Click(window, canvas, at: canvas.PaintNodes().Single(n => n.Node.Id == root).Center);
        await network.LinksReloaded;
        window.UpdateLayout();
        var links = app.Services.GetRequiredService<ISummaryLinks>();

        // A double click whose second press comes once the link is made: it stays one pick; the summary is not opened and
        // the new link's note editor stays open.
        network.BeginLinkCommand.Execute(null);
        await UiInput.PastDoubleClickAsync();
        window.UpdateLayout();
        var origin = PointIn(window, canvas, default);
        var leafAt = PointIn(window, canvas, canvas.PaintNodes().Single(n => n.Node.Id == leaf).Center);
        Press(window, leafAt);
        await UiWait.UntilAsync(() => !network.IsPicking && network.Links!.Items.Any(i => i.IsEditing));
        await SettledAsync(network);
        window.UpdateLayout();

        // The canvas did not move under the pointer: the hint lies over it, and the toolbar's 隐藏关联 came with 关联到….
        Assert.Equal(origin, PointIn(window, canvas, default));
        SecondPress(window, leafAt);
        Assert.Same(network, app.Workbench.CurrentPage);
        Assert.Equal(root, network.Selected?.Id);
        Assert.True(Assert.Single(network.Links!.Items).IsEditing);
        Assert.Null(network.Links.Notice);

        // Both presses before the first link is written: still one link, and no "already linked" said about it.
        network.Links.Items[0].CancelEditCommand.Execute(null);
        network.BeginLinkCommand.Execute(null);
        await UiInput.PastDoubleClickAsync();
        var siblingAt = PointIn(window, canvas, canvas.PaintNodes().Single(n => n.Node.Id == sibling).Center);
        Press(window, siblingAt);
        SecondPress(window, siblingAt);

        await UiWait.UntilAsync(() => !network.IsPicking && network.Links.Items.Count == 2 && network.Links.Items.Any(i => i.IsEditing));
        await SettledAsync(network);
        Assert.Same(network, app.Workbench.CurrentPage);
        Assert.Equal(root, network.Selected?.Id);
        Assert.Null(network.Links.Notice);
        Assert.Null(network.PickRefusal);
        Assert.Equal(2, (await links.ListAsync(network.Workspace.Id, CancellationToken.None)).Count);

        // The same two clicks reaching the page at once (however they came): one link is written, nothing is refused.
        await links.DeleteAsync(network.Links.Items.Single(i => i.Other == sibling).Id, CancellationToken.None);
        await UiWait.UntilAsync(() => network.Links.Items.Count == 1 && network.LinkEdges.Count == 1);
        network.BeginLinkCommand.Execute(null);
        await Task.WhenAll(network.PickNodeAsync(sibling), network.PickNodeAsync(sibling));
        await UiWait.UntilAsync(() => network.Links.Items.Count == 2);
        Assert.False(network.IsPicking);
        Assert.Null(network.Links.Notice);
        Assert.Null(network.PickRefusal);

        // A double click on the summary itself: refused, and its page is not opened.
        network.Links.Items.Single(i => i.IsEditing).CancelEditCommand.Execute(null);
        network.BeginLinkCommand.Execute(null);
        await UiInput.PastDoubleClickAsync();
        var rootAt = PointIn(window, canvas, canvas.PaintNodes().Single(n => n.Node.Id == root).Center);
        Press(window, rootAt);
        SecondPress(window, rootAt);
        Assert.Same(network, app.Workbench.CurrentPage);
        Assert.True(network.IsPicking);
        Assert.Equal(UiText.LinkPickRefusedSelf, network.PickRefusal);

        // Outside pick mode a double click still opens the summary.
        network.CancelPickCommand.Execute(null);
        await UiInput.PastDoubleClickAsync();
        Press(window, leafAt);
        SecondPress(window, leafAt);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is SummaryViewModel);
        window.Close();

        // Raw presses: UiInput.Click lets the headless clock run between clicks, so two of them never make a double click.
        static Avalonia.Point PointIn(Window window, Control control, Avalonia.Point at) =>
            Avalonia.VisualExtensions.TranslatePoint(control, at, window) ?? throw new InvalidOperationException("The canvas left the window.");

        static void Press(Window window, Avalonia.Point at)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
        }

        // The second press of a double click. It says so itself instead of relying on the clock: on a slow machine the first
        // headless press runs the whole refresh the link caused, and a press measured by time is then a click of its own
        // (Linux CI runs 44797665196, 44806763101, 45196408589). It goes to whatever lies under the point, as a real one does.
        static void SecondPress(Window window, Avalonia.Point at)
        {
            var target = Assert.IsAssignableFrom<Avalonia.Interactivity.Interactive>(window.InputHitTest(at));
            var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
            target.RaiseEvent(new PointerPressedEventArgs(target, pointer, window, at, (ulong)Environment.TickCount64, properties, KeyModifiers.None, clickCount: 2));
        }
    }

    [AvaloniaFact]
    public async Task Leaving_the_picker_or_pick_mode_without_a_link_gives_focus_back_to_link_to_and_the_list_view_ends_pick_mode()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        network.Select(NodeOf(network, Root));
        await network.LinksReloaded;
        window.UpdateLayout();

        // Pick mode, then Esc: focus returns to 关联到… (it was on 从列表选择…, which is now hidden).
        UiInput.Click(window, Find<Button>(window, "LinkToButton"));
        await UiWait.UntilAsync(() => Find<Button>(window, "PickFromListButton").IsFocused);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(network.IsPicking);
        await UiWait.UntilAsync(() => Find<Button>(window, "LinkToButton").IsFocused);

        // The keyboard picker, then Esc: the same.
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => Find<Button>(window, "PickFromListButton").IsFocused);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => network.ShowsLinkPicker);
        window.UpdateLayout();
        await UiWait.UntilAsync(() => Find<TextBox>(window, "LinkPickerSearch").IsFocused);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(network.Links!.IsPickerOpen);
        await UiWait.UntilAsync(() => Find<Button>(window, "LinkToButton").IsFocused);

        // The list view has no nodes to click: pick mode ends with it, and the hint goes.
        network.BeginLinkCommand.Execute(null);
        Assert.True(network.IsPicking);
        network.ToggleViewCommand.Execute(null);
        Assert.False(network.IsPicking);
        window.UpdateLayout();
        Assert.False(Find<Border>(window, "LinkPickLine").IsEffectivelyVisible);
        network.ToggleViewCommand.Execute(null);

        // The summary page's picker: Esc gives focus back to its 关联到….
        var summary = await OpenSummaryAsync(app, window, NodeOf(network, Root));
        UiInput.Click(window, Find<Button>(window, "LinkToButton"));
        await UiWait.UntilAsync(() => summary.Links!.IsPickerOpen);
        window.UpdateLayout();
        await UiWait.UntilAsync(() => Find<TextBox>(window, "LinkPickerSearch").IsFocused);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(summary.Links!.IsPickerOpen);
        await UiWait.UntilAsync(() => Find<Button>(window, "LinkToButton").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Renaming_the_other_summary_renames_every_name_of_its_link_row()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var root = NodeOf(network, Root);
        var sibling = NodeOf(network, Sibling);
        await LinkAsync(app, network, root, sibling, null);
        network.Select(root);
        await network.LinksReloaded;
        var item = Assert.Single(network.Links!.Items);
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await app.Services.GetRequiredService<Application.Summaries.ISummaries>().RenameAsync(sibling, "改过的名字", CancellationToken.None);
        await UiWait.UntilAsync(() => item.OtherName == "改过的名字");
        Assert.Contains(nameof(SummaryLinkItem.EditorName), raised);
        Assert.Contains(nameof(SummaryLinkItem.DeleteConfirmText), raised);
        Assert.Equal(UiText.LinkNoteEditorName("改过的名字"), item.EditorName);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_keyboard_picker_links_from_the_keyboard_alone_and_shows_linked_summaries_disabled()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var root = NodeOf(network, Root);
        var leaf = NodeOf(network, Leaf);
        var sibling = NodeOf(network, Sibling);
        await LinkAsync(app, network, root, sibling, null);

        // From the list view (canvas nodes are not keyboard-reachable, D-018): Enter on a row opens it; the details offer the rest.
        network.Select(root);
        await network.LinksReloaded;
        window.UpdateLayout();
        var linkTo = Find<Button>(window, "LinkToButton");
        linkTo.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(network.IsPicking);
        var fromList = Find<Button>(window, "PickFromListButton");
        await UiWait.UntilAsync(() => fromList.IsFocused);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => network.ShowsLinkPicker);
        window.UpdateLayout();
        Assert.False(network.IsPicking);
        Assert.False(Find<ScrollViewer>(window, "DetailScroller").IsEffectivelyVisible);
        var search = Find<TextBox>(window, "LinkPickerSearch");
        await UiWait.UntilAsync(() => search.IsFocused);
        Assert.Equal(UiText.LinkPickerSearchName, AutomationProperties.GetName(search));
        Assert.Equal(UiText.LinkPickerHeading(network.Selected!.DisplayName), Find<TextBlock>(window, "LinkPickerHeading").Text);

        // Every other summary is listed; the linked one is disabled and says so.
        var rows = window.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "LinkCandidateRow").ToList();
        Assert.Equal(2, rows.Count);
        var linkedRow = rows.Single(r => ((SummaryLinkCandidateItem)r.DataContext!).Node == sibling);
        Assert.False(linkedRow.IsEffectivelyEnabled);
        Assert.Equal(UiText.LinkCandidateName(network.Nodes.Single(n => n.Id == sibling).DisplayName, linked: true), AutomationProperties.GetName(linkedRow));
        var leafName = network.Nodes.Single(n => n.Id == leaf).DisplayName;
        Assert.Equal(UiText.LinkCandidateName(leafName, linked: false), AutomationProperties.GetName(rows.Single(r => r != linkedRow)));

        // The search narrows the list; nothing matching says so; Esc closes the picker.
        window.KeyTextInput("没有这个");
        Assert.Empty(network.Links!.PickerItems);
        Assert.Equal(UiText.LinkPickerNoMatch, network.Links.PickerEmptyText);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(network.Links.IsPickerOpen);
        await network.Links.OpenPickerCommand.ExecuteAsync(null);
        window.UpdateLayout();
        await UiWait.UntilAsync(() => Find<TextBox>(window, "LinkPickerSearch").IsFocused);

        // Tab reaches the row that can be picked; Enter links it; the note editor takes focus; Esc skips the note.
        window.KeyTextInput(leafName[..2]);
        Assert.Equal([leaf], network.Links.PickerItems.Select(i => i.Node));
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LinkCandidateRow");
        Assert.True(row.IsFocused);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => network.Links.Items.Count == 2 && !network.Links.IsPickerOpen && network.Links.Items.Any(i => i.IsEditing));
        window.UpdateLayout();
        var added = network.Links.Items.Single(i => i.Other == leaf);
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "LinkNoteBox" && b.IsEffectivelyVisible);
        await UiWait.UntilAsync(() => box.IsFocused);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(added.IsEditing);
        Assert.Null(added.Note);
        Assert.True(Find<ScrollViewer>(window, "DetailScroller").IsEffectivelyVisible);

        // Enter in the search box picks the first row that can be picked; with none left, it does nothing.
        await network.Links.OpenPickerCommand.ExecuteAsync(null);
        Assert.All(network.Links.PickerItems, i => Assert.True(i.Linked));
        await network.Links.PickFirstCommand.ExecuteAsync(null);
        Assert.Equal(2, (await app.Services.GetRequiredService<ISummaryLinks>().ListAsync(network.Workspace.Id, CancellationToken.None)).Count);

        // The list view names each summary's links in words.
        network.Links.ClosePicker();
        network.ToggleViewCommand.Execute(null);
        await UiWait.UntilAsync(() => network.Items.Count(i => i.HasLinks) == 3);
        window.UpdateLayout();
        var lines = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "SummaryRowLinks" && t.IsEffectivelyVisible).Select(t => t.Text).ToList();

        // A screen reader tabbing through the rows hears each summary's links with its name.
        var rowButtons = Find<ItemsControl>(window, "SummaryList").GetVisualDescendants().OfType<Button>().Where(b => ((SummaryListItem)b.DataContext!).HasSummary).ToList();
        Assert.Equal(3, rowButtons.Count);
        Assert.All(rowButtons, b => Assert.Equal(((SummaryListItem)b.DataContext!).Links, AutomationProperties.GetHelpText(b)));
        Assert.All(rowButtons, b => Assert.StartsWith("关联：", AutomationProperties.GetHelpText(b), StringComparison.Ordinal));
        // In the order they were made.
        Assert.Contains(UiText.LinksLine([(network.Nodes.Single(n => n.Id == sibling).DisplayName, null, false), (network.Nodes.Single(n => n.Id == leaf).DisplayName, null, false)]), lines);
        Assert.Equal(3, lines.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_details_list_jumps_edits_the_note_with_inline_validation_and_deletes_after_one_confirmation()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var root = NodeOf(network, Root);
        var sibling = NodeOf(network, Sibling);
        await LinkAsync(app, network, root, sibling, "对比");
        network.Select(root);
        await network.LinksReloaded;
        window.UpdateLayout();

        // One row: the other summary in words, its note, and three buttons with their names.
        var item = Assert.Single(network.Links!.Items);
        var siblingName = network.Nodes.Single(n => n.Id == sibling).DisplayName;
        Assert.Equal(UiText.LinkItemName(siblingName, "对比"), AutomationProperties.GetName(Find<Border>(window, "LinkRow")));
        Assert.Equal("对比", Find<Border>(window, "LinkNote").GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Assert.Equal(UiText.JumpToLinkedName(siblingName), AutomationProperties.GetName(Find<Button>(window, "JumpToLinkedButton")));
        Assert.Equal(UiText.EditLinkNoteName(siblingName), AutomationProperties.GetName(Find<Button>(window, "EditLinkNoteButton")));
        Assert.Equal(UiText.DeleteLinkName(siblingName), AutomationProperties.GetName(Find<Button>(window, "DeleteLinkButton")));

        // Edit: over 16 characters is said while typing and cannot be saved; trimmed to fit it saves.
        UiInput.Click(window, Find<Button>(window, "EditLinkNoteButton"));
        Assert.True(item.IsEditing);
        Assert.Equal("对比", item.EditText);
        item.EditText = new string('长', 17);
        window.UpdateLayout();
        Assert.True(Find<TextBlock>(window, "LinkNoteError").IsEffectivelyVisible);
        Assert.Equal(UiText.LinkNoteTooLong(17), Find<TextBlock>(window, "LinkNoteError").Text);
        Assert.Equal("17/16", Find<TextBlock>(window, "LinkNoteCount").Text);
        Assert.False(Find<Button>(window, "SaveLinkNoteButton").IsEffectivelyEnabled);
        item.EditText = " 例子 ";
        window.UpdateLayout();
        Assert.False(Find<TextBlock>(window, "LinkNoteError").IsEffectivelyVisible);
        UiInput.Click(window, Find<Button>(window, "SaveLinkNoteButton"));
        await UiWait.UntilAsync(() => !item.IsEditing && item.Note == "例子");
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 1 && network.LinkEdges[0].Note == "例子");

        // Jump: the other summary is selected and brought to the centre.
        UiInput.Click(window, Find<Button>(window, "JumpToLinkedButton"));
        Assert.Equal(sibling, network.Selected?.Id);
        var target = network.Nodes.Single(n => n.Id == sibling);
        Assert.Equal((target.X, target.Y), (network.Session.Viewport!.Value.CenterX, network.Session.Viewport.Value.CenterY));
        await network.LinksReloaded;
        window.UpdateLayout();

        // Delete: one confirmation (no backup); then only the relation is gone, and its pair is remembered as ignored.
        item = Assert.Single(network.Links.Items);
        UiInput.Click(window, Find<Button>(window, "DeleteLinkButton"));
        Assert.True(item.IsConfirmingDelete);
        window.UpdateLayout();
        Assert.True(Find<Border>(window, "DeleteLinkConfirmation").IsEffectivelyVisible);
        UiInput.Click(window, Find<Button>(window, "CancelDeleteLinkButton"));
        Assert.False(item.IsConfirmingDelete);
        UiInput.Click(window, Find<Button>(window, "DeleteLinkButton"));
        window.UpdateLayout();
        UiInput.Click(window, Find<Button>(window, "ConfirmDeleteLinkButton"));
        await UiWait.UntilAsync(() => network.Links.Items.Count == 0);
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 0);
        Assert.Equal(4, network.Nodes.Count);
        Assert.Empty(app.Services.GetRequiredService<SqliteDatabase>().ListBackups());
        Assert.Equal(1, await app.Services.GetRequiredService<SqliteDatabase>().ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM link_suggestion WHERE state = 'ignored';"), CancellationToken.None));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hiding_links_is_display_only_and_remembered_for_the_workspace_across_a_restart()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await LinkAsync(app, network, NodeOf(network, Root), NodeOf(network, Leaf), "原因");
        window.UpdateLayout();

        LinksMenu.Choose(window, "ToggleLinksMenuItem");
        await UiWait.UntilAsync(() => !network.ToggleLinksCommand.IsRunning);
        window.UpdateLayout();
        Assert.Empty(network.LinkEdges);
        Assert.Empty(CanvasOf(window).PaintLinks());
        LinksMenu.Open(window);
        Assert.Equal(UiText.ShowLinks, LinksMenu.Entry(window, "ToggleLinksMenuItem").Header);
        LinksMenu.Close(window);
        Assert.False(LegendCard.Shows(window, "LinkLegend"));
        Assert.Single(await app.Services.GetRequiredService<ISummaryLinks>().ListAsync(network.Workspace.Id, CancellationToken.None));

        // The details still list it.
        network.Select(NodeOf(network, Root));
        await network.LinksReloaded;
        Assert.Single(network.Links!.Items);

        window.Close();
        await app.RestartAsync();
        window = app.ShowWindow(1100, 720);
        network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        Assert.True(network.LinksHidden);
        Assert.Empty(network.LinkEdges);

        // Opened afresh, the list view names the links too.
        Assert.Equal(2, network.Items.Count(i => i.HasLinks));
        Assert.Equal(UiText.ShowLinks, network.ToggleLinksLabel);

        await network.ToggleLinksCommand.ExecuteAsync(null);
        Assert.Single(network.LinkEdges);
        Assert.Null(await app.Services.GetRequiredService<Application.Storage.IPreferenceStore>().GetAsync(SummaryLinkService.HiddenKeyPrefix + network.Workspace.Id, CancellationToken.None));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_summary_page_links_through_its_picker_lists_its_links_quietly_and_opens_the_other_summary()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var root = NodeOf(network, Root);
        var sibling = NodeOf(network, Sibling);
        var siblingName = network.Nodes.Single(n => n.Id == sibling).DisplayName;
        var summary = await OpenSummaryAsync(app, window, root);

        // No links: only the action.
        Assert.False(Find<StackPanel>(window, "LinksArea").IsEffectivelyVisible);
        var linkTo = Find<Button>(window, "LinkToButton");
        Assert.True(linkTo.IsEffectivelyVisible);
        Assert.Equal(UiText.LinkToTip, AutomationProperties.GetHelpText(linkTo));

        // 关联到… opens the picker under the bar, focused; picking links and opens the note editor.
        UiInput.Click(window, linkTo);
        await UiWait.UntilAsync(() => summary.Links!.IsPickerOpen);
        window.UpdateLayout();
        Assert.True(Find<Border>(window, "LinkPickerPanel").IsEffectivelyVisible);
        await UiWait.UntilAsync(() => Find<TextBox>(window, "LinkPickerSearch").IsFocused);
        UiInput.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LinkCandidateRow" && ((SummaryLinkCandidateItem)b.DataContext!).Node == sibling));
        await UiWait.UntilAsync(() => summary.Links!.Items.Count == 1 && summary.Links.Items[0].IsEditing && !summary.Links.IsPickerOpen);
        window.UpdateLayout();
        Assert.False(Find<Border>(window, "LinkPickerPanel").IsEffectivelyVisible);
        Assert.True(Find<StackPanel>(window, "LinksArea").IsEffectivelyVisible);
        var item = Assert.Single(summary.Links!.Items);
        Assert.True(item.IsEditing);
        item.EditText = "例子";
        await item.SaveEditCommand.ExecuteAsync(null);
        Assert.Equal("例子", item.Note);
        Assert.Equal(UiText.OpenLinkedName(siblingName), AutomationProperties.GetName(Find<Button>(window, "JumpToLinkedButton")));

        // The body and the read state are untouched.
        Assert.False(summary.ShownIsRead);

        // Open: the other summary's page, listing the same link from its side; delete it there.
        UiInput.Click(window, Find<Button>(window, "JumpToLinkedButton"));
        var other = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(sibling, other.Node);
        await other.Loaded;
        await other.Links!.Loaded;
        Assert.Equal(root, Assert.Single(other.Links.Items).Other);
        await Assert.Single(other.Links.Items).ConfirmDeleteCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => other.Links.Items.Count == 0);
        window.UpdateLayout();
        Assert.False(Find<StackPanel>(window, "LinksArea").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Every_confirmation_that_removes_summaries_states_how_many_links_go_with_them()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        var root = NodeOf(network, Root);
        var leaf = NodeOf(network, Leaf);
        var sibling = NodeOf(network, Sibling);
        await LinkAsync(app, network, root, leaf, null);
        await LinkAsync(app, network, leaf, sibling, "对比");
        var conversationOf = network.Session.Projection.Nodes.ToDictionary(n => n.Id, n => n.Conversation);

        // Deleting the sibling conversation (its summary) takes one link.
        var dialog = app.Root.ConversationDeletion;
        dialog.Open(network.Workspace.Id, [conversationOf[sibling]]);
        await dialog.Loading;
        Assert.Equal(UiText.ConversationDeleteContents(1, 0, 1, 1), dialog.Contents);
        Assert.Contains("1 条内容关联随之移除", dialog.Contents, StringComparison.Ordinal);
        dialog.CancelCommand.Execute(null);

        // The root's whole tree (both other summaries): the two links touching it, each once.
        dialog.Open(network.Workspace.Id, [conversationOf[root]]);
        await dialog.Loading;
        Assert.Equal(2, dialog.Scope!.Links);
        Assert.Contains("2 条内容关联随之移除", dialog.Contents, StringComparison.Ordinal);
        dialog.CancelCommand.Execute(null);

        // The workspace: its links counted.
        var workspaceDialog = app.Root.WorkspaceDeletion;
        workspaceDialog.Open(app.Workbench.CurrentWorkspace!);
        await workspaceDialog.Loading;
        Assert.Equal(UiText.DeleteWorkspaceContents(4, 3, 2), workspaceDialog.Contents);
        workspaceDialog.CancelCommand.Execute(null);

        // A summary's last version: its links; a version that leaves the summary: none mentioned.
        var summary = await OpenSummaryAsync(app, window, leaf);
        await summary.Links!.Loaded;
        Assert.Single(summary.History).IsSelected = true;
        summary.DeleteSelectedCommand.Execute(null);
        Assert.Equal(UiText.DeleteVersionsConfirm(1, null, 2), summary.Confirmation!.Message);
        Assert.Contains("2 条内容关联随之移除", summary.Confirmation.Message, StringComparison.Ordinal);
        await summary.ConfirmCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is not SummaryViewModel);
        Assert.Empty(await app.Services.GetRequiredService<ISummaryLinks>().ListAsync(network.Workspace.Id, CancellationToken.None));

        // Without links nothing about them is said.
        Assert.DoesNotContain("关联", UiText.ConversationDeleteContents(2, 1, 1), StringComparison.Ordinal);
        Assert.DoesNotContain("关联", UiText.DeleteWorkspaceContents(2, 1), StringComparison.Ordinal);
        Assert.DoesNotContain("关联", UiText.DeleteVersionsConfirm(1, null), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>
    /// The scenario's three summaries, every answer seen: the hints its answers popped up are gone, so none covers the
    /// details panel a test clicks in.
    /// </summary>
    [AvaloniaFact]
    public async Task A_link_note_moves_along_its_line_off_a_summary_name_lying_at_the_middle()
    {
        // Review 2026-11-17 (gallery g55b): the note at the middle was drawn over another summary's name, hiding both.
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await LinkAsync(app, network, NodeOf(network, Leaf), NodeOf(network, Sibling), "来自推荐");
        window.UpdateLayout();
        var canvas = CanvasOf(window);

        // The root summary's name put across the middle of the link's line.
        var link = Assert.Single(network.LinkEdges);
        var root = network.Nodes.Single(n => n.ShortTitle == Root);
        network.Select(NodeOf(network, Leaf));
        network.PutDotForTests(root.Id, ((link.AX + link.BX) / 2) - 20, (link.AY + link.BY) / 2);
        window.UpdateLayout();
        var painted = Assert.Single(canvas.PaintLinks());
        var rootPaint = canvas.PaintNodes().Single(n => n.Node.Id == root.Id);
        var middle = new Avalonia.Point((painted.From.X + painted.To.X) / 2, (painted.From.Y + painted.To.Y) / 2);
        Assert.True(rootPaint.LabelBounds.Inflate(6).Contains(middle), "the root's name should lie on the middle of the line");

        // The note is still written, further along the line, clear of every summary's ring and name.
        Assert.Equal("来自推荐", painted.Label);
        Assert.False(painted.NoteBounds.Contains(middle));
        foreach (var node in canvas.PaintNodes())
        {
            var ring = new Avalonia.Rect(node.Center.X - node.Radius, node.Center.Y - node.Radius, node.Radius * 2, node.Radius * 2);
            Assert.False(painted.NoteBounds.Intersects(ring), $"note over the ring of {node.Node.ShortTitle}");
            Assert.False(node.LabelVisible && painted.NoteBounds.Intersects(node.LabelBounds), $"note over the name of {node.Node.ShortTitle}");
        }

        window.Close();
    }

    private static Task SeedAsync(DesktopHarness app) => SummaryNetworkScenario.SeedSeenAsync(app);

    private static SummaryNodeId NodeOf(SummaryNetworkHomeViewModel network, string shortTitle) => network.Nodes.Single(n => n.ShortTitle == shortTitle).Id;

    /// <summary>Links two summaries through the service, as the pickers do, and waits for the open network to show it.</summary>
    private static async Task LinkAsync(DesktopHarness app, SummaryNetworkHomeViewModel network, SummaryNodeId first, SummaryNodeId second, string? note)
    {
        var before = (await app.Services.GetRequiredService<ISummaryLinks>().ListAsync(network.Workspace.Id, CancellationToken.None)).Count;
        await app.Services.GetRequiredService<ISummaryLinks>().CreateAsync(first, second, note, CancellationToken.None);
        await UiWait.UntilAsync(() => network.LinkEdges.Count == before + 1 || (network.LinksHidden && network.LinksReloaded.IsCompleted));
        await SettledAsync(network);
    }

    /// <summary>The reload a link change posted has run, the details' list included.</summary>
    private static async Task SettledAsync(SummaryNetworkHomeViewModel network)
    {
        await UiWait.UntilAsync(() => network.LinksReloaded.IsCompleted);
        await network.LinksReloaded;
        await network.Links!.Loaded;
    }

    private static async Task<SummaryViewModel> OpenSummaryAsync(DesktopHarness app, Window window, SummaryNodeId node)
    {
        app.Workbench.Navigate(new NavigationRequest(MainSection.SummaryNetwork, new SummaryRoute(node)));
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        await UiWait.UntilAsync(() => summary.Shown is not null);
        await summary.Links!.Loaded;
        window.UpdateLayout();
        return summary;
    }
}
