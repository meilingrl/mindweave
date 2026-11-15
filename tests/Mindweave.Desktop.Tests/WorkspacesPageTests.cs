using Avalonia.Automation;
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
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// 设置 › 工作区 (2026-11-10): every workspace in one place, with what it holds, switching, renaming in place and
/// deleting any of them through the existing confirmation.
/// </summary>
public class WorkspacesPageTests
{
    [AvaloniaFact]
    public async Task The_settings_entry_counts_the_workspaces_and_the_page_lists_each_with_what_it_holds()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var (city, photos) = await WorkspaceSwitcherTests.AddAsync(app, "城市交通", "摄影史");
        app.Client.Reply("1863 年，伦敦。");
        await SummaryNetworkScenario.AskAsync(app, app.Services.GetRequiredService<ConversationService>(), null, city.Id, "第一条地铁在哪里？");

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        window.UpdateLayout();
        var entry = Find<Button>(window, "ManageWorkspacesButton");
        Assert.Equal(UiText.WorkspacesCount(3), Find<TextBlock>(window, "WorkspaceCountText").Text);
        Assert.Equal(UiText.ManageWorkspacesName(3), AutomationProperties.GetName(entry));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), c => c.Name is "SettingsWorkspaceName" or "DeleteWorkspaceButton");

        // A workspace added or deleted while the settings are shown: the count follows at once.
        var extra = await app.Services.GetRequiredService<WorkspaceService>().CreateAsync(WorkspaceName.FromStored("临时"), CancellationToken.None);
        app.Workbench.Workspaces.Add(extra);
        window.UpdateLayout();
        Assert.Equal(UiText.WorkspacesCount(4), Find<TextBlock>(window, "WorkspaceCountText").Text);
        Assert.Equal(UiText.ManageWorkspacesName(4), AutomationProperties.GetName(entry));
        app.Workbench.RemoveWorkspace(extra.Id, null);
        window.UpdateLayout();
        Assert.Equal(UiText.WorkspacesCount(3), Find<TextBlock>(window, "WorkspaceCountText").Text);

        UiInput.Click(window, entry);
        var page = Assert.IsType<WorkspacesViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        window.UpdateLayout();
        Assert.Equal(UiText.WorkspacesTitle, app.ShellPage.Title);
        Assert.Equal(["火车", "城市交通", "摄影史"], Texts(window, "WorkspaceRowName"));
        Assert.Equal([UiText.WorkspaceCounts(4, 3), UiText.WorkspaceCounts(1, 0), UiText.WorkspaceCounts(0, 0)], Texts(window, "WorkspaceRowCounts"));

        // 当前 on the open one only; it offers no 切换; every row can be renamed and deleted.
        Assert.Equal([true, false, false], Visible(window, "CurrentWorkspaceTag"));
        Assert.Equal([false, true, true], Visible(window, "SwitchWorkspaceButton"));
        Assert.Equal([true, true, true], Visible(window, "RenameWorkspaceButton"));
        Assert.Equal([true, true, true], Visible(window, "DeleteWorkspaceButton"));
        Assert.Equal(UiText.WorkspaceRowName("火车", true, UiText.WorkspaceCounts(4, 3)), AutomationProperties.GetName(Rows(window, "WorkspaceRow")[0]));
        Assert.Equal(UiText.SwitchToWorkspaceName("城市交通"), AutomationProperties.GetName(Rows<Button>(window, "SwitchWorkspaceButton")[1]));
        Assert.Equal(UiText.RenameWorkspaceName("摄影史"), AutomationProperties.GetName(Rows<Button>(window, "RenameWorkspaceButton")[2]));
        Assert.Equal(UiText.DeleteWorkspaceName("摄影史"), AutomationProperties.GetName(Rows<Button>(window, "DeleteWorkspaceButton")[2]));

        // A workspace added elsewhere shows up here, and in the settings entry's count.
        var flights = await app.Services.GetRequiredService<WorkspaceService>().CreateAsync(WorkspaceName.FromStored("航空史"), CancellationToken.None);
        app.Workbench.Workspaces.Add(flights);
        await page.Loaded;
        window.UpdateLayout();
        Assert.Equal(["火车", "城市交通", "摄影史", "航空史"], Texts(window, "WorkspaceRowName"));
        Assert.Equal(UiText.WorkspaceCounts(0, 0), page.Rows[3].CountsText);
        Assert.Equal(UiText.WorkspacesCount(4), settings.WorkspaceCountText);
        _ = photos;
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_row_says_how_many_of_its_conversations_are_archived()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var (city, _) = await WorkspaceSwitcherTests.AddAsync(app, "城市交通", "摄影史");
        var conversations = app.Services.GetRequiredService<ConversationService>();
        app.Client.Reply("1863 年，伦敦。");
        await SummaryNetworkScenario.AskAsync(app, conversations, null, city.Id, "第一条地铁在哪里？");
        var subway = Assert.Single(await conversations.ListAsync(city.Id, CancellationToken.None));
        Assert.True((await conversations.ArchiveAsync(subway.Conversation.Id, CancellationToken.None)).Archived);

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();
        UiInput.Click(window, Find<Button>(window, "ManageWorkspacesButton"));
        var page = Assert.IsType<WorkspacesViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        window.UpdateLayout();

        // 2026-11-10: the count says how many are archived, and says nothing of archiving where none is.
        Assert.Equal("1 个会话，包含 1 个归档对话 · 0 个总结", Texts(window, "WorkspaceRowCounts")[1]);
        Assert.Equal(UiText.WorkspaceCounts(4, 3), Texts(window, "WorkspaceRowCounts")[0]);
        Assert.Equal("4 个会话 · 3 个总结", UiText.WorkspaceCounts(4, 3));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Switching_from_the_page_opens_that_workspace_and_asks_about_an_unsaved_layout_first()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var trains = app.Workbench.CurrentWorkspace!;
        var (city, _) = await WorkspaceSwitcherTests.AddAsync(app, "城市交通", "摄影史");
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await SummaryNetworkScenario.MoveSomethingAsync(app, network);
        network.ArrangeCommand.Execute(null);

        var page = await OpenPageAsync(app, window);
        UiInput.Click(window, Rows<Button>(window, "SwitchWorkspaceButton")[1]);
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        Assert.Equal(trains.Id, app.Workbench.CurrentWorkspace!.Id);
        app.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        Assert.Equal(city.Id, app.Workbench.CurrentWorkspace!.Id);
        Assert.True(page.Rows[1].IsCurrent);
        Assert.False(page.Rows[0].IsCurrent);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Renaming_in_place_saves_on_enter_and_the_new_name_shows_at_once_everywhere()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var trains = await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var (city, _) = await WorkspaceSwitcherTests.AddAsync(app, "城市交通", "摄影史");

        // News in 城市交通 (open: 火车), so its hint and the switcher's tooltip name it.
        var news = app.ShellPage.WorkspaceNews!;
        app.Client.Reply("1863 年，伦敦。");
        await app.Services.GetRequiredService<ConversationService>().StartAsync(city.Id, "第一条地铁在哪里？", CancellationToken.None);
        await UiWait.UntilAsync(() => news.HasNews && news.Toasts.Count == 1);
        Assert.Equal(UiText.OtherWorkspacesNews(["城市交通"]), news.Hint);

        var page = await OpenPageAsync(app, window);
        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[1]);
        var row = page.Rows[1];
        Assert.True(row.IsRenaming);
        var box = Rows<TextBox>(window, "WorkspaceRenameBox")[1];
        await UiWait.UntilAsync(() => box.IsFocused);
        Assert.Equal("城市交通", box.Text);
        Assert.Equal("城市交通", box.SelectedText);
        Assert.False(Rows<Control>(window, "WorkspaceRowActions")[1].IsEffectivelyVisible);
        Assert.True(Rows<Control>(window, "WorkspaceRenameActions")[1].IsEffectivelyVisible);

        // The row keeps its height while the name is a box, so nothing below it jumps.
        var rowView = Rows(window, "WorkspaceRow")[1];
        var editing = rowView.Bounds.Height;
        row.CancelRenameCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal(editing, rowView.Bounds.Height, 0.5);
        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[1]);
        await UiWait.UntilAsync(() => box.IsFocused);

        window.KeyTextInput("  城市   轨道交通 ");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await page.Renaming;
        Assert.False(row.IsRenaming);
        Assert.Equal("城市 轨道交通", row.Name);
        Assert.Equal("城市 轨道交通", (await app.Services.GetRequiredService<WorkspaceService>().ListAsync(CancellationToken.None)).Single(w => w.Id == city.Id).Name.Value);
        Assert.Equal("城市 轨道交通", app.Workbench.Workspaces.Single(w => w.Id == city.Id).Name.Value);
        await UiWait.UntilAsync(() => Rows<Button>(window, "RenameWorkspaceButton")[1].IsFocused);

        // The hint, the toast and the switcher's menu follow; renaming the open workspace renames the switcher and its home page.
        await UiWait.UntilAsync(() => news.Hint == UiText.OtherWorkspacesNews(["城市 轨道交通"]) && news.Toasts[0].Name == "城市 轨道交通");
        Assert.Equal(UiText.OtherWorkspacesNews(["城市 轨道交通"]), ToolTip.GetTip(Part<Button>(window, "WorkspacePicker")));
        page.Rows[0].BeginRenameCommand.Execute(null);
        page.Rows[0].RenameText = "蒸汽时代";
        await page.Rows[0].CommitRenameCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.Equal("蒸汽时代", Part<TextBlock>(window, "WorkspacePickerName").Text);
        Assert.Equal(UiText.WorkspaceSwitcherName("蒸汽时代"), AutomationProperties.GetName(Part<Button>(window, "WorkspacePicker")));
        Assert.Equal("蒸汽时代", app.Workbench.CurrentWorkspace!.Name.Value);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        var home = Assert.IsType<ExploreHomeViewModel>(app.Workbench.CurrentPage);
        Assert.Equal("蒸汽时代", home.Title);
        Assert.Equal("蒸汽时代", app.ShellPage.Title);
        Assert.Equal(trains.Id, home.Workspace.Id);
        app.ShellPage.IsWorkspaceMenuOpen = true;
        window.UpdateLayout();
        Assert.Equal(["蒸汽时代", "城市 轨道交通", "摄影史"], app.ShellPage.WorkspaceChoices.Select(c => c.Workspace.Name.Value));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_cancels_a_rename_and_an_unchanged_name_writes_nothing()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];

        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[0]);
        var box = Rows<TextBox>(window, "WorkspaceRenameBox")[0];
        await UiWait.UntilAsync(() => box.IsFocused);
        window.KeyTextInput("别的名字");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(row.IsRenaming);
        Assert.Equal("火车", row.Name);
        Assert.Equal("火车", app.Workbench.CurrentWorkspace!.Name.Value);
        await UiWait.UntilAsync(() => Rows<Button>(window, "RenameWorkspaceButton")[0].IsFocused);

        // Starting again shows the stored name, not what was typed; the same name (after normalizing) just closes the box.
        row.BeginRenameCommand.Execute(null);
        Assert.Equal("火车", row.RenameText);
        row.RenameText = " 火车 ";
        var before = app.Workbench.Workspaces[0];
        await row.CommitRenameCommand.ExecuteAsync(null);
        Assert.False(row.IsRenaming);
        Assert.Same(before, app.Workbench.Workspaces[0]);

        // One row at a time: renaming another gives up the first.
        var (_, other) = await WorkspaceSwitcherTests.AddAsync(app, "城市交通", "摄影史");
        page.Rows[0].BeginRenameCommand.Execute(null);
        page.Rows[2].BeginRenameCommand.Execute(null);
        Assert.Equal([false, false, true], page.Rows.Select(r => r.IsRenaming));
        _ = other;
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_invalid_name_is_said_under_the_box_and_kept_and_a_name_in_use_is_accepted_as_when_creating()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await WorkspaceSwitcherTests.AddAsync(app, "城市交通", "摄影史");
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[1];
        var service = app.Services.GetRequiredService<WorkspaceService>();

        row.BeginRenameCommand.Execute(null);
        foreach (var (typed, error) in new[]
        {
            ("   ", WorkspaceNameError.Empty),
            (new string('长', WorkspaceName.MaxLength + 1), WorkspaceNameError.TooLong),
        })
        {
            row.RenameText = typed;
            await row.CommitRenameCommand.ExecuteAsync(null);
            Assert.True(row.IsRenaming);
            Assert.Equal(typed, row.RenameText);
            Assert.Equal(UiText.WorkspaceNameError(error), row.RenameError);
            window.UpdateLayout();
            Assert.True(Rows<TextBlock>(window, "WorkspaceRenameError")[1].IsEffectivelyVisible);
            Assert.Equal("城市交通", (await service.ListAsync(CancellationToken.None))[1].Name.Value);
        }

        // Creating does not refuse a name in use, so renaming does not either.
        row.RenameText = "火车";
        await row.CommitRenameCommand.ExecuteAsync(null);
        Assert.False(row.IsRenaming);
        Assert.Null(row.RenameError);
        Assert.Equal(["火车", "火车", "摄影史"], (await service.ListAsync(CancellationToken.None)).Select(w => w.Name.Value));
        window.Close();
    }

    [AvaloniaFact]
    public async Task While_storage_is_paused_a_rename_is_refused_and_said_and_the_box_keeps_the_name()
    {
        await using var app = DesktopHarness.Create(s => s.AddSingleton(new StorageHealthOptions { RetryInterval = TimeSpan.FromHours(1) }));
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        app.Services.GetRequiredService<StorageHealth>().Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));

        row.BeginRenameCommand.Execute(null);
        row.RenameText = "蒸汽时代";
        await row.CommitRenameCommand.ExecuteAsync(null);

        Assert.True(row.IsRenaming);
        Assert.Equal("蒸汽时代", row.RenameText);
        Assert.Equal(UiText.WorkspaceRenameStorageFailed, row.RenameError);
        Assert.Equal("火车", row.Name);
        Assert.Equal("火车", app.Workbench.CurrentWorkspace!.Name.Value);
        Assert.Equal("火车", (await app.Services.GetRequiredService<WorkspaceService>().ListAsync(CancellationToken.None)).Single().Name.Value);
        window.UpdateLayout();
        Assert.True(Rows<TextBlock>(window, "WorkspaceRenameError")[0].IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_another_workspace_from_its_row_backs_up_first_and_leaves_the_open_one_and_the_rest_alone()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var trains = app.Workbench.CurrentWorkspace!;
        var (city, photos) = await WorkspaceSwitcherTests.AddAsync(app, "城市交通", "摄影史");
        var conversations = app.Services.GetRequiredService<ConversationService>();
        await SummaryNetworkScenario.AskAsync(app, conversations, null, city.Id, "第一条地铁在哪里？");
        await SummaryNetworkScenario.AskAsync(app, conversations, null, photos.Id, "银版照相是怎么回事？");
        var page = await OpenPageAsync(app, window);

        UiInput.Click(window, Rows<Button>(window, "DeleteWorkspaceButton")[1]);
        var dialog = app.Root.WorkspaceDeletion;
        Assert.Equal(city.Id, dialog.Target!.Id);
        await dialog.Loading;
        Assert.Equal(UiText.DeleteWorkspaceTitle("城市交通"), dialog.Title);
        Assert.Equal(UiText.DeleteWorkspaceContents(1, 0), dialog.Contents);
        dialog.TypedName = "火车";
        Assert.False(dialog.CanDelete);
        dialog.TypedName = "城市交通";
        await dialog.DeleteCommand.ExecuteAsync(null);

        Assert.False(dialog.IsOpen);
        Assert.Equal(trains.Id, app.Workbench.CurrentWorkspace!.Id);
        Assert.Same(page, app.Workbench.CurrentPage);
        Assert.Equal(["火车", "摄影史"], page.Rows.Select(r => r.Name));
        Assert.Single(app.Services.GetRequiredService<Application.Startup.IDatabaseLifecycle>().ListBackups());
        var deletion = app.Services.GetRequiredService<IWorkspaceDeletion>();
        Assert.False((await deletion.InspectAsync(city.Id, CancellationToken.None)).Exists);
        Assert.Equal((4, 3), Counts(await deletion.InspectAsync(trains.Id, CancellationToken.None)));
        Assert.Equal((1, 0), Counts(await deletion.InspectAsync(photos.Id, CancellationToken.None)));
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 4);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_the_open_workspace_from_its_row_moves_to_the_most_recent_other_one()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var trains = await app.StartWithWorkspaceAsync("火车");
        var (older, recent) = await WorkspaceSwitcherTests.AddAsync(app, "摄影史", "天文");
        app.ShellPage.SelectedWorkspace = older;
        app.ShellPage.SelectedWorkspace = recent;
        app.ShellPage.SelectedWorkspace = trains;
        await app.Workbench.LastLocationSave;
        var page = await OpenPageAsync(app, window);

        page.Rows[0].DeleteCommand.Execute(null);
        var dialog = app.Root.WorkspaceDeletion;
        Assert.Equal(trains.Id, dialog.Target!.Id);
        await dialog.Loading;
        dialog.TypedName = "火车";
        await dialog.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(recent.Id, app.Workbench.CurrentWorkspace!.Id);
        Assert.True(page.IsDisposed);
        Assert.Equal([older.Id, recent.Id], app.Workbench.Workspaces.Select(w => w.Id));
        window.UpdateLayout();
        Assert.Equal("天文", Part<TextBlock>(window, "WorkspacePickerName").Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_workspace_made_on_the_page_is_listed_without_leaving_the_page()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var trains = await app.StartWithWorkspaceAsync("火车");
        var page = await OpenPageAsync(app, window);

        UiInput.Click(window, Find<Button>(window, "NewWorkspaceOnPage"));
        Assert.True(page.IsCreating);
        var box = Find<TextBox>(window, "NewWorkspaceNameOnPage");
        await UiWait.UntilAsync(() => box.IsFocused);
        window.KeyTextInput("航空史");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => page.Rows.Count == 2);
        await page.Loaded;

        Assert.False(page.IsCreating);
        Assert.Equal(trains.Id, app.Workbench.CurrentWorkspace!.Id);
        Assert.Same(page, app.Workbench.CurrentPage);
        Assert.Equal(["火车", "航空史"], page.Rows.Select(r => r.Name));
        Assert.Equal(UiText.WorkspaceCounts(0, 0), page.Rows[1].CountsText);
        Assert.Equal(2, (await app.Services.GetRequiredService<WorkspaceService>().ListAsync(CancellationToken.None)).Count);

        // Esc closes the form and forgets what was typed; the focus goes back to 新建工作区….
        UiInput.Click(window, Find<Button>(window, "NewWorkspaceOnPage"));
        await UiWait.UntilAsync(() => box.IsFocused);
        window.KeyTextInput("半截");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(page.IsCreating);
        Assert.Equal(string.Empty, page.NewWorkspace.Name);
        await UiWait.UntilAsync(() => Find<Button>(window, "NewWorkspaceOnPage").IsFocused);
        Assert.Equal(2, page.Rows.Count);
        window.Close();
    }

    private static async Task<WorkspacesViewModel> OpenPageAsync(DesktopHarness app, MainWindow window)
    {
        app.ShellPage.ManageWorkspacesCommand.Execute(null);
        var page = Assert.IsType<WorkspacesViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        window.UpdateLayout();
        return page;
    }

    private static (int, int) Counts(WorkspaceContents contents) => (contents.Conversations, contents.Summaries);

    private static List<Control> Rows(Window window, string name) => Rows<Control>(window, name);

    /// <summary>The named part of every row, in the rows' order.</summary>
    private static List<T> Rows<T>(Window window, string name)
        where T : Control =>
        [.. window.GetVisualDescendants().OfType<WorkspacesView>().Single().GetVisualDescendants().OfType<T>().Where(c => c.Name == name)];

    private static List<string?> Texts(Window window, string name) => [.. Rows<TextBlock>(window, name).Select(t => t.Text)];

    private static List<bool> Visible(Window window, string name) => [.. Rows(window, name).Select(c => c.IsEffectivelyVisible)];
}
