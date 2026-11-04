using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Preferences;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The workbench sidebar can be resized and collapsed (S7 round 3, the user's acceptance review 2026-11-02, finding 1):
/// the width stays within its bounds, both are kept in the local preferences and come back at the next start.
/// </summary>
public class SidebarLayoutTests
{
    [AvaloniaFact]
    public async Task Dragging_the_splitter_resizes_the_sidebar_within_its_bounds_and_keeps_the_width()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        window.UpdateLayout();
        var sidebar = Part<Border>(window, "Sidebar");
        Assert.Equal(SidebarLayout.DefaultWidth, sidebar.Bounds.Width);

        // A real drag on the splitter at the sidebar's right edge.
        Drag(window, Part<Control>(window, "SidebarSplitter"), 80);
        Assert.Equal(320, sidebar.Bounds.Width);
        Assert.Equal(320, app.ShellPage.SidebarWidth);
        await app.ShellPage.SidebarSaving;
        Assert.Equal(new SidebarLayout(320, false), await Stored(app));

        // Far past either bound, it stops at the bound.
        Drag(window, Part<Control>(window, "SidebarSplitter"), 600);
        Assert.Equal(SidebarLayout.MaxWidth, sidebar.Bounds.Width);
        Drag(window, Part<Control>(window, "SidebarSplitter"), -900);
        Assert.Equal(SidebarLayout.MinWidth, sidebar.Bounds.Width);
        await app.ShellPage.SidebarSaving;
        Assert.Equal(new SidebarLayout(SidebarLayout.MinWidth, false), await Stored(app));

        // The content area takes the rest of the window.
        var content = Part<Control>(window, "ContentArea");
        Assert.Equal(window.Bounds.Width - SidebarLayout.MinWidth, content.Bounds.Width, 0.5);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Collapsing_hides_the_sidebar_and_gives_its_width_to_the_content()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        window.UpdateLayout();
        var content = Part<Control>(window, "ContentArea");
        var before = content.Bounds.Width;
        var expand = Part<Button>(window, "ExpandSidebar");
        Assert.False(expand.IsEffectivelyVisible);

        Click(window, Part<Button>(window, "CollapseSidebar"));
        Assert.True(app.ShellPage.IsSidebarCollapsed);
        Assert.False(Part<Border>(window, "Sidebar").IsEffectivelyVisible);
        Assert.False(Part<Control>(window, "SidebarSplitter").IsEffectivelyVisible);
        Assert.Equal(window.Bounds.Width, content.Bounds.Width, 0.5);
        Assert.True(content.Bounds.Width > before);
        Assert.True(expand.IsEffectivelyVisible);
        await app.ShellPage.SidebarSaving;
        Assert.True((await Stored(app)).IsCollapsed);

        // Expanding brings back the width it had.
        Click(window, expand);
        Assert.False(app.ShellPage.IsSidebarCollapsed);
        Assert.True(Part<Border>(window, "Sidebar").IsEffectivelyVisible);
        Assert.Equal(before, content.Bounds.Width, 0.5);
        Assert.False(expand.IsEffectivelyVisible);

        // The keyboard shortcut toggles it too, wherever the focus is.
        window.KeyPress(Key.B, ShortcutModifier, PhysicalKey.B, "b");
        window.UpdateLayout();
        Assert.True(app.ShellPage.IsSidebarCollapsed);
        window.KeyPress(Key.B, ShortcutModifier, PhysicalKey.B, "b");
        window.UpdateLayout();
        Assert.False(app.ShellPage.IsSidebarCollapsed);
        await app.ShellPage.SidebarSaving;
        Assert.False((await Stored(app)).IsCollapsed);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Width_and_collapsed_state_come_back_after_a_restart()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        window.UpdateLayout();
        Drag(window, Part<Control>(window, "SidebarSplitter"), 60);
        Click(window, Part<Button>(window, "CollapseSidebar"));
        await app.ShellPage.SidebarSaving;
        window.Close();

        await app.RestartAsync();
        var again = Open(app);
        again.UpdateLayout();
        Assert.True(app.ShellPage.IsSidebarCollapsed);
        Assert.False(Part<Border>(again, "Sidebar").IsEffectivelyVisible);
        Click(again, Part<Button>(again, "ExpandSidebar"));
        Assert.Equal(300, Part<Border>(again, "Sidebar").Bounds.Width);
        again.Close();
    }

    [AvaloniaFact]
    public async Task Stored_values_outside_the_bounds_or_unreadable_fall_back_inside_them()
    {
        await using var app = DesktopHarness.Create();
        var store = app.Services.GetRequiredService<IPreferenceStore>();
        await app.StartWithWorkspaceAsync("火车");
        var layout = app.Services.GetRequiredService<ISidebarLayout>();

        await store.SetAsync(SidebarLayoutService.WidthKey, "9000", CancellationToken.None);
        Assert.Equal(SidebarLayout.MaxWidth, (await layout.GetAsync(CancellationToken.None)).Width);
        await store.SetAsync(SidebarLayoutService.WidthKey, "12", CancellationToken.None);
        Assert.Equal(SidebarLayout.MinWidth, (await layout.GetAsync(CancellationToken.None)).Width);
        await store.SetAsync(SidebarLayoutService.WidthKey, "wide", CancellationToken.None);
        await store.SetAsync(SidebarLayoutService.CollapsedKey, "maybe", CancellationToken.None);
        Assert.Equal(SidebarLayout.Default, await layout.GetAsync(CancellationToken.None));

        // Width set from code is clamped as well.
        app.ShellPage.SidebarWidth = 10;
        Assert.Equal(SidebarLayout.MinWidth, app.ShellPage.SidebarWidth);
        app.ShellPage.SidebarWidth = double.NaN;
        Assert.Equal(SidebarLayout.DefaultWidth, app.ShellPage.SidebarWidth);
    }

    [AvaloniaFact]
    public async Task While_collapsed_the_expand_button_shows_that_a_conversation_needs_attention()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        Click(window, Part<Button>(window, "CollapseSidebar"));
        var dot = Part<Ellipse>(window, "ExpandSidebarAttention");
        Assert.False(dot.IsEffectivelyVisible);

        app.Client.Fail(GenerationErrorKind.Network, "connection reset", "半句");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "问";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].State == MessageState.Failed);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.HasAttention);
        window.UpdateLayout();
        Assert.True(dot.IsEffectivelyVisible);
        Assert.Equal(UiText.ExpandSidebarAttention, ToolTip.GetTip(Part<Button>(window, "ExpandSidebar")));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_narrow_window_narrows_the_sidebar_not_the_content_and_keeps_the_stored_width()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        window.UpdateLayout();
        Drag(window, Part<Control>(window, "SidebarSplitter"), 600);
        await app.ShellPage.SidebarSaving;
        var sidebar = Part<Border>(window, "Sidebar");
        var content = Part<Control>(window, "ContentArea");
        Assert.Equal(SidebarLayout.MaxWidth, sidebar.Bounds.Width);

        // At the window's minimum width the page keeps its minimum; the sidebar gives way.
        window.Width = 720;
        window.UpdateLayout();
        Assert.Equal(720 - ShellView.MinContentWidth, sidebar.Bounds.Width, 0.5);
        Assert.True(content.Bounds.Width >= ShellView.MinContentWidth - 0.5);
        Assert.Equal(SidebarLayout.MaxWidth, app.ShellPage.SidebarWidth);

        // A wider window lets the stored width show again.
        window.Width = 1100;
        window.UpdateLayout();
        Assert.Equal(SidebarLayout.MaxWidth, sidebar.Bounds.Width, 0.5);

        // A drag starts from the width shown and cannot push past what the window spares.
        window.Width = 720;
        Drag(window, Part<Control>(window, "SidebarSplitter"), -20);
        Assert.Equal(720 - ShellView.MinContentWidth - 20, sidebar.Bounds.Width, 0.5);
        Drag(window, Part<Control>(window, "SidebarSplitter"), 300);
        Assert.Equal(720 - ShellView.MinContentWidth, sidebar.Bounds.Width, 0.5);
        Assert.True(content.Bounds.Width >= ShellView.MinContentWidth - 0.5);
        window.Close();
    }

    [AvaloniaFact]
    public void The_splitter_is_named_for_assistive_technology_and_carries_no_instruction()
    {
        var view = new ShellView();
        var splitter = view.FindControl<Border>("SidebarSplitter")!;
        Assert.Null(ToolTip.GetTip(splitter));
        Assert.Equal(UiText.SidebarSplitter, Avalonia.Automation.AutomationProperties.GetName(splitter));
        Assert.DoesNotContain("拖动", UiText.SidebarSplitter, StringComparison.Ordinal);
    }

    private static RawInputModifiers ShortcutModifier =>
        OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static async Task<SidebarLayout> Stored(DesktopHarness app) =>
        await app.Services.GetRequiredService<ISidebarLayout>().GetAsync(CancellationToken.None);

    private static T Part<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<ShellView>().Single().GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Drag(Window window, Control splitter, double dx)
    {
        window.UpdateLayout();
        var start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 300), window)!.Value;
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Vector(dx / 2, 0), RawInputModifiers.LeftMouseButton);
        window.MouseMove(start + new Vector(dx, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(start + new Vector(dx, 0), MouseButton.Left);
        window.UpdateLayout();
    }

    private static void Click(Window window, Button button) => UiInput.Click(window, button);

    private static MainWindow Open(DesktopHarness app)
    {
        var window = new MainWindow { DataContext = app.Root, Width = 1100, Height = 720 };
        window.Show();
        return window;
    }
}
