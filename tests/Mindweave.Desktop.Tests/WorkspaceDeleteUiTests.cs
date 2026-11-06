using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Network;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>Deleting a whole workspace from the window (S7 round 4): typed confirmation, refusals, and where it lands.</summary>
public class WorkspaceDeleteUiTests
{
    [AvaloniaFact]
    public async Task Only_the_exact_name_enables_delete_and_cancel_is_the_default()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await NetworkUiTests.SeedAsync(app);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        window.UpdateLayout();
        Assert.Equal("火车", Find<TextBlock>(window, "SettingsWorkspaceName").Text);

        settings.DeleteWorkspaceCommand.Execute(null);
        var dialog = app.Root.WorkspaceDeletion;
        await dialog.Loading;
        window.UpdateLayout();

        Assert.True(Find<Control>(window, "WorkspaceDeleteDialog").IsEffectivelyVisible);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(Find<TextBox>(window, "WorkspaceDeleteName").IsFocused);
        Assert.Equal(UiText.DeleteWorkspaceTitle("火车"), dialog.Title);
        Assert.Equal(UiText.DeleteWorkspaceContents(4, 3), Find<TextBlock>(window, "WorkspaceDeleteContents").Text);
        Assert.Null(dialog.Problem);
        var confirm = Find<Button>(window, "WorkspaceDeleteConfirm");
        var cancel = Find<Button>(window, "WorkspaceDeleteCancel");
        Assert.True(cancel.IsDefault);
        Assert.Contains("danger", confirm.Classes);
        foreach (var typed in new[] { string.Empty, "火", "火车 ", " 火车", "火 车" })
        {
            Find<TextBox>(window, "WorkspaceDeleteName").Text = typed;
            window.UpdateLayout();
            Assert.False(confirm.IsEffectivelyEnabled, $"enabled for '{typed}'");
        }

        Find<TextBox>(window, "WorkspaceDeleteName").Text = "火车";
        window.UpdateLayout();
        Assert.True(confirm.IsEffectivelyEnabled);

        cancel.Command!.Execute(null);
        Assert.False(dialog.IsOpen);
        Assert.Single(app.Workbench.Workspaces);

        // The same dialog from the workspace picker's context menu, in the danger colour.
        var picker = Find<ComboBox>(window, "WorkspacePicker");
        picker.ContextMenu!.Open(picker);
        var item = Assert.IsType<MenuItem>(picker.ContextMenu.Items.Single());
        Assert.Equal(UiText.DeleteWorkspace, item.Header);
        Assert.Contains("danger", item.Classes);
        item.Command!.Execute(null);
        picker.ContextMenu.Close();
        Assert.True(dialog.IsOpen);
        Assert.Equal(string.Empty, dialog.TypedName);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_the_current_workspace_opens_the_most_recent_one_and_drops_its_pages_and_unsaved_layout_unasked()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await NetworkUiTests.SeedAsync(app);
        var train = app.Workbench.CurrentWorkspace!;
        var workspaces = app.Services.GetRequiredService<WorkspaceService>();
        var older = await workspaces.CreateAsync(WorkspaceName.FromStored("摄影史"), CancellationToken.None);
        var recent = await workspaces.CreateAsync(WorkspaceName.FromStored("天文"), CancellationToken.None);
        app.Workbench.Workspaces.Add(older);
        app.Workbench.Workspaces.Add(recent);
        app.ShellPage.SelectedWorkspace = older;
        app.ShellPage.SelectedWorkspace = recent;
        app.ShellPage.SelectedWorkspace = train;
        await app.Workbench.LastLocationSave;

