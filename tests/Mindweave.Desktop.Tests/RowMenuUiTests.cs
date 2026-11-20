using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using TextBlock = Avalonia.Controls.TextBlock;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The sidebar row's context menu (2026-11-10): the page toolbar's 改名, 总结此会话, 归档 and 删除… on a row, through the
/// real window with real pointer and key input, each with the same stored outcome as the page's button. The seed is
/// root › middle › leaf and root › sibling, with summaries of root, leaf and sibling; the sidebar lists root, middle,
/// leaf, sibling in that order, and the sibling's page is open.
/// </summary>
public class RowMenuUiTests
{
    private static RawInputModifiers Toggle => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public async Task A_right_click_opens_the_menu_without_opening_the_row_and_open_opens_it()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (_, middle, _, sibling) = await SeedAsync(app, window);
        var page = app.Workbench.CurrentPage;

        var menu = OpenMenu(window, middle);
        Assert.Same(page, app.Workbench.CurrentPage);
        Assert.Empty(app.ShellPage.Conversations.Selection);

        // The row keeps a hover tint while its menu is open, so it is clear what the menu acts on.
        Assert.Contains("menu-open", Row(window, middle).Classes);
        Assert.Equal(ThemeColour("HoverRow", Avalonia.Styling.ThemeVariant.Light), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(Row(window, middle).Background).Color);

