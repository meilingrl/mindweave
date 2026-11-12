using Avalonia.Automation;
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
using ConversationId = Mindweave.Core.Conversations.ConversationId;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Restoring a backup from inside the application, and removing backups (2026-11-12): 设置 › 数据与存储 › 备份. The
/// restore asks the exit sequence's questions first (cancelling changes nothing), keeps the current data as a "恢复前"
/// backup, then shows the restored data with a notice; a failure keeps the current data. Deleting asks once, says the
/// space freed and what may be the only copy, and shows what could not be deleted.
/// </summary>
public class BackupRestoreUiTests
{
    private static readonly int Current = MigrationCatalog.Default.LatestVersion;

    [AvaloniaFact]
    public async Task Restoring_brings_back_the_backups_data_keeps_the_current_data_as_a_backup_and_says_so()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var seed = await SeedAsync(app);
        var oldRoot = app.Root;
        var oldDatabase = app.Services.GetRequiredService<SqliteDatabase>();
        var oldWorkbench = app.Workbench;
        var page = await OpenStorageAsync(app, window);
        var row = Assert.Single(page.Rows);
        Assert.Equal(seed.Backup.Path, row.Entry.Backup!.Path);
        Assert.Equal(UiText.BackupBeforeConversationDelete, row.Badge);

        UiInput.Click(window, Find<Button>(window, "RestoreBackupButton"));
        var dialog = app.Root.BackupRestore;
        Assert.True(dialog.IsOpen);
        window.UpdateLayout();
        Assert.True(Find<Control>(window, "BackupRestoreDialog").IsEffectivelyVisible);
        Assert.Equal(UiText.RestoreTitle(seed.Backup.CreatedAt), Find<TextBlock>(window, "BackupRestoreTitle").Text);
        Assert.Equal(
            [UiText.RestoreMadeAt(seed.Backup), UiText.RestoreAllWorkspaces, UiText.RestoreBringsBackDeleted, UiText.RestoreKeepsCurrent, UiText.RestoreStopsWork],
            dialog.Points);
        var cancel = Find<Button>(window, "BackupRestoreCancel");
        var confirm = Find<Button>(window, "BackupRestoreConfirm");
        Assert.True(cancel.IsDefault);
        Assert.True(cancel.IsCancel);
        Assert.Contains("primary", confirm.Classes);
        Assert.False(confirm.IsDefault);
        Dispatcher.UIThread.RunJobs();
        Assert.True(cancel.IsFocused);

        UiInput.Click(window, confirm);
        await UiWait.UntilAsync(() => !ReferenceEquals(app.Root, oldRoot));
        await app.Session.LastRestore;
        window.UpdateLayout();

        // The window shows the next session on the restored data, at the page a start would show.
        Assert.Same(app.Root, window.DataContext);
        Assert.IsType<ShellViewModel>(app.Root.Current);
        Assert.Equal(seed.Workspace, app.Workbench.CurrentWorkspace!.Id);
        Assert.Equal(Sorted([seed.Kept, seed.Deleted]), Sorted(await ConversationIdsAsync(app, seed.Workspace)));
        Assert.Equal(UiText.RestoredNotice(seed.Backup.CreatedAt), Find<TextBlock>(window, "RestoreNotice").Text);
        Assert.Contains("info", Find<Border>(window, "RestoreNoticePanel").Classes);
        Assert.Equal(UiText.CloseRestoreNotice, AutomationProperties.GetName(Find<Button>(window, "CloseRestoreNoticeButton")));

        // The data before the restore is a verified "恢复前" backup in the same list; the files it replaced are gone.
        var backups = app.Services.GetRequiredService<IBackupFiles>().ListBackups();
        var kept = Assert.Single(backups, b => b.Reason == BackupReason.BeforeRestore);
        Assert.Equal(BackupProblem.None, app.Services.GetRequiredService<IBackupFiles>().Check(kept));
        Assert.Contains(backups, b => b.Path == seed.Backup.Path);
        Assert.Empty(Directory.GetFiles(app.Paths.DataDirectory, "mindweave.db.replaced-*"));