        // A conversation page in the back history, and an arrangement that only an explicit save would keep.
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 4);
        var root = app.ShellPage.Conversations.Items[0];
        app.ShellPage.Conversations.OpenCommand.Execute(root);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        var network = await NetworkUiTests.OpenNetworkAsync(app, window);
        await NetworkUiTests.MoveSomethingAsync(app, network);
        network.ArrangeCommand.Execute(null);
        Assert.True(network.IsDirty);

        app.ShellPage.DeleteWorkspaceCommand.Execute(null);
        var dialog = app.Root.WorkspaceDeletion;
        Assert.Equal(train, dialog.Target);
        await dialog.Loading;
        dialog.TypedName = "火车";
        await dialog.DeleteCommand.ExecuteAsync(null);

        Assert.False(dialog.IsOpen);
        Assert.False(app.Root.LayoutPrompt.IsOpen);
        Assert.Empty(app.Services.GetRequiredService<NetworkSessions>().Dirty);
        Assert.Equal(recent.Id, app.Workbench.CurrentWorkspace!.Id);
        Assert.Equal(MainSection.Explore, app.Workbench.CurrentSection);
        Assert.IsType<ExploreHomeViewModel>(app.Workbench.CurrentPage);
        Assert.False(app.Workbench.CanGoBack);
        Assert.DoesNotContain(train.Id, app.Workbench.OpenWorkspaces);
        Assert.Equal([older.Id, recent.Id], app.Workbench.Workspaces.Select(w => w.Id));
        Assert.True(page.IsDisposed);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 0);
        Assert.Single(app.Services.GetRequiredService<Application.Startup.IDatabaseLifecycle>().ListBackups());

        // Quitting afterwards asks nothing about the deleted workspace's layout.
        Assert.False(app.Root.LayoutPrompt.HoldExit(_ => { }, () => { }));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_the_last_workspace_returns_to_the_first_workspace_flow()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await app.StartWithWorkspaceAsync("唯一");
        app.ShellPage.DeleteWorkspaceCommand.Execute(null);
        var dialog = app.Root.WorkspaceDeletion;
        await dialog.Loading;
        Assert.Equal(UiText.DeleteWorkspaceContents(0, 0), dialog.Contents);
        dialog.TypedName = "唯一";
        await dialog.DeleteCommand.ExecuteAsync(null);

        var onboarding = Assert.IsType<OnboardingViewModel>(app.Root.Current);
        Assert.Empty(app.Workbench.Workspaces);
        Assert.Null(app.Workbench.CurrentWorkspace);
        onboarding.Form.Name = "重新开始";
        await onboarding.Form.SubmitCommand.ExecuteAsync(null);
        Assert.IsType<ShellViewModel>(app.Root.Current);
        Assert.Equal("重新开始", app.Workbench.CurrentWorkspace!.Name.Value);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Running_work_is_named_and_nothing_is_deleted()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var held = app.Client.Hold("半句");
        var sent = await conversations.StartAsync(app.Workbench.CurrentWorkspace!.Id, "火车是怎么发明的？", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        app.ShellPage.DeleteWorkspaceCommand.Execute(null);
        var dialog = app.Root.WorkspaceDeletion;
        await dialog.Loading;
        var expected = UiText.DeleteWorkspaceBusy([("火车是怎么发明的？", true, false)]);
        Assert.Equal(expected, dialog.Problem);
        window.UpdateLayout();
        Assert.True(Find<Control>(window, "WorkspaceDeleteProblem").IsEffectivelyVisible);

        // The exact name does not enable 删除 while the notice says why it cannot work.
        dialog.TypedName = "火车";
        Assert.True(dialog.HasRunningWork);
        Assert.False(dialog.CanDelete);
        Assert.False(dialog.DeleteCommand.CanExecute(null));
        Assert.Single(app.Workbench.Workspaces);
        Assert.Empty(app.Services.GetRequiredService<Application.Startup.IDatabaseLifecycle>().ListBackups());

        // The answer finishing clears the notice and enables 删除 without another press.
        held.Finish();
        await UiWait.UntilAsync(() => dialog.Problem is null && dialog.CanDelete);
        Assert.False(dialog.HasRunningWork);
        window.UpdateLayout();
        Assert.False(Find<Control>(window, "WorkspaceDeleteProblem").IsEffectivelyVisible);
        Assert.True(Find<Button>(window, "WorkspaceDeleteConfirm").IsEffectivelyEnabled);

        // Work that starts with the dialog open brings the notice back.
        var again = app.Client.Hold("又半句");
        _ = await conversations.AskAsync(sent.Conversation, "再问一句", CancellationToken.None);
        await again.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => dialog.HasRunningWork);
        Assert.EndsWith("（回答）。", dialog.Problem, StringComparison.Ordinal); // titled automatically by now
        Assert.False(dialog.CanDelete);
        again.Finish();
        await UiWait.UntilAsync(() => dialog.CanDelete);
        await UiWait.UntilAsync(() => conversations.LoadAsync(sent.Conversation, CancellationToken.None).Result!.Messages.All(m => m.IsFinished));

        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.False(dialog.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_backup_or_paused_storage_is_said_in_the_dialog_and_nothing_is_deleted()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.DeleteWorkspaceCommand.Execute(null);
        var dialog = app.Root.WorkspaceDeletion;
        await dialog.Loading;
        dialog.TypedName = "火车";

        await File.WriteAllTextAsync(app.Paths.BackupDirectory, "not a directory");
        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.True(dialog.IsOpen);
        Assert.Equal(UiText.WorkspaceDeleteBackupFailed, dialog.Problem);
        Assert.Single(app.Workbench.Workspaces);
        File.Delete(app.Paths.BackupDirectory);

        app.Services.GetRequiredService<StorageHealth>().Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));
        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.True(dialog.IsOpen);
        Assert.Equal(UiText.ArchiveStorageFailed, dialog.Problem);
        Assert.Single(app.Workbench.Workspaces);
        Assert.True((await app.Services.GetRequiredService<IWorkspaceDeletion>().InspectAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None)).Exists);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_in_the_name_box_keeps_the_typed_name_and_deletes_only_once_it_matches()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.DeleteWorkspaceCommand.Execute(null);
        var dialog = app.Root.WorkspaceDeletion;
        await dialog.Loading;
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var box = Find<TextBox>(window, "WorkspaceDeleteName");
        Assert.True(box.IsFocused);

        box.Text = "火";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(dialog.IsOpen);
        Assert.Equal("火", dialog.TypedName);
        Assert.Single(app.Workbench.Workspaces);

        box.Text = "火车";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await dialog.Deleting;
        Assert.False(dialog.IsOpen);
        Assert.Empty(app.Workbench.Workspaces);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_unreadable_count_is_said_and_delete_stays_disabled()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        var requests = new WorkspaceDeletionRequests();
        var dialog = new WorkspaceDeletionViewModel(
            new UnreadableDeletion(),
            app.Services.GetRequiredService<IConversations>(),
            app.Workbench,
            app.Services.GetRequiredService<NetworkSessions>(),
            requests,
            app.Services.GetRequiredService<Services.IUiDispatcher>());

        requests.Open(app.Workbench.CurrentWorkspace!);
        await dialog.Loading;
        dialog.TypedName = "火车";

        Assert.True(dialog.IsOpen);
        Assert.Equal(UiText.WorkspaceDeleteCountFailed, dialog.Problem);
        Assert.False(dialog.CanDelete);
        Assert.True(dialog.CancelCommand.CanExecute(null));
    }

    private sealed class UnreadableDeletion : IWorkspaceDeletion
    {
        public Task<WorkspaceContents> InspectAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
            throw new IOException("unreadable");

        public Task<WorkspaceDeleteOutcome> DeleteAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not expected");
    }
}