        // The page's entries in the page's words and icons, the default first and bold, the destructive one last in the danger colour.
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["RowMenuOpen", "RowMenuRename", "RowMenuSummarize", "RowMenuArchive", "RowMenuDelete"], items.Select(i => i.Name));
        Assert.Equal([UiText.RowOpen, UiText.Rename, UiText.Summarize, UiText.Archive, UiText.RowDelete], items.Select(i => i.Header));
        Assert.All(items, i => Assert.IsType<PathIcon>(i.Icon));
        Assert.All(items, i => Assert.True(i.IsEffectivelyEnabled, i.Name));
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, items[0].FontWeight);
        Assert.Contains("danger", items[^1].Classes);
        Assert.DoesNotContain(items.SkipLast(1), i => i.Classes.Contains("danger"));
        Assert.Equal(2, menu.Items.OfType<Separator>().Count());

        // The entries sit inside the rounded card with a margin all round, and their hover band is rounded too
        // (2026-11-10: they used to run edge to edge).
        window.UpdateLayout();
        Assert.Equal(12, menu.CornerRadius.TopLeft);
        foreach (var item in items)
        {
            var at = item.TranslatePoint(default, menu)!.Value;
            Assert.True(at.X >= 5 && menu.Bounds.Width - (at.X + item.Bounds.Width) >= 5, $"{item.Name} at {at.X}, {item.Bounds.Width} of {menu.Bounds.Width}");
            var band = item.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");
            Assert.Equal(8, band.CornerRadius.TopLeft);
        }

        await ClickEntryAsync(window, "RowMenuOpen");
        Dispatcher.UIThread.RunJobs();
        Assert.False(menu.IsOpen);
        Assert.DoesNotContain("menu-open", Row(window, middle).Classes);
        var opened = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(middle, opened.Id);
        await opened.Loaded;
        Assert.NotEqual(sibling, opened.Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Shift_F10_and_the_menu_key_open_the_menu_on_the_focused_row()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (root, middle, _, _) = await SeedAsync(app, window);

        Row(window, middle).Focus(NavigationMethod.Tab);
        window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        Dispatcher.UIThread.RunJobs();
        var menu = Row(window, middle).ContextMenu!;
        Assert.True(menu.IsOpen);

        // Esc closes it and leaves everything as it was.
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(menu.IsOpen);

        Row(window, root).Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
        window.KeyRelease(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Row(window, root).ContextMenu!.IsOpen);
        Row(window, root).ContextMenu!.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Rename_edits_the_title_in_the_row_enter_or_leaving_saves_as_the_page_does_and_esc_cancels()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (root, middle, leaf, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var middleTitle = list.Items.Single(i => i.Id == middle).Title;

        // The title turns into a box holding it, all selected, with the focus; the row keeps its place and width.
        var rowBounds = Row(window, middle).Bounds;
        OpenMenu(window, middle);
        await ClickEntryAsync(window, "RowMenuRename");
        await UiWait.UntilAsync(() => RenameBox(window, middle) is { IsFocused: true, IsEffectivelyVisible: true });
        var box = RenameBox(window, middle);
        Assert.False(Row(window, middle).IsVisible);
        Assert.Equal(middleTitle, box.Text);
        Assert.Equal(middleTitle, box.SelectedText);
        Assert.Equal(UiText.RowRenameBox, AutomationProperties.GetName(box));
        Assert.Equal(rowBounds.Width, box.Bounds.Width);
        Assert.Equal(rowBounds.Height, box.Bounds.Height);
        Assert.Equal(rowBounds.X, box.Bounds.X);

        // Typing replaces the selection; Enter saves through the page's rename and gives the focus back to the row.
        window.KeyTextInput("蒸汽机车的先驱");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await list.RowAction;
        await UiWait.UntilAsync(() => Row(window, middle) is { IsVisible: true, IsFocused: true });
        Assert.Equal(sibling, ((ConversationViewModel)app.Workbench.CurrentPage!).Id);
        var stored = (await conversations.LoadAsync(middle, CancellationToken.None))!.Conversation;
        Assert.Equal(("蒸汽机车的先驱", TitleSource.Manual), (stored.Title, stored.TitleSource));
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == middle).Title == "蒸汽机车的先驱");

        // The page's rename of the same text stores the same.
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(leaf)));
        var leafPage = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await leafPage.Loaded;
        leafPage.BeginRenameCommand.Execute(null);
        leafPage.RenameText = "蒸汽机车的先驱";
        await leafPage.CommitRenameCommand.ExecuteAsync(null);
        var byPage = (await conversations.LoadAsync(leaf, CancellationToken.None))!.Conversation;
        Assert.Equal((stored.Title, stored.TitleSource), (byPage.Title, byPage.TitleSource));

        // Esc leaves the title as it was.
        var rootTitle = list.Items.Single(i => i.Id == root).Title;
        OpenMenu(window, root);
        await ClickEntryAsync(window, "RowMenuRename");
        await UiWait.UntilAsync(() => RenameBox(window, root).IsFocused);
        window.KeyTextInput("不要了");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        await UiWait.UntilAsync(() => Row(window, root) is { IsVisible: true, IsFocused: true });
        Assert.False(list.Items.Single(i => i.Id == root).IsRenaming);
        Assert.Equal(rootTitle, list.Items.Single(i => i.Id == root).Title);
        Assert.Equal(TitleSource.Auto, (await conversations.LoadAsync(root, CancellationToken.None))!.Conversation.TitleSource);

        // A save after the box was left (a late Enter, a second focus loss) saves nothing.
        await list.CommitRenameRowCommand.ExecuteAsync(list.Items.Single(i => i.Id == root));
        Assert.Equal(TitleSource.Auto, (await conversations.LoadAsync(root, CancellationToken.None))!.Conversation.TitleSource);

        // Leaving the box (a click on another row) saves, as file lists do.
        OpenMenu(window, sibling);
        await ClickEntryAsync(window, "RowMenuRename");
        await UiWait.UntilAsync(() => RenameBox(window, sibling).IsFocused);
        window.KeyTextInput("铁轨的材料");
        Find<TextBox>(CurrentView(window), "Input").Focus();
        await list.RowAction;
        Assert.Equal("铁轨的材料", (await conversations.LoadAsync(sibling, CancellationToken.None))!.Conversation.Title);
        Assert.False(RenameBox(window, sibling).IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Summarize_starts_the_pages_summary_in_the_background_and_a_refusal_is_named_in_the_sidebar()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (_, middle, _, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var summaries = app.Services.GetRequiredService<SummaryService>();
        Assert.Null(await summaries.ForConversationAsync(middle, CancellationToken.None));

        // The middle has no summary: one is generated for it while the sibling's page stays on screen; the row spins meanwhile.
        var gate = app.Client.Knowledge.Hold();
        app.Client.Knowledge.Replies.Enqueue("标题：第一台机车的制造者\n短名：第一台机车\n\n正文");
        OpenMenu(window, middle);
        await ClickEntryAsync(window, "RowMenuSummarize");
        await list.RowAction;
        Assert.Null(list.RowNotice);
        Assert.Equal(sibling, Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Id);
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == middle).Mark == ConversationMark.Summarizing);

        // While it runs the entry is disabled, saying why (the page shows 生成中 in place of its button).
        OpenMenu(window, middle);
        AssertDisabled(window, "RowMenuSummarize", UiText.SummarizeRejected(SummarizeRejection.SummaryInProgress));
        CloseMenu(window, middle);
        gate.SetResult();
        await UiWait.UntilAsync(() => summaries.LatestTaskAsync(middle, CancellationToken.None).Result is { State: GenerationTaskState.Completed });
        Assert.Equal("第一台机车的制造者", (await summaries.ForConversationAsync(middle, CancellationToken.None))!.Current.Title);
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == middle).Mark != ConversationMark.Summarizing);

        // A refusal the row could not foresee (no finished answer yet) is said above the sidebar's foot, with the title.
        var conversations = app.Services.GetRequiredService<ConversationService>();
        app.Client.Fail(GenerationErrorKind.Network);
        var failed = (await conversations.StartAsync(app.Workbench.CurrentWorkspace!.Id, "没有回答的问题", CancellationToken.None)).Conversation;
        await UiWait.UntilAsync(() => list.Items.SingleOrDefault(i => i.Id == failed) is { NeedsAttention: true });
        var title = list.Items.Single(i => i.Id == failed).Title;
        OpenMenu(window, failed);
        await ClickEntryAsync(window, "RowMenuSummarize");
        await list.RowAction;
        window.UpdateLayout();
        Assert.Equal(UiText.RowNotice(title, UiText.SummarizeRejected(SummarizeRejection.NothingToSummarize)), list.RowNotice);
        Assert.True(Find<TextBlock>(window, "RowNotice").IsEffectivelyVisible);
        UiInput.Click(window, Find<Button>(window, "CloseRowNoticeButton"));
        Assert.Null(list.RowNotice);
        Assert.False(Find<Control>(window, "RowNoticePanel").IsEffectivelyVisible);

        // It belongs to its workspace: switching to another one takes it away.
        await list.SummarizeRowCommand.ExecuteAsync(list.Items.Single(i => i.Id == failed));
        Assert.NotNull(list.RowNotice);
        app.ShellPage.NewWorkspace.Name = "城市交通";
        await app.ShellPage.NewWorkspace.SubmitCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Name.Value == "城市交通");
        Assert.Null(list.RowNotice);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Archive_hides_the_conversation_with_its_branches_as_the_page_does()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (root, middle, leaf, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var conversations = app.Services.GetRequiredService<ConversationService>();

        OpenMenu(window, middle);
        await ClickEntryAsync(window, "RowMenuArchive");
        await list.RowAction;
        Assert.Null(list.RowNotice);
        await UiWait.UntilAsync(() => list.Items.Select(i => i.Id).SequenceEqual([root, sibling]) && list.ArchivedCount == 1);
        Assert.Equal(new HashSet<ConversationId> { middle }, (await conversations.ArchivedAmongAsync([root, middle, leaf, sibling], CancellationToken.None)).ToHashSet());
        Assert.True((await conversations.PlanRestoreAsync(leaf, CancellationToken.None)).IsHidden);

        // The page's button on the open sibling stores the same kind of mark: its own, nothing above it.
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Lifecycle.ArchiveCommand.ExecuteAsync(null);
        Assert.Equal(new HashSet<ConversationId> { middle, sibling }, (await conversations.ArchivedAmongAsync([root, middle, leaf, sibling], CancellationToken.None)).ToHashSet());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Delete_goes_through_the_scope_dialog_and_backs_up_first()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (root, _, leaf, _) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var rootTitle = list.Items.Single(i => i.Id == root).Title;

        OpenMenu(window, root);
        await ClickEntryAsync(window, "RowMenuDelete");
        var dialog = app.Root.ConversationDeletion;
        await dialog.Loading;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // The selection's dialog for one row: its title, every conversation and summary that goes, 改为归档, 取消 focused.
        await UiWait.UntilAsync(() =>
        {
            window.UpdateLayout();
            return Find<Control>(window, "ConversationDeleteDialog").IsEffectivelyVisible;
        });
        Assert.Equal(UiText.ConversationDeleteTitle(rootTitle), dialog.Title);
        Assert.Equal(UiText.ConversationDeleteContents(4, 3, 3), dialog.Contents);
        Assert.True(Find<Button>(window, "ConversationDeleteCancel").IsFocused);
        Assert.Empty(app.Services.GetRequiredService<IDatabaseLifecycle>().ListBackups());

        UiInput.Click(window, Find<Button>(window, "ConversationDeleteConfirm"));
        await dialog.Running;
        Assert.False(dialog.IsOpen);
        await UiWait.UntilAsync(() => list.Items.Count == 0);
        Assert.IsType<ExploreHomeViewModel>(app.Workbench.CurrentPage);
        var backup = Assert.Single(app.Services.GetRequiredService<IDatabaseLifecycle>().ListBackups());
        Assert.Equal(BackupReason.BeforeConversationDelete, backup.Reason);
        Assert.Null(await app.Services.GetRequiredService<SummaryService>().ForConversationAsync(leaf, CancellationToken.None));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Entries_are_disabled_with_the_reason_while_work_runs_in_their_scope()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (root, middle, leaf, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var conversations = app.Services.GetRequiredService<ConversationService>();

        // An answer runs in the leaf: the leaf cannot be summarized; the leaf and every row above it cannot be archived or
        // deleted (they would take it along); the sibling is untouched. Renaming and opening stay.
        var held = app.Client.Hold("半句");
        await conversations.AskAsync(leaf, "再问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == leaf).IsGenerating);

        OpenMenu(window, leaf);
        Assert.True(MenuEntry(window, "RowMenuOpen").IsEffectivelyEnabled);
        Assert.True(MenuEntry(window, "RowMenuRename").IsEffectivelyEnabled);
        AssertDisabled(window, "RowMenuSummarize", UiText.SummarizeRejected(SummarizeRejection.AnswerInProgress));
        AssertDisabled(window, "RowMenuArchive", UiText.RowArchiveBusy);
        AssertDisabled(window, "RowMenuDelete", UiText.RowDeleteBusy);

        // A press on a disabled entry does nothing.
        UiInput.Click(window, MenuEntry(window, "RowMenuArchive"));
        CloseMenu(window, leaf);
        Assert.Empty(await conversations.ArchivedAmongAsync([root, middle, leaf, sibling], CancellationToken.None));

        foreach (var above in new[] { middle, root })
        {
            OpenMenu(window, above);
            Assert.True(MenuEntry(window, "RowMenuSummarize").IsEffectivelyEnabled);
            AssertDisabled(window, "RowMenuArchive", UiText.RowArchiveBusy);
            AssertDisabled(window, "RowMenuDelete", UiText.RowDeleteBusy);
            CloseMenu(window, above);
        }

        OpenMenu(window, sibling);
        Assert.All(Entries(window), e => Assert.True(e.IsEffectivelyEnabled, e.Name));
        CloseMenu(window, sibling);

        // The view model refuses too, whatever calls it.
        var leafRow = list.Items.Single(i => i.Id == leaf);
        await list.ArchiveRowCommand.ExecuteAsync(leafRow);
        Assert.Null(list.RowNotice);
        await list.SummarizeRowCommand.ExecuteAsync(leafRow);
        Assert.Null(list.RowNotice);
        list.DeleteRowCommand.Execute(leafRow);
        Assert.False(app.Root.ConversationDeletion.IsOpen);
        Assert.Empty(await conversations.ArchivedAmongAsync([root, middle, leaf, sibling], CancellationToken.None));

        held.Finish();
        await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(leaf) && !list.Items.Single(i => i.Id == leaf).IsGenerating);
        OpenMenu(window, root);
        Assert.All(Entries(window), e => Assert.True(e.IsEffectivelyEnabled, e.Name));
        CloseMenu(window, root);
        window.Close();
    }

    [AvaloniaFact]
    public async Task While_storage_is_paused_every_entry_that_writes_is_disabled_with_the_reason()
    {
        // No recovery pass during the test: the pause stays until the test ends.
        await using var app = DesktopHarness.Create(s => s.AddSingleton(new StorageHealthOptions { RetryInterval = TimeSpan.FromHours(1) }));
        var window = app.ShowWindow(1100, 720);
        var (_, middle, _, _) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;

        app.Services.GetRequiredService<StorageHealth>().Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));
        await UiWait.UntilAsync(() => list.IsStoragePaused);
        OpenMenu(window, middle);
        Assert.True(MenuEntry(window, "RowMenuOpen").IsEffectivelyEnabled);
        foreach (var name in new[] { "RowMenuRename", "RowMenuSummarize", "RowMenuArchive", "RowMenuDelete" })
        {
            AssertDisabled(window, name, UiText.RowStoragePaused);
        }

        CloseMenu(window, middle);

        // The view model refuses too.
        var row = list.Items.Single(i => i.Id == middle);
        list.BeginRenameRowCommand.Execute(row);
        Assert.False(row.IsRenaming);
        Assert.All(list.Items, i => Assert.True(i.IsStoragePaused));
        window.Close();
    }

    /// <summary>
    /// A refusal the row's state could not foresee (its marker or the pause had not caught up yet) is said above the
    /// sidebar's foot, in the page's words: running work named by title, a write storage refused.
    /// </summary>
    [AvaloniaFact]
    public async Task Refusals_the_row_could_not_foresee_are_named_in_the_sidebar()
    {
        await using var app = DesktopHarness.Create(s => s.AddSingleton(new StorageHealthOptions { RetryInterval = TimeSpan.FromHours(1) }));
        var window = app.ShowWindow(1100, 720);
        var (root, middle, leaf, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var conversations = app.Services.GetRequiredService<ConversationService>();

        var held = app.Client.Hold("半句");
        await conversations.AskAsync(leaf, "再问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == middle).IsBusyBelow);
        var middleRow = list.Items.Single(i => i.Id == middle);
        middleRow.IsBusyBelow = false;
        await list.ArchiveRowCommand.ExecuteAsync(middleRow);
        window.UpdateLayout();
        Assert.Equal(UiText.ArchiveRefused([list.Items.Single(i => i.Id == leaf).Title]), list.RowNotice);
        Assert.True(Find<TextBlock>(window, "RowNotice").IsEffectivelyVisible);
        Assert.Empty(await conversations.ArchivedAmongAsync([root, middle, leaf, sibling], CancellationToken.None));
        held.Finish();
        await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(leaf));

        app.Services.GetRequiredService<StorageHealth>().Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));
        await UiWait.UntilAsync(() => list.IsStoragePaused);
        var siblingRow = list.Items.Single(i => i.Id == sibling);
        var title = siblingRow.Title;
        siblingRow.IsStoragePaused = false;
        await list.ArchiveRowCommand.ExecuteAsync(siblingRow);
        Assert.Equal(UiText.RowNotice(title, UiText.ArchiveStorageFailed), list.RowNotice);

        list.CloseRowNoticeCommand.Execute(null);
        list.BeginRenameRowCommand.Execute(siblingRow);
        siblingRow.RenameText = "存不下的名字";
        await list.CommitRenameRowCommand.ExecuteAsync(siblingRow);
        Assert.Equal(UiText.RowNotice(title, UiText.ArchiveStorageFailed), list.RowNotice);
        Assert.Equal(title, list.Items.Single(i => i.Id == sibling).Title);
        window.Close();
    }

    [AvaloniaFact]
    public async Task While_rows_are_selected_or_in_the_selection_mode_the_menu_is_not_offered()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (root, middle, _, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;

        ClickRow(window, root, Toggle);
        RightClickRow(window, middle);
        Assert.False(Row(window, middle).ContextMenu!.IsOpen);
        RightClickRow(window, root);
        Assert.False(Row(window, root).ContextMenu!.IsOpen);
        Assert.Equal([root], list.Selection.Select(i => i.Id));

        list.ClearSelection();
        list.ToggleSelectionModeCommand.Execute(null);
        RightClickRow(window, sibling);
        Assert.False(Row(window, sibling).ContextMenu!.IsOpen);

        // The keyboard too.
        Row(window, sibling).Focus(NavigationMethod.Tab);
        window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Row(window, sibling).ContextMenu!.IsOpen);

        list.ClearSelection();
        OpenMenu(window, sibling);
        CloseMenu(window, sibling);
        window.Close();
    }


    /// <summary>
    /// The rename box's own cut/copy/paste menu takes the focus while it is open; that is not leaving the box, so the
    /// rename goes on (to paste a name, say). Closing that menu by a click elsewhere leaves the box and saves.
    /// </summary>
    [AvaloniaFact]
    public async Task The_rename_boxs_own_menu_does_not_end_the_rename()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (_, middle, _, sibling) = await SeedAsync(app, window);
        var list = app.ShellPage.Conversations;
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var row = list.Items.Single(i => i.Id == middle);

        OpenMenu(window, middle);
        await ClickEntryAsync(window, "RowMenuRename");
        await UiWait.UntilAsync(() => RenameBox(window, middle) is { IsFocused: true, IsEffectivelyVisible: true });
        var box = RenameBox(window, middle);
        window.KeyTextInput("蒸汽");
        RightClick(window, box);
        Assert.True(box.ContextFlyout!.IsOpen);
        Assert.True(row.IsRenaming);
        Assert.True(box.IsEffectivelyVisible);

        // Esc closes that menu only; the focus comes back to the box and typing goes on.
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await UiWait.UntilAsync(() => !box.ContextFlyout!.IsOpen);
        Dispatcher.UIThread.RunJobs();
        Assert.True(row.IsRenaming);
        box.Focus();
        window.KeyTextInput("机车");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await list.RowAction;
        Assert.Equal("蒸汽机车", (await conversations.LoadAsync(middle, CancellationToken.None))!.Conversation.Title);

        // Opened again and dismissed by a click elsewhere: that click only closes the menu, as menus do; the next one
        // leaves the box and saves.
        OpenMenu(window, sibling);
        await ClickEntryAsync(window, "RowMenuRename");
        await UiWait.UntilAsync(() => RenameBox(window, sibling) is { IsFocused: true, IsEffectivelyVisible: true });
        var siblingBox = RenameBox(window, sibling);
        window.KeyTextInput("铁轨");
        RightClick(window, siblingBox);
        Assert.True(siblingBox.ContextFlyout!.IsOpen);
        UiInput.Click(window, Find<TextBox>(CurrentView(window), "Input"));
        await UiWait.UntilAsync(() => !siblingBox.ContextFlyout.IsOpen);
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.Items.Single(i => i.Id == sibling).IsRenaming);
        UiInput.Click(window, Find<TextBox>(CurrentView(window), "Input"));
        await UiWait.UntilAsync(() => !list.Items.Single(i => i.Id == sibling).IsRenaming);
        await list.RowAction;
        Assert.Equal("铁轨", (await conversations.LoadAsync(sibling, CancellationToken.None))!.Conversation.Title);
        window.Close();
    }

    /// <summary>The network seed, the sibling's page open and the sidebar laid out; returns root, middle, leaf, sibling.</summary>
    private static async Task<(ConversationId Root, ConversationId Middle, ConversationId Leaf, ConversationId Sibling)> SeedAsync(DesktopHarness app, MainWindow window)
    {
        await SummaryNetworkScenario.SeedAsync(app);
        var list = app.ShellPage.Conversations;
        await UiWait.UntilAsync(() => list.Items.Count == 4 && list.Items.All(i => i.Mark == ConversationMark.None || i.IsUnread));
        var ids = list.Items.Select(i => i.Id).ToList();
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(ids[3])));
        await ((ConversationViewModel)app.Workbench.CurrentPage!).Loaded;
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == ids[3]).IsSelected);
        window.UpdateLayout();
        return (ids[0], ids[1], ids[2], ids[3]);
    }

    private static Button Row(Window window, ConversationId id) =>
        Find<ItemsControl>(window, "ConversationList").GetVisualDescendants().OfType<Button>()
            .Single(b => b.DataContext is ConversationItemViewModel item && item.Id == id && b.Classes.Contains("nav"));

    private static TextBox RenameBox(Window window, ConversationId id) =>
        Find<ItemsControl>(window, "ConversationList").GetVisualDescendants().OfType<TextBox>()
            .Single(b => b.Name == "RowRenameBox" && b.DataContext is ConversationItemViewModel item && item.Id == id);

    /// <summary>The entries of the open menu (it is shown in the window's overlay).</summary>
    private static List<MenuItem> Entries(Window window) =>
        [.. window.GetVisualDescendants().OfType<MenuItem>().Where(m => m.Name?.StartsWith("RowMenu", StringComparison.Ordinal) == true && m.IsEffectivelyVisible)];

    private static MenuItem MenuEntry(Window window, string name) => Entries(window).Single(m => m.Name == name);

    /// <summary>
    /// Clicks an entry of the open menu once it is enabled. Work the seeding started (a summary, a title) can disable a
    /// writing entry for a moment, and a click then does nothing (a Linux CI runner was slow enough for that, 2026-11-21).
    /// </summary>
    private static async Task ClickEntryAsync(Window window, string name)
    {
        await UiWait.UntilAsync(() =>
        {
            window.UpdateLayout();
            return MenuEntry(window, name).IsEffectivelyEnabled;
        });
        UiInput.Click(window, MenuEntry(window, name));
    }

    private static void AssertDisabled(Window window, string name, string reason)
    {
        var entry = MenuEntry(window, name);
        Assert.False(entry.IsEffectivelyEnabled, name);
        Assert.Equal(reason, ToolTip.GetTip(entry));
        Assert.True(ToolTip.GetShowOnDisabled(entry));
    }

    /// <summary>A real right click on the row's title; returns its menu, open.</summary>
    private static ContextMenu OpenMenu(Window window, ConversationId id)
    {
        RightClickRow(window, id);
        var menu = Row(window, id).ContextMenu!;
        Assert.True(menu.IsOpen);
        window.UpdateLayout();
        return menu;
    }

    private static void CloseMenu(Window window, ConversationId id)
    {
        Row(window, id).ContextMenu!.Close();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void RightClickRow(Window window, ConversationId id)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var title = Find<TextBlock>(Row(window, id), "RowTitle");
        var point = title.TranslatePoint(new Point(4, title.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void RightClick(Window window, Control target)
    {
        var point = target.TranslatePoint(new Point(20, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void ClickRow(Window window, ConversationId id, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.UpdateLayout();
        var title = Find<TextBlock>(Row(window, id), "RowTitle");
        UiInput.Click(window, title, modifiers, new Point(4, title.Bounds.Height / 2));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