        // New work runs in the restored data.
        var asked = await NetworkScenario.AskAsync(app, app.Services.GetRequiredService<ConversationService>(), null, seed.Workspace, "再问一个");
        Assert.Contains(asked, await ConversationIdsAsync(app, seed.Workspace));

        UiInput.Click(window, Find<Button>(window, "CloseRestoreNoticeButton"));
        Assert.Null(app.ShellPage.RestoreNotice);

        // The old session is over: its database is closed and its pages are gone; quitting goes through the new one.
        Assert.False(oldDatabase.IsOpen);
        Assert.Empty(oldWorkbench.OpenWorkspaces);
        Assert.True(ExitScenario.Quit(app, window, ExitEntry.Window));
        await UiWait.UntilAsync(() => app.Shell.ExitRequests == 1);
        Assert.Equal(ExitStage.Releasing, app.Root.Exit.Stage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cancelling_the_unsaved_layout_prompt_or_the_running_task_prompt_changes_nothing()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var network = await ExitScenario.DirtyLayoutAsync(app, window);
        await app.Services.GetRequiredService<IDatabaseBackup>().BackupAsync(BackupReason.BeforeWorkspaceDelete, CancellationToken.None);
        var running = await ExitScenario.RunningAnswerAsync(app);
        var oldRoot = app.Root;
        var backup = Assert.Single(app.Services.GetRequiredService<IBackupFiles>().ListBackups());

        // First the layout prompt, as on exit; cancelling it ends the restore with nothing done.
        app.Root.BackupRestore.Open(backup);
        app.Root.BackupRestore.RestoreCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Root.LayoutPrompt.IsOpen);
        window.UpdateLayout();
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        Assert.False(app.Root.Exit.IsTaskPromptOpen);
        Assert.True(Find<Control>(window, "LayoutPromptDialog").IsEffectivelyVisible);
        app.Root.LayoutPrompt.CancelCommand.Execute(null);
        AssertNothingChanged();
        Assert.True(network.IsDirty);

