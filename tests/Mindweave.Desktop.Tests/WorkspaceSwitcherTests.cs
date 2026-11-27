using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The sidebar's workspace switcher (2026-11-10): a quiet button and a menu instead of a stock drop-down, with the
/// workspace actions gathered behind it (新建工作区…, 管理工作区…) instead of a separate + and a right-click menu.
/// </summary>
public class WorkspaceSwitcherTests
{
    [AvaloniaFact]
    public async Task The_closed_switcher_names_the_open_workspace_and_is_the_only_workspace_control_in_the_sidebar()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车历史");
        window.UpdateLayout();

        var picker = Part<Button>(window, "WorkspacePicker");
        Assert.Contains("workspace-switcher", picker.Classes);
        Assert.Equal("火车历史", Part<TextBlock>(window, "WorkspacePickerName").Text);
        Assert.Equal(UiText.WorkspaceSwitcherName("火车历史"), AutomationProperties.GetName(picker));
        Assert.Null(picker.ContextMenu);
        Assert.True(Part<Button>(window, "CollapseSidebar").IsEffectivelyVisible);

        // Consolidated (2026-11-10): no stock drop-down, no separate + beside it, the form hidden until asked for.
        var shell = window.GetVisualDescendants().OfType<ShellView>().Single();
        Assert.Empty(shell.GetVisualDescendants().OfType<ComboBox>());
        Assert.DoesNotContain(shell.GetVisualDescendants().OfType<Control>(), c => c.Name == "ToggleNewWorkspace");
        Assert.False(Part<Border>(window, "NewWorkspaceForm").IsVisible);
        Assert.False(app.ShellPage.IsWorkspaceMenuOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_opens_a_menu_of_every_workspace_with_a_check_on_the_open_one_and_the_entries_below()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var trains = await app.StartWithWorkspaceAsync("火车历史");
        await AddAsync(app, "城市交通", "摄影史");

        UiInput.Click(window, Part<Button>(window, "WorkspacePicker"));
        Assert.True(app.ShellPage.IsWorkspaceMenuOpen);
        Assert.Contains("open", Part<Button>(window, "WorkspacePicker").Classes);
        await UiWait.UntilAsync(() => MenuRows(window).Count == 7);
        window.UpdateLayout();

        var rows = MenuRows(window);
        Assert.Equal(["火车历史", "城市交通", "摄影史", UiText.NewWorkspaceEntry, UiText.ImportEntry, UiText.ManualBackupEntry, UiText.ManageWorkspaces], rows.Select(Label));
        Assert.Equal(["火车历史", "城市交通", "摄影史", UiText.NewWorkspaceEntry, UiText.ImportEntry, UiText.ManualBackupEntry, UiText.ManageWorkspaces], rows.Select(r => AutomationProperties.GetName(r)));
        var current = rows[0];
        Assert.Equal(trains.Id, Assert.IsType<WorkspaceChoice>(current.DataContext).Workspace.Id);
        Assert.True(Check(current).IsEffectivelyVisible);
        Assert.False(Check(rows[1]).IsEffectivelyVisible);
        Assert.False(Check(rows[2]).IsEffectivelyVisible);
        Assert.Equal(FontWeight.SemiBold, current.FontWeight);
        Assert.Equal(FontWeight.Normal, rows[1].FontWeight);
        Assert.Equal(UiText.CurrentWorkspaceHint, AutomationProperties.GetHelpText(current));
        Assert.Null(AutomationProperties.GetHelpText(rows[1]));

        // The open workspace is marked by the check and the weight, never by a filled band (the old purple highlight).
        Assert.Equal(Brushes.Transparent.Color, Assert.IsAssignableFrom<ISolidColorBrush>(Presenter(current).Background ?? Brushes.Transparent).Color);

        // A click elsewhere closes it, and nothing changes.
        app.ShellPage.IsWorkspaceMenuOpen = false;
        window.UpdateLayout();
        Assert.Empty(MenuRows(window));
        Assert.Equal(trains.Id, app.Workbench.CurrentWorkspace!.Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_keyboard_opens_moves_through_chooses_and_escapes_the_menu()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车历史");
        var (city, photos) = await AddAsync(app, "城市交通", "摄影史");
        window.UpdateLayout();
        var picker = Part<Button>(window, "WorkspacePicker");

        // Each opening key: the open workspace's row takes the focus; Esc closes and gives it back to the switcher.
        foreach (var (key, physical, modifiers) in new[]
        {
            (Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None),
            (Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.Alt),
            (Key.Enter, PhysicalKey.Enter, RawInputModifiers.None),
            (Key.Space, PhysicalKey.Space, RawInputModifiers.None),
        })
        {
            picker.Focus(NavigationMethod.Tab);
            window.KeyPress(key, modifiers, physical, null);
            await UiWait.UntilAsync(() => app.ShellPage.IsWorkspaceMenuOpen && Focused(window) is Button { DataContext: WorkspaceChoice { IsCurrent: true } });
            window.KeyRelease(key, modifiers, physical, null);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(app.ShellPage.IsWorkspaceMenuOpen);
            Assert.Same(picker, Focused(window));
        }

        // Down and Up move (wrapping), Home and End jump; the footer entries are rows like the others.
        picker.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        await UiWait.UntilAsync(() => Focused(window) is Button { DataContext: WorkspaceChoice { IsCurrent: true } });
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal("城市交通", Label((Button)Focused(window)!));
        Press(window, Key.End, PhysicalKey.End);
        Assert.Equal(UiText.ManageWorkspaces, Label((Button)Focused(window)!));
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal("火车历史", Label((Button)Focused(window)!));
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(UiText.ManageWorkspaces, Label((Button)Focused(window)!));
        Press(window, Key.Home, PhysicalKey.Home);
        Assert.Equal("火车历史", Label((Button)Focused(window)!));
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal("摄影史", Label((Button)Focused(window)!));
        Assert.True(((Button)Focused(window)!).IsFocused);

        // Enter chooses: the menu closes, the workspace opens, the focus is back on the switcher, which names it.
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.False(app.ShellPage.IsWorkspaceMenuOpen);
        Assert.Equal(photos.Id, app.Workbench.CurrentWorkspace!.Id);
        await UiWait.UntilAsync(() => ReferenceEquals(Focused(window), picker));
        window.UpdateLayout();
        Assert.Equal("摄影史", Part<TextBlock>(window, "WorkspacePickerName").Text);
        Assert.Equal(UiText.WorkspaceSwitcherName("摄影史"), AutomationProperties.GetName(picker));

        // Space chooses too.
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        await UiWait.UntilAsync(() => Focused(window) is Button { DataContext: WorkspaceChoice { IsCurrent: true } });
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal("城市交通", Label((Button)Focused(window)!));
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Assert.Equal(city.Id, app.Workbench.CurrentWorkspace!.Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_screen_reader_is_told_whether_the_menu_is_open_and_can_open_and_close_it()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车历史");
        await AddAsync(app, "城市交通", "摄影史");
        window.UpdateLayout();
        var picker = Part<Button>(window, "WorkspacePicker");

        // As the stock drop-down did: the expand/collapse pattern follows the menu, however it opens or closes.
        var peer = Assert.IsAssignableFrom<Avalonia.Automation.Provider.IExpandCollapseProvider>(Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(picker));
        Assert.True(peer.ShowsMenu);
        Assert.Equal(ExpandCollapseState.Collapsed, peer.ExpandCollapseState);
        UiInput.Click(window, picker);
        Assert.True(app.ShellPage.IsWorkspaceMenuOpen);
        Assert.Equal(ExpandCollapseState.Expanded, peer.ExpandCollapseState);
        app.ShellPage.IsWorkspaceMenuOpen = false;
        Assert.Equal(ExpandCollapseState.Collapsed, peer.ExpandCollapseState);

        // And through it the menu opens and closes.
        peer.Expand();
        Assert.True(app.ShellPage.IsWorkspaceMenuOpen);
        await UiWait.UntilAsync(() => MenuRows(window).Count == 7);
        peer.Collapse();
        Assert.False(app.ShellPage.IsWorkspaceMenuOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_a_row_switches_at_once()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var trains = app.Workbench.CurrentWorkspace!;
        var (other, _) = await AddAsync(app, "摄影", "天文");
        await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);

        UiInput.Click(window, Part<Button>(window, "WorkspacePicker"));
        await UiWait.UntilAsync(() => MenuRows(window).Count == 7);
        window.UpdateLayout();
        UiInput.Click(window, MenuRows(window).Single(r => Label(r) == "摄影"));
        Assert.False(app.ShellPage.IsWorkspaceMenuOpen);
        Assert.Equal(other.Id, app.Workbench.CurrentWorkspace!.Id);
        window.UpdateLayout();
        Assert.Equal("摄影", Part<TextBlock>(window, "WorkspacePickerName").Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task New_workspace_opens_the_form_under_the_switcher_with_the_focus_in_it_and_enter_creates()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车历史");

        await OpenMenuAsync(app, window);
        UiInput.Click(window, Find<Button>(window, "NewWorkspaceEntry"));
        Assert.False(app.ShellPage.IsWorkspaceMenuOpen);
        Assert.True(app.ShellPage.IsCreatingWorkspace);
        var box = Part<TextBox>(window, "NewWorkspaceName");
        await UiWait.UntilAsync(() => box.IsFocused);
        Assert.True(Part<Border>(window, "NewWorkspaceForm").IsEffectivelyVisible);
        Assert.Equal(UiText.WorkspaceNameLabel, AutomationProperties.GetName(box));

        // An invalid name is said under the box and kept; Enter with a valid one creates and opens the workspace.
        window.KeyTextInput("   ");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Single(app.Workbench.Workspaces);
        box.Text = new string('长', WorkspaceName.MaxLength + 1);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => app.ShellPage.NewWorkspace.Error is not null);
        window.UpdateLayout();
        Assert.True(Part<TextBlock>(window, "NewWorkspaceError").IsEffectivelyVisible);
        Assert.Single(app.Workbench.Workspaces);
        box.Text = "城市交通";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Name.Value == "城市交通");
        Assert.False(app.ShellPage.IsCreatingWorkspace);
        Assert.Equal(["火车历史", "城市交通"], (await app.Services.GetRequiredService<WorkspaceService>().ListAsync(CancellationToken.None)).Select(w => w.Name.Value));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_or_cancel_closes_the_new_workspace_form_and_forgets_what_was_typed()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车历史");
        window.UpdateLayout();
        var picker = Part<Button>(window, "WorkspacePicker");

        app.ShellPage.BeginCreateWorkspaceCommand.Execute(null);
        var box = Part<TextBox>(window, "NewWorkspaceName");
        await UiWait.UntilAsync(() => box.IsFocused);
        window.KeyTextInput("半截");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(app.ShellPage.IsCreatingWorkspace);
        Assert.Equal(string.Empty, app.ShellPage.NewWorkspace.Name);
        Assert.Same(picker, Focused(window));

        app.ShellPage.BeginCreateWorkspaceCommand.Execute(null);
        await UiWait.UntilAsync(() => box.IsFocused);
        Assert.Equal(string.Empty, box.Text ?? string.Empty);
        window.KeyTextInput("又一个");
        window.UpdateLayout();
        UiInput.Click(window, Part<Button>(window, "CancelNewWorkspaceButton"));
        Assert.False(app.ShellPage.IsCreatingWorkspace);
        Assert.Equal(string.Empty, app.ShellPage.NewWorkspace.Name);
        Assert.Single(app.Workbench.Workspaces);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Manage_workspaces_opens_the_settings_page_that_lists_them()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车历史");
        await AddAsync(app, "城市交通", "摄影史");

        await OpenMenuAsync(app, window);
        UiInput.Click(window, Find<Button>(window, "ManageWorkspacesEntry"));
        Assert.False(app.ShellPage.IsWorkspaceMenuOpen);
        Assert.True(app.ShellPage.IsSettings);
        var page = Assert.IsType<WorkspacesViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(UiText.WorkspacesTitle, app.ShellPage.Title);
        Assert.Equal(["火车历史", "城市交通", "摄影史"], page.Rows.Select(r => r.Name));

        // Back leads to the settings home, as from 数据与存储.
        Assert.True(app.ShellPage.GoBackCommand.CanExecute(null));
        app.ShellPage.GoBackCommand.Execute(null);
        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        Assert.True(page.IsDisposed);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_new_answer_elsewhere_puts_its_dot_in_the_open_menu_and_its_automation_name()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var (city, _) = await AddAsync(app, "城市交通", "摄影史");
        var news = app.ShellPage.WorkspaceNews;
        await OpenMenuAsync(app, window);
        var row = MenuRows(window).Single(r => Label(r) == "城市交通");
        var dot = row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(e => e.Name == "WorkspaceNewsDot");
        Assert.False(dot.IsVisible);
        Assert.Equal("城市交通", AutomationProperties.GetName(row));

        // The menu stays open while the answer ends there; its row gets the dot and says so.
        app.Client.Reply("1863 年，伦敦。");
        await app.Services.GetRequiredService<Application.Conversations.ConversationService>().StartAsync(city.Id, "第一条地铁在哪里？", CancellationToken.None);
        await UiWait.UntilAsync(() => news.HasNews);
        window.UpdateLayout();
        Assert.True(app.ShellPage.IsWorkspaceMenuOpen);
        Assert.True(dot.IsVisible);
        Assert.Equal(UiText.WorkspaceWithNews("城市交通"), AutomationProperties.GetName(row));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_long_list_scrolls_inside_the_menu()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("工作区 1");
        var service = app.Services.GetRequiredService<WorkspaceService>();
        for (var i = 2; i <= 20; i++)
        {
            app.Workbench.Workspaces.Add(await service.CreateAsync(WorkspaceName.FromStored($"工作区 {i}"), CancellationToken.None));
        }

        await OpenMenuAsync(app, window);
        var scroller = Find<ScrollViewer>(window, "WorkspaceMenuScroller");
        Assert.True(scroller.Bounds.Height <= scroller.MaxHeight);
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);

        // From the last row (管理工作区…) up past the entries above it to the last workspace, scrolled into view.
        Press(window, Key.End, PhysicalKey.End);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        var last = (Button)Focused(window)!;
        Assert.Equal("工作区 20", Label(last));
        window.UpdateLayout();
        var top = last.TranslatePoint(default, scroller)!.Value.Y;
        Assert.InRange(top, 0, scroller.Viewport.Height - last.Bounds.Height + 1);
        window.Close();
    }

