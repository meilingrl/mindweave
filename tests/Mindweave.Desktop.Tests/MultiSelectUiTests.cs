using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Selecting several conversations in the sidebar and archiving or deleting them together, and the confirmation that
/// states what a delete takes along (requirements v0.12 §8.2), through the real window with real pointer and key input.
/// The seed is root › middle › leaf and root › sibling, with summaries of root, leaf and sibling; the sidebar lists
/// root, middle, leaf, sibling in that order.
/// </summary>
public class MultiSelectUiTests
{
    private static RawInputModifiers Toggle => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public async Task Ctrl_or_cmd_click_toggles_shift_click_takes_a_range_esc_clears_and_a_plain_click_opens()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var (root, middle, leaf, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(sibling, page.Id);
        Assert.False(Find<Control>(window, "SelectionBar").IsEffectivelyVisible);

        ClickRow(window, middle, Toggle);
        Assert.Equal([middle], Selected(list));
        Assert.Same(page, app.Workbench.CurrentPage);
        Assert.True(Find<Control>(window, "SelectionBar").IsEffectivelyVisible);
        Assert.Equal(UiText.SelectionCount(1), Find<TextBlock>(window, "SelectionCount").Text);
        Assert.Contains("checked", Row(window, middle).Classes);

        ClickRow(window, leaf, Toggle);
        Assert.Equal([middle, leaf], Selected(list));
        ClickRow(window, middle, Toggle);
        Assert.Equal([leaf], Selected(list));

        // From the last row toggled (middle, even though it was toggled off) up to root, in the order listed; nothing
        // else. The anchor stays, so the next Shift click spans from middle again.
        ClickRow(window, root, RawInputModifiers.Shift);
        Assert.Equal([root, middle], Selected(list));
        ClickRow(window, sibling, RawInputModifiers.Shift);
        Assert.Equal([middle, leaf, sibling], Selected(list));
        Assert.Same(page, app.Workbench.CurrentPage);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Empty(Selected(list));
        Assert.False(Find<Control>(window, "SelectionBar").IsEffectivelyVisible);

        // A plain click opens the conversation and ends the selection.
        ClickRow(window, leaf, Toggle);
        ClickRow(window, root);
        Assert.Empty(Selected(list));
        Assert.Equal(root, Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_selection_mode_shows_check_boxes_ticks_on_a_plain_click_and_ends_from_the_bar()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var (root, middle, _, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var page = app.Workbench.CurrentPage;
        var select = Find<ToggleButton>(window, "SelectConversationsButton");
        Assert.Equal(UiText.SelectConversations, AutomationProperties.GetName(select));
        Assert.Equal(UiText.SelectConversations, ToolTip.GetTip(select));
        Assert.All(Checks(window), c => Assert.False(c.IsEffectivelyVisible));
        var rowWidths = Rows(window).Select(r => r.Bounds.Width).ToList();

        Click(window, select);
        Assert.True(list.IsSelecting);
        var rootTitle = list.Items.Single(i => i.Id == root).Title;
        Assert.Equal(rootTitle, ControlAutomationPeer.CreatePeerForElement(Row(window, root)).GetName());
        Assert.True(select.IsChecked);
        Assert.All(Checks(window), c => Assert.True(c.IsEffectivelyVisible));
        Assert.True(Find<Control>(window, "SelectionBar").IsEffectivelyVisible);
        Assert.Equal(UiText.SelectionCount(0), Find<TextBlock>(window, "SelectionCount").Text);
        Assert.False(Find<Button>(window, "DeleteSelectedButton").IsEffectivelyEnabled);

        ClickRow(window, root);
        ClickRow(window, sibling);
        Assert.Equal([root, sibling], Selected(list));
        Assert.Same(page, app.Workbench.CurrentPage);
        Assert.Contains("checked", Find<Border>(Row(window, root), "RowCheck").Classes);
        Assert.DoesNotContain("checked", Find<Border>(Row(window, middle), "RowCheck").Classes);

        // The tick is drawn only; a screen reader hears it in the row's name.
        Assert.Equal(UiText.CheckedRowName(rootTitle), ControlAutomationPeer.CreatePeerForElement(Row(window, root)).GetName());
        Assert.Equal(list.Items.Single(i => i.Id == middle).Title, ControlAutomationPeer.CreatePeerForElement(Row(window, middle)).GetName());
        Assert.True(Find<Button>(window, "DeleteSelectedButton").IsEffectivelyEnabled);

        // Rows keep their width: the marker column is where it was, the box takes room from the title only.
        Assert.Equal(rowWidths, Rows(window).Select(r => r.Bounds.Width));

        // The boxes sit at the right edge, one column for every depth (root and branch alike), right of the marker column.
        // Measured in layout terms (the row's place plus the box's place inside it): a clicked row's Fluent press scale
        // eases back over a few frames, and a render-space measurement taken during it is off by up to a pixel or two.
        var boxEdges = Checks(window).Select(c =>
        {
            var row = c.FindAncestorOfType<Button>()!;
            return row.Bounds.X + c.TranslatePoint(new Point(c.Bounds.Width, 0), row)!.Value.X;
        }).ToList();
        Assert.True(boxEdges.Max() - boxEdges.Min() < 0.01, string.Join(" ", boxEdges));
        var marker = Row(window, middle).GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "RowMarker");
        Assert.True(marker.TranslatePoint(default, Row(window, middle))!.Value.X < Find<Border>(Row(window, middle), "RowCheck").TranslatePoint(default, Row(window, middle))!.Value.X);

        var clear = Find<Button>(window, "ClearSelectionButton");
        Assert.Equal(UiText.ClearSelection, AutomationProperties.GetName(clear));
        Click(window, clear);
        Assert.False(list.IsSelecting);
        Assert.Empty(Selected(list));
        Assert.False(Find<Control>(window, "SelectionBar").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_a_parent_and_its_child_together_confirms_the_union_once_backs_up_and_lands_on_the_explore_home()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var (root, _, leaf, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var summaries = app.Services.GetRequiredService<Application.Summaries.SummaryService>();
        var leafNode = (await summaries.ForConversationAsync(leaf, CancellationToken.None))!.Node.Id;
        app.Workbench.Navigate(new NavigationRequest(MainSection.Network, new SummaryRoute(leafNode)));
        var summaryPage = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(sibling)));
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel);
        var siblingPage = (ConversationViewModel)app.Workbench.CurrentPage!;

        ClickRow(window, root, Toggle);
        ClickRow(window, leaf, Toggle);
        Assert.Equal(UiText.Delete, AutomationProperties.GetName(Find<Button>(window, "DeleteSelectedButton")));
        Click(window, Find<Button>(window, "DeleteSelectedButton"));
        var dialog = app.Root.ConversationDeletion;
        await dialog.Loading;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // One confirmation for the union: the title counts the two rows picked, as the bar does; the sentence counts all
        // four conversations once each (the leaf is inside the root's scope), three of them branches.
        Assert.True(Find<Control>(window, "ConversationDeleteDialog").IsEffectivelyVisible);
        Assert.Equal(UiText.ConversationDeleteTitle(2), Find<TextBlock>(window, "ConversationDeleteTitle").Text);
        Assert.Equal(UiText.ConversationDeleteContents(4, 3, 3), Find<TextBlock>(window, "ConversationDeleteContents").Text);
        Assert.Equal("删除所选 2 个会话？", dialog.Title);
        Assert.Equal("4 个会话（含 3 个分支）、3 个总结将被永久删除，应用内无法撤销。删除前的备份会列在“数据与存储”中。", dialog.Contents);
        var cancel = Find<Button>(window, "ConversationDeleteCancel");
        Assert.True(cancel.IsDefault && cancel.IsCancel);
        Assert.True(cancel.IsFocused);
        Assert.Contains("danger", Find<Button>(window, "ConversationDeleteConfirm").Classes);
        Assert.True(Find<Button>(window, "ConversationDeleteArchive").IsEffectivelyVisible);
        Assert.Equal(UiText.ArchiveInstead, AutomationProperties.GetName(Find<Button>(window, "ConversationDeleteArchive")));

        Click(window, Find<Button>(window, "ConversationDeleteConfirm"));
        await dialog.Running;
        Dispatcher.UIThread.RunJobs();

        Assert.False(dialog.IsOpen);
        Assert.IsType<ExploreHomeViewModel>(app.Workbench.CurrentPage);
        Assert.True(siblingPage.IsDisposed);
        Assert.Empty(Selected(list));
        await UiWait.UntilAsync(() => list.Items.Count == 0);
        var backup = Assert.Single(app.Services.GetRequiredService<IDatabaseLifecycle>().ListBackups());
        Assert.Equal(BackupReason.BeforeConversationDelete, backup.Reason);
        Assert.Contains(UiText.BackupBeforeConversationDelete, UiText.FormatBackup(backup), StringComparison.Ordinal);
        Assert.Null(await summaries.ForConversationAsync(leaf, CancellationToken.None));

        // The deleted summary's page left the network section's stack too.
        app.Workbench.Navigate(new NavigationRequest(MainSection.Network));
        Assert.IsType<NetworkHomeViewModel>(app.Workbench.CurrentPage);
        Assert.NotSame(summaryPage, app.Workbench.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Archive_instead_archives_every_selected_tree_and_a_delete_of_plain_rows_needs_no_backup()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var (root, middle, _, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;

        ClickRow(window, middle, Toggle);
        ClickRow(window, sibling, Toggle);
        Click(window, Find<Button>(window, "DeleteSelectedButton"));
        var dialog = app.Root.ConversationDeletion;
        await dialog.Loading;
        window.UpdateLayout();
        Assert.Equal(UiText.ConversationDeleteTitle(2), dialog.Title);
        Assert.Equal(UiText.ConversationDeleteContents(3, 1, 2), dialog.Contents);

        Click(window, Find<Button>(window, "ConversationDeleteArchive"));
        await dialog.Running;
        Assert.False(dialog.IsOpen);
        await UiWait.UntilAsync(() => Selected(list).Count == 0);
        await UiWait.UntilAsync(() => list.Items.Count == 1 && list.ArchivedCount == 2);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        Assert.Equal(new HashSet<ConversationId> { middle, sibling }, (await conversations.ArchivedAmongAsync([root, middle, sibling], CancellationToken.None)).ToHashSet());

        // A lone conversation without branches or summary: the same dialog, the plain sentence, and no backup.
        var lone = await NetworkUiTests.AskAsync(app, conversations, null, app.Workbench.CurrentWorkspace!.Id, "单独的问题");
        await UiWait.UntilAsync(() => list.Items.Count == 2);
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == lone).Title == Mindweave.Testing.FakeChatClient.DefaultTitle);
        ClickRow(window, lone, Toggle);
        Click(window, Find<Button>(window, "DeleteSelectedButton"));
        await dialog.Loading;
        Assert.Equal(UiText.ConversationDeleteTitle(list.Items.Single(i => i.Id == lone).Title), dialog.Title);
        Assert.Equal("全部消息和未发送的草稿将被永久删除，无法撤销。", dialog.Contents);
        Assert.False(dialog.IsCascade);
        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.False(dialog.IsOpen);
        await UiWait.UntilAsync(() => list.Items.Count == 1);
        Assert.Empty(app.Services.GetRequiredService<IDatabaseLifecycle>().ListBackups());
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_bar_archives_each_selected_tree_once_and_names_what_was_refused()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var (root, middle, leaf, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var conversations = app.Services.GetRequiredService<ConversationService>();

        // A running answer in the leaf: archiving the middle (which holds it) is refused, the sibling goes.
        var held = app.Client.Hold("半句");
        var running = await conversations.AskAsync(leaf, "再问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ClickRow(window, middle, Toggle);
        ClickRow(window, leaf, Toggle);
        ClickRow(window, sibling, Toggle);
        Assert.Equal(UiText.Archive, AutomationProperties.GetName(Find<Button>(window, "ArchiveSelectedButton")));
        Click(window, Find<Button>(window, "ArchiveSelectedButton"));
        await list.Archiving;
        window.UpdateLayout();

        Assert.Equal(new HashSet<ConversationId> { sibling }, (await conversations.ArchivedAmongAsync([root, middle, leaf, sibling], CancellationToken.None)).ToHashSet());
        Assert.NotNull(list.SelectionNotice);
        Assert.Contains(list.Items.Single(i => i.Id == leaf).Title, list.SelectionNotice, StringComparison.Ordinal);
        Assert.True(Find<TextBlock>(window, "SelectionNotice").IsEffectivelyVisible);
        await UiWait.UntilAsync(() => list.Items.All(i => i.Id != sibling));
        Assert.Equal([middle, leaf], Selected(list));
        held.Finish();
        await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(leaf));
        _ = running;

        // Now it goes; the leaf is inside the middle's tree, so only the middle gets an archive mark of its own.
        Click(window, Find<Button>(window, "ArchiveSelectedButton"));
        await list.Archiving;
        Assert.Null(list.SelectionNotice);
        Assert.Empty(Selected(list));
        Assert.Equal(new HashSet<ConversationId> { middle, sibling }, (await conversations.ArchivedAmongAsync([root, middle, leaf, sibling], CancellationToken.None)).ToHashSet());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Running_work_keeps_delete_disabled_and_the_notice_follows_it_live()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var (root, _, leaf, _) = await SeedAsync(app, window);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        ClickRow(window, root, Toggle);
        Click(window, Find<Button>(window, "DeleteSelectedButton"));
        var dialog = app.Root.ConversationDeletion;
        await dialog.Loading;
        Assert.True(dialog.CanDelete);

        var held = app.Client.Hold("半句");
        await conversations.AskAsync(leaf, "再问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => dialog.Problem is not null);
        window.UpdateLayout();
        Assert.Contains(app.ShellPage.Conversations.Items.Single(i => i.Id == leaf).Title, dialog.Problem, StringComparison.Ordinal);
        Assert.True(Find<Control>(window, "ConversationDeleteProblem").IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "ConversationDeleteConfirm").IsEffectivelyEnabled);

        held.Finish();
        await UiWait.UntilAsync(() => dialog.Problem is null && dialog.CanDelete);
        window.UpdateLayout();
        Assert.True(Find<Button>(window, "ConversationDeleteConfirm").IsEffectivelyEnabled);

        // A branch made while the dialog is open is counted before anything is deleted, and the change is said.
        var before = dialog.Scope!.Conversations.Count;
        await NetworkUiTests.BranchAsync(app, conversations, root, "又一个分支");
        await UiWait.UntilAsync(() => dialog.Scope!.Conversations.Count == before + 1);
        // The branch's own answer runs for a moment, and the dialog recounts on a throttle: wait until the recount after
        // it ended has replaced the running-work notice with the change notice.
        await UiWait.UntilAsync(() => dialog.Problem == UiText.ConversationDeleteScopeChanged);
        Assert.Equal(UiText.ConversationDeleteContents(5, 4, 3), dialog.Contents);
        Assert.Equal(UiText.ConversationDeleteScopeChanged, dialog.Problem);
        window.UpdateLayout();
        Assert.True(Find<Control>(window, "ConversationDeleteProblem").IsEffectivelyVisible);

        // Esc in the dialog cancels it and leaves the selection as it was; nothing was deleted.
        Find<Button>(window, "ConversationDeleteCancel").Focus();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.IsOpen);
        Assert.Equal([root], Selected(app.ShellPage.Conversations));
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 5);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_backup_is_said_in_the_dialog_and_nothing_is_deleted()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var (root, _, _, _) = await SeedAsync(app, window);
        Directory.CreateDirectory(Path.GetDirectoryName(app.Paths.BackupDirectory)!);
        if (Directory.Exists(app.Paths.BackupDirectory))
        {
            Directory.Delete(app.Paths.BackupDirectory, recursive: true);
        }