        // Then (layout not saved) the running-task prompt in the restore's words; cancelling it ends it too.
        app.Root.BackupRestore.RestoreCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Root.LayoutPrompt.IsOpen);
        app.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(app.Root.Exit.IsTaskPromptOpen);
        Assert.Equal(UiText.RestoreTaskTitle, app.Root.Exit.TaskTitle);
        Assert.Equal(UiText.RestoreWithUnfinished(1, 0), app.Root.Exit.TaskMessage);
        Assert.Equal(UiText.RestoreTaskConfirm, Find<Button>(window, "ExitConfirm").Content);
        Assert.Equal(UiText.Cancel, Find<Button>(window, "ExitCancel").Content);
        app.Root.Exit.KeepRunningCommand.Execute(null);
        AssertNothingChanged();
        Assert.True(network.IsDirty);
        Assert.True(app.Services.GetRequiredService<ConversationService>().Observe(running) is not null);

        // Afterwards quitting asks in the exit's own words again.
        Assert.False(app.Root.Exit.IsForRestore);
        Assert.Equal(UiText.ExitTitle, app.Root.Exit.TaskTitle);
        window.Close();

        void AssertNothingChanged()
        {
            Assert.Same(oldRoot, app.Root);
            Assert.Equal(ExitStage.Idle, app.Root.Exit.Stage);
            Assert.False(app.Root.BackupRestore.IsBusy);
            Assert.True(app.Root.BackupRestore.IsOpen);
            Assert.DoesNotContain(app.Services.GetRequiredService<IBackupFiles>().ListBackups(), b => b.Reason == BackupReason.BeforeRestore);
            Assert.Equal(1, app.Services.GetRequiredService<GenerationCoordinator>().UnfinishedAnswerCount);
            Assert.False(app.Services.GetRequiredService<GenerationCoordinator>().IsClosed);
        }
    }

    [AvaloniaFact]
    public async Task A_backup_from_a_newer_build_is_listed_but_cannot_be_restored_and_says_why()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        Directory.CreateDirectory(app.Paths.BackupDirectory);
        await File.WriteAllBytesAsync(Path.Combine(app.Paths.BackupDirectory, $"mindweave-v{Current + 1}-20261110T080000000Z.db"), new byte[2048], TestContext.Current.CancellationToken);
        var page = await OpenStorageAsync(app, window);

        var row = Assert.Single(page.Rows);
        Assert.False(row.CanRestore);
        var restore = Find<Button>(window, "RestoreBackupButton");
        Assert.False(restore.IsEffectivelyEnabled);
        Assert.Equal(UiText.BackupTooNew(Current + 1, Current), ToolTip.GetTip(restore));
        Assert.Equal(UiText.BackupTooNew(Current + 1, Current), AutomationProperties.GetHelpText(restore));
        Assert.True(ToolTip.GetShowOnDisabled(restore));
        page.RestoreCommand.Execute(row);
        Assert.False(app.Root.BackupRestore.IsOpen);

        // It can still be deleted.
        Assert.True(Find<Button>(window, "DeleteBackupButton").IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_bad_backup_is_refused_in_the_dialog_before_anything_is_asked_or_changed()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        Directory.CreateDirectory(app.Paths.BackupDirectory);
        await File.WriteAllTextAsync(Path.Combine(app.Paths.BackupDirectory, $"mindweave-v{Current}-20261110T080000000Z.db"), "not a database", TestContext.Current.CancellationToken);
        var oldRoot = app.Root;
        var page = await OpenStorageAsync(app, window);

        UiInput.Click(window, Find<Button>(window, "RestoreBackupButton"));
        UiInput.Click(window, Find<Button>(window, "BackupRestoreConfirm"));
        await app.Root.BackupRestore.Running;
        window.UpdateLayout();

        Assert.Equal(UiText.BackupProblemText(BackupProblem.Unreadable), app.Root.BackupRestore.Problem);
        Assert.True(Find<Control>(window, "BackupRestoreProblem").IsEffectivelyVisible);
        Assert.True(app.Root.BackupRestore.IsOpen);
        Assert.Same(oldRoot, app.Root);
        Assert.Equal(ExitStage.Idle, app.Root.Exit.Stage);
        Assert.Single(page.Rows);
        Assert.Empty(Directory.GetFiles(app.Paths.DataDirectory, "mindweave.db.replaced-*"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_restore_that_fails_while_swapping_reopens_the_current_data_and_says_so()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var seed = await SeedAsync(app);
        var oldRoot = app.Root;
        await OpenStorageAsync(app, window);

        // The next session's database fails to move the files (on Windows, a file held open elsewhere).
        app.Session.Replaced += (_, _) =>
            app.Services.GetRequiredService<SqliteDatabase>().BeforeSwapForTests = () => throw new IOException("simulated: the file is in use");
        UiInput.Click(window, Find<Button>(window, "RestoreBackupButton"));
        UiInput.Click(window, Find<Button>(window, "BackupRestoreConfirm"));
        await UiWait.UntilAsync(() => !ReferenceEquals(app.Root, oldRoot));
        await app.Session.LastRestore;
        window.UpdateLayout();

        Assert.IsType<ShellViewModel>(app.Root.Current);
        Assert.Equal(Sorted([seed.Kept, seed.Later]), Sorted(await ConversationIdsAsync(app, seed.Workspace)));
        Assert.Equal(UiText.RestoreFailedNotice(seed.Backup.CreatedAt, RestoreFailure.SwapFailed), Find<TextBlock>(window, "RestoreNotice").Text);
        Assert.Contains("error", Find<Border>(window, "RestoreNoticePanel").Classes);
        Assert.Empty(Directory.GetFiles(app.Paths.DataDirectory, "mindweave.db.replaced-*"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_one_backup_or_a_selection_asks_once_says_the_space_freed_and_what_may_be_lost()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        var upgrade = await FakeBackupAsync(app, "20261101T080000000Z", string.Empty, 400 * 1024);
        var beforeDelete = await FakeBackupAsync(app, "20261102T080000000Z", "-before-delete", 100 * 1024);
        var olderBeforeRestore = await FakeBackupAsync(app, "20261103T080000000Z", "-before-restore", 50 * 1024);
        var newestBeforeRestore = await FakeBackupAsync(app, "20261104T080000000Z", "-before-restore", 60 * 1024);
        var leftover = $"{app.Paths.DatabasePath}.replaced-20261104T080000000Z";
        await File.WriteAllBytesAsync(leftover, new byte[30 * 1024], TestContext.Current.CancellationToken);
        var page = await OpenStorageAsync(app, window);
        Assert.Equal(5, page.Rows.Count);
        Assert.Equal(UiText.BackupsTotal(4, 1, 640 * 1024), Find<TextBlock>(window, "BackupsTotal").Text);
        Assert.False(Find<Button>(window, "DeleteSelectedBackups").IsVisible);
        var dialog = app.Root.BackupDeletion;

        // One row: cancelling deletes nothing, then 删除 removes it and the list follows.
        var upgradeRow = page.Rows.Single(r => r.Entry.Backup?.Path == upgrade);
        page.DeleteCommand.Execute(upgradeRow);
        window.UpdateLayout();
        Assert.True(Find<Control>(window, "BackupDeleteDialog").IsEffectivelyVisible);
        Assert.Equal(UiText.DeleteBackupsTitle(1), dialog.Title);
        Assert.Equal(UiText.DeleteBackupsBody(1, 400 * 1024), Find<TextBlock>(window, "BackupDeleteBody").Text);
        Assert.Empty(dialog.Warnings);
        Assert.True(Find<Button>(window, "BackupDeleteCancel").IsDefault);
        Assert.Contains("danger", Find<Button>(window, "BackupDeleteConfirm").Classes);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<Button>(window, "BackupDeleteCancel").IsFocused);
        UiInput.Click(window, Find<Button>(window, "BackupDeleteCancel"));
        Assert.False(dialog.IsOpen);
        Assert.True(File.Exists(upgrade));
        page.DeleteCommand.Execute(page.Rows.Single(r => r.Entry.Backup?.Path == upgrade));
        UiInput.Click(window, Find<Button>(window, "BackupDeleteConfirm"));
        await dialog.Running;
        Assert.False(dialog.IsOpen);
        Assert.False(File.Exists(upgrade));
        Assert.Equal(4, page.Rows.Count);

        // A selection, checked with the keyboard: one confirmation naming the count, the size and what may be lost.
        foreach (var key in new[] { beforeDelete, newestBeforeRestore, leftover })
        {
            var index = page.Rows.ToList().FindIndex(r => r.Entry.Key == key);
            window.UpdateLayout();
            var check = window.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Name == "BackupCheck").ElementAt(index);
            check.Focus();
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.True(page.Rows[index].IsChecked, key);
        }

        window.UpdateLayout();
        var deleteSelected = Find<Button>(window, "DeleteSelectedBackups");
        Assert.True(deleteSelected.IsVisible);
        Assert.Equal(UiText.DeleteSelectedBackups(3, 190 * 1024), page.DeleteSelectedText);
        Assert.Equal(page.DeleteSelectedText, AutomationProperties.GetName(deleteSelected));
        UiInput.Click(window, deleteSelected);
        Assert.Equal(UiText.DeleteBackupsTitle(3), dialog.Title);
        Assert.Equal(UiText.DeleteBackupsBody(3, 190 * 1024), dialog.Body);
        Assert.Equal([UiText.DeleteOnlyCopyOfWorkspace, UiText.DeleteWayBackFromRestore, UiText.DeleteLeftoverFiles], dialog.Warnings);

        // A leftover from a restore on the startup page has no "恢复前" backup behind it: the warning says it may be the
        // only copy of the data from before that restore (review 2026-11-12).
        Assert.Contains("唯一的副本", UiText.DeleteLeftoverFiles, StringComparison.Ordinal);
        UiInput.Click(window, Find<Button>(window, "BackupDeleteConfirm"));
        await dialog.Running;
        Assert.False(dialog.IsOpen);
        Assert.False(File.Exists(beforeDelete));
        Assert.False(File.Exists(newestBeforeRestore));
        Assert.False(File.Exists(leftover));
        Assert.Equal(olderBeforeRestore, Assert.Single(page.Rows).Entry.Key);
        Assert.False(page.HasSelection);

        // The older "恢复前" backup is no longer the newest one's way back: deleting it now warns about exactly that.
        page.DeleteCommand.Execute(page.Rows[0]);
        Assert.Equal([UiText.DeleteWayBackFromRestore], dialog.Warnings);
        dialog.CancelCommand.Execute(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_delete_the_file_system_refuses_is_said_and_the_file_stays_listed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a read-only directory, as POSIX permissions make one.");
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        var path = await FakeBackupAsync(app, "20261101T080000000Z", "-before-conversation-delete", 4096);
        var page = await OpenStorageAsync(app, window);
        var dialog = app.Root.BackupDeletion;
        page.DeleteCommand.Execute(page.Rows[0]);
        Assert.Equal([UiText.DeleteOnlyCopyOfConversations], dialog.Warnings);

        MakeReadOnly(app.Paths.BackupDirectory, true);
        try
        {
            UiInput.Click(window, Find<Button>(window, "BackupDeleteConfirm"));
            await dialog.Running;
            window.UpdateLayout();
            Assert.True(dialog.IsOpen);
            Assert.Equal(UiText.BackupsDeleteFailed(1), dialog.Problem);
            Assert.True(Find<Control>(window, "BackupDeleteProblem").IsEffectivelyVisible);
            Assert.True(File.Exists(path));
            Assert.Single(page.Rows);
        }
        finally
        {
            MakeReadOnly(app.Paths.BackupDirectory, false);
        }

        // Once the file can go, the same dialog deletes it.
        UiInput.Click(window, Find<Button>(window, "BackupDeleteConfirm"));
        await dialog.Running;
        Assert.False(dialog.IsOpen);
        Assert.Empty(page.Rows);
        Assert.True(Find<Control>(window, "NoBackups").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_backup_rows_work_from_the_keyboard_and_name_themselves()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await FakeBackupAsync(app, "20261101T080000000Z", "-before-restore", 4096);
        var page = await OpenStorageAsync(app, window);
        var row = Assert.Single(page.Rows);
        Assert.Equal(UiText.BackupBeforeRestore, row.Badge);
        Assert.Contains("accent", Find<Border>(window, "BackupBadge").Classes);

        var check = Find<ToggleButton>(window, "BackupCheck");
        var restore = Find<Button>(window, "RestoreBackupButton");
        var delete = Find<Button>(window, "DeleteBackupButton");
        Assert.Equal(row.SelectName, AutomationProperties.GetName(check));

        // Announced as a check box with its state, not as a toggle button (review 2026-11-12).
        var checkPeer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(check);
        Assert.Equal(Avalonia.Automation.Peers.AutomationControlType.CheckBox, checkPeer.GetAutomationControlType());
        Assert.IsAssignableFrom<Avalonia.Automation.Provider.IToggleProvider>(checkPeer);
        Assert.Equal(row.RestoreName, AutomationProperties.GetName(restore));
        Assert.Equal(row.DeleteName, AutomationProperties.GetName(delete));
        Assert.Contains(UiText.BackupBeforeRestore, row.RestoreName, StringComparison.Ordinal);
        Assert.True(check.Focusable && restore.Focusable && delete.Focusable);

        // Tab moves from the box to 恢复… to 删除…; Enter opens the dialog with 取消 focused, Esc closes it.
        check.Focus();
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(restore.IsFocused);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(delete.IsFocused);
        restore.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(app.Root.BackupRestore.IsOpen);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<Button>(window, "BackupRestoreCancel").IsFocused);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(app.Root.BackupRestore.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_list_is_read_again_when_the_page_is_shown_again()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        var page = await OpenStorageAsync(app, window);
        Assert.Empty(page.Rows);
        Assert.True(Find<Control>(window, "NoBackups").IsEffectivelyVisible);

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        window.UpdateLayout();
        await app.Services.GetRequiredService<IDatabaseBackup>().BackupAsync(BackupReason.BeforeConversationDelete, CancellationToken.None);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();

        Assert.Same(page, app.Workbench.CurrentPage);
        Assert.Single(page.Rows);
        Assert.False(Find<Control>(window, "NoBackups").IsEffectivelyVisible);
        window.Close();
    }

    internal sealed record Seed(WorkspaceId Workspace, ConversationId Kept, ConversationId Deleted, ConversationId Later, BackupInfo Backup);

    /// <summary>
    /// Workspace "火车" with two conversations; a backup made before one of them is deleted (as a cascade delete makes);
    /// then the delete, and a third conversation started after it.
    /// </summary>
    internal static async Task<Seed> SeedAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var kept = await NetworkScenario.AskAsync(app, conversations, null, workspace, "火车是怎么发明的？");
        var deleted = await NetworkScenario.AskAsync(app, conversations, null, workspace, "铁轨用什么做？");
        var path = await app.Services.GetRequiredService<IDatabaseBackup>().BackupAsync(BackupReason.BeforeConversationDelete, CancellationToken.None);
        var deletion = app.Services.GetRequiredService<IConversationDeletion>();
        Assert.True((await deletion.DeleteAsync(await deletion.InspectAsync([deleted], CancellationToken.None), CancellationToken.None)).IsDeleted);
        var later = await NetworkScenario.AskAsync(app, conversations, null, workspace, "高铁为什么快？");
        var backup = Assert.Single(app.Services.GetRequiredService<IBackupFiles>().ListBackups(), b => b.Path == path);
        return new Seed(workspace, kept, deleted, later, backup);
    }

    internal static async Task<StorageDetailsViewModel> OpenStorageAsync(DesktopHarness app, MainWindow window)
    {
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();
        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).ShowStorageDetailsCommand.Execute(null);
        var page = Assert.IsType<StorageDetailsViewModel>(app.Workbench.CurrentPage);
        window.UpdateLayout();
        await Task.Yield();
        return page;
    }

    private static async Task<string> FakeBackupAsync(DesktopHarness app, string stamp, string suffix, int bytes)
    {
        Directory.CreateDirectory(app.Paths.BackupDirectory);
        var path = Path.Combine(app.Paths.BackupDirectory, $"mindweave-v{Current}-{stamp}{suffix}.db");
        await File.WriteAllBytesAsync(path, new byte[bytes], TestContext.Current.CancellationToken);
        return path;
    }

    private static List<string> Sorted(IEnumerable<ConversationId> ids) => [.. ids.Select(i => i.ToString()).Order(StringComparer.Ordinal)];

    private static async Task<List<ConversationId>> ConversationIdsAsync(DesktopHarness app, WorkspaceId workspace) =>
        [.. (await app.Services.GetRequiredService<IConversations>().ListAsync(workspace, CancellationToken.None)).Select(c => c.Conversation.Id)];

    private static void MakeReadOnly(string directory, bool readOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(directory, readOnly
            ? UnixFileMode.UserRead | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