    internal static async Task<(Workspace First, Workspace Second)> AddAsync(DesktopHarness app, string first, string second)
    {
        var service = app.Services.GetRequiredService<WorkspaceService>();
        var a = await service.CreateAsync(WorkspaceName.FromStored(first), CancellationToken.None);
        var b = await service.CreateAsync(WorkspaceName.FromStored(second), CancellationToken.None);
        app.Workbench.Workspaces.Add(a);
        app.Workbench.Workspaces.Add(b);
        return (a, b);
    }

    private static async Task OpenMenuAsync(DesktopHarness app, MainWindow window)
    {
        window.UpdateLayout();
        var picker = Part<Button>(window, "WorkspacePicker");
        picker.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        await UiWait.UntilAsync(() => app.ShellPage.IsWorkspaceMenuOpen && Focused(window) is Button { DataContext: WorkspaceChoice });
        window.UpdateLayout();
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
    }

    internal static IReadOnlyList<Button> MenuRows(Window window) =>
        [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("switcher-row") && b.IsEffectivelyVisible)];

    private static string Label(Button row) =>
        row.DataContext is WorkspaceChoice choice ? choice.Workspace.Name.Value : row.GetVisualDescendants().OfType<TextBlock>().Last().Text ?? string.Empty;

    private static PathIcon Check(Button row) => row.GetVisualDescendants().OfType<PathIcon>().Single(p => p.Name == "WorkspaceCurrentCheck");

    private static ContentPresenter Presenter(Button row) =>
        row.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();
}