        await File.WriteAllTextAsync(app.Paths.BackupDirectory, "not a directory");
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(root)));
        var rootPage = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await rootPage.Loaded;

        // From the conversation page: it takes branches along, so the dialog states the scope.
        await rootPage.DeleteCommand.ExecuteAsync(null);
        var dialog = app.Root.ConversationDeletion;
        await dialog.Loading;
        await dialog.DeleteCommand.ExecuteAsync(null);

        Assert.True(dialog.IsOpen);
        Assert.Equal(UiText.WorkspaceDeleteBackupFailed, dialog.Problem);
        Assert.Equal(4, app.ShellPage.Conversations.Items.Count);
        Assert.False(rootPage.IsDisposed);
        _ = page;
        dialog.CancelCommand.Execute(null);
        File.Delete(app.Paths.BackupDirectory);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Switching_workspace_drops_the_selection()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var (root, _, _, _) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        ClickRow(window, root, Toggle);
        Assert.True(list.HasSelection);
        var other = await app.Services.GetRequiredService<Application.Workspaces.WorkspaceService>().CreateAsync(Core.Workspaces.WorkspaceName.FromStored("天文"), CancellationToken.None);
        app.Workbench.Workspaces.Add(other);
        app.ShellPage.SelectedWorkspace = other;
        await UiWait.UntilAsync(() => list.Items.Count == 0);
        Assert.False(list.HasSelection);
        Assert.False(list.IsSelecting);
        window.Close();
    }

    /// <summary>
    /// A scope that grows while the dialog is open is said, as a change the transaction finds is, and a press that lands
    /// right after the change (it may have started before) deletes nothing; a later press deletes what is now shown.
    /// </summary>
    [AvaloniaFact]
    public async Task A_scope_that_changes_while_open_is_said_and_a_press_right_after_it_deletes_nothing()
    {
        var clock = new SetTime();
        await using var app = DesktopHarness.Create(s => s.AddSingleton(sp => new ConversationDeletionViewModel(
            sp.GetRequiredService<IConversationDeletion>(), sp.GetRequiredService<IConversations>(), sp.GetRequiredService<Workbench>(),
            sp.GetRequiredService<ConversationDeletionRequests>(), sp.GetRequiredService<Services.IUiDispatcher>(), clock)));
        var window = NetworkUiTests.Show(app);
        var (root, _, _, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var conversations = app.Services.GetRequiredService<ConversationService>();
        ClickRow(window, sibling, Toggle);
        Click(window, Find<Button>(window, "DeleteSelectedButton"));
        var dialog = app.Root.ConversationDeletion;
        await dialog.Loading;
        Assert.Null(dialog.Problem);
        Assert.Equal(UiText.ConversationDeleteContents(1, 0, 1), dialog.Contents);

        var branch = await NetworkUiTests.BranchAsync(app, conversations, sibling, "旁支的分支");
        await UiWait.UntilAsync(() => dialog.Scope!.Conversations.Count == 2);
        Assert.Equal(UiText.ConversationDeleteScopeChanged, dialog.Problem);
        Assert.Equal(UiText.ConversationDeleteContents(2, 1, 1), dialog.Contents);

        // Within the hold: the press is not taken as confirming counts the user may not have seen.
        clock.Now += TimeSpan.FromMilliseconds(100);
        Click(window, Find<Button>(window, "ConversationDeleteConfirm"));
        await dialog.Running;
        Assert.True(dialog.IsOpen);
        Assert.Empty(app.Services.GetRequiredService<IDatabaseLifecycle>().ListBackups());
        Assert.Contains(list.Items, i => i.Id == branch);

        clock.Now += ConversationDeletionViewModel.ChangeHold;
        Click(window, Find<Button>(window, "ConversationDeleteConfirm"));
        await dialog.Running;
        Assert.False(dialog.IsOpen);
        await UiWait.UntilAsync(() => list.Items.All(i => i.Id != branch && i.Id != sibling));
        Assert.Contains(list.Items, i => i.Id == root);
        window.Close();
    }

    /// <summary>A plain delete with an answer running in it is refused at once, not after an irreversible-delete question.</summary>
    [AvaloniaFact]
    public async Task A_plain_delete_with_work_running_says_so_before_asking()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var lone = await NetworkUiTests.AskAsync(app, conversations, null, app.Workbench.CurrentWorkspace!.Id, "单独的问题");
        // The background title lands at its own time; the refusal names the conversation, so let it land first.
        await UiWait.UntilAsync(() => list.Items.Any(i => i.Id == lone && i.Title == Mindweave.Testing.FakeChatClient.DefaultTitle));
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(lone)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;

        var held = app.Client.Hold("半句");
        await conversations.AskAsync(lone, "再问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.DeleteCommand.ExecuteAsync(null);

        Assert.Null(page.Confirmation);
        Assert.False(app.Root.ConversationDeletion.IsOpen);
        await UiWait.UntilAsync(() => list.Items.Any(i => i.Id == lone));
        Assert.Equal(UiText.DeleteWorkspaceBusy([(list.Items.Single(i => i.Id == lone).Title, true, false)]), page.ArchiveNotice);
        held.Finish();
        await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(lone));
        window.Close();
    }

    /// <summary>A delete from a conversation page leaves an unrelated sidebar selection as it was.</summary>
    [AvaloniaFact]
    public async Task A_delete_opened_from_a_page_keeps_an_unrelated_selection()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var (_, middle, leaf, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        ClickRow(window, sibling, Toggle);
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(middle)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;

        // The middle has the leaf below it: the page opens the dialog.
        await page.DeleteCommand.ExecuteAsync(null);
        var dialog = app.Root.ConversationDeletion;
        await dialog.Loading;
        Assert.True(dialog.IsOpen);
        window.UpdateLayout();
        Click(window, Find<Button>(window, "ConversationDeleteConfirm"));
        await dialog.Running;
        Assert.False(dialog.IsOpen);
        await UiWait.UntilAsync(() => list.Items.All(i => i.Id != middle && i.Id != leaf));
        Assert.Equal([sibling], Selected(list));
        Assert.True(Find<Control>(window, "SelectionBar").IsEffectivelyVisible);
        window.Close();
    }

    /// <summary>The clock the delete dialog reads, moved only by the test.</summary>
    private sealed class SetTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 9, 0, 0, TimeSpan.FromHours(8));

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The network seed, the sibling's page open and the sidebar laid out; returns root, middle, leaf, sibling.</summary>
    private static async Task<(ConversationId Root, ConversationId Middle, ConversationId Leaf, ConversationId Sibling)> SeedAsync(DesktopHarness app, MainWindow window)
    {
        await NetworkUiTests.SeedAsync(app);
        var list = app.ShellPage.Conversations;
        await UiWait.UntilAsync(() => list.Items.Count == 4);
        var ids = list.Items.Select(i => i.Id).ToList();
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(ids[3])));
        await ((ConversationViewModel)app.Workbench.CurrentPage!).Loaded;
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == ids[3]).IsSelected);
        window.UpdateLayout();
        return (ids[0], ids[1], ids[2], ids[3]);
    }

    private static List<ConversationId> Selected(ConversationListViewModel list) => [.. list.Selection.Select(i => i.Id)];

    private static IEnumerable<Button> Rows(Window window) =>
        Find<ItemsControl>(window, "ConversationList").GetVisualDescendants().OfType<Button>();

    private static Button Row(Window window, ConversationId id) =>
        Rows(window).Single(b => b.DataContext is ConversationItemViewModel item && item.Id == id);

    private static IEnumerable<Border> Checks(Window window) =>
        Find<ItemsControl>(window, "ConversationList").GetVisualDescendants().OfType<Border>().Where(b => b.Name == "RowCheck");

    /// <summary>A real click on the row's title, with the modifiers held.</summary>
    private static void ClickRow(Window window, ConversationId id, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.UpdateLayout();
        var title = Find<TextBlock>(Row(window, id), "RowTitle");
        var point = title.TranslatePoint(new Point(4, title.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
}
