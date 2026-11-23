using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The settings page without boxes (D-037): the category list jumps and follows the scrolling, the find box on top of it
/// leaves only what matches, and the longer explanations are behind an icon.
/// </summary>
public class SettingsFindingTests
{
    [AvaloniaFact]
    public async Task A_category_jumps_to_its_section_and_the_list_follows_the_scrolling()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var scroller = Find<ScrollViewer>(Page(window), "Scroller");
        Assert.True(Find<RadioButton>(window, "CategoryAppearance").IsChecked);

        UiInput.Click(window, Find<RadioButton>(window, "CategoryModel"));
        window.UpdateLayout();
        Assert.InRange(Find<StackPanel>(window, "ModelSection").TranslatePoint(default, scroller)!.Value.Y, -1, 1);
        Assert.True(Find<RadioButton>(window, "CategoryModel").IsChecked);

        // The last one cannot reach the top: it stays marked all the same, and scrolling away unmarks it.
        UiInput.Click(window, Find<RadioButton>(window, "CategoryData"));
        window.UpdateLayout();
        Assert.True(Find<RadioButton>(window, "CategoryData").IsChecked);
        Assert.True(Find<Button>(window, "StorageDetailsButton").TranslatePoint(default, scroller)!.Value.Y < scroller.Viewport.Height);
        scroller.Offset = default;
        window.UpdateLayout();
        Assert.True(Find<RadioButton>(window, "CategoryAppearance").IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Finding_leaves_only_what_matches_and_escape_brings_all_back()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var box = Find<TextBox>(window, "FindSettingsBox");
        Assert.Equal(UiText.FindSettings, AutomationProperties.GetName(box));

        // On top of the categories.
        Assert.True(box.TranslatePoint(default, window)!.Value.Y < Find<RadioButton>(window, "CategoryAppearance").TranslatePoint(default, window)!.Value.Y);

        // By its name.
        box.Text = "正文";
        await SettleAsync(window);
        Assert.True(Find<Border>(window, "TextWidthPicker").IsEffectivelyVisible);
        Assert.False(Find<Border>(window, "AppearancePicker").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(window, "DataSection").IsEffectivelyVisible);
        Screenshots.Capture(window, "settings-find");

        // By a choice it offers, and by its explanation (a sound is "响" once).
        box.Text = "梅紫";
        await SettleAsync(window);
        Assert.True(Find<Border>(window, "BoldColourPicker").IsEffectivelyVisible);
        box.Text = "响一次";
        await SettleAsync(window);
        Assert.True(Find<CheckBox>(window, "NewsSoundToggle").IsEffectivelyVisible);
        Assert.False(Find<CheckBox>(window, "NewsInWorkspaceToggle").IsEffectivelyVisible);

        // By its category: all of it.
        box.Text = "外观";
        await SettleAsync(window);
        Assert.True(Find<Border>(window, "AppearancePicker").IsEffectivelyVisible);
        Assert.True(Find<Border>(window, "TextWidthPicker").IsEffectivelyVisible);

        // Inside a form.
        box.Text = "密钥";
        await SettleAsync(window);
        Assert.True(Find<TextBox>(window, "ApiKey").IsEffectivelyVisible);

        box.Text = "没有这样的设置";
        await SettleAsync(window);
        var nothing = Find<TextBlock>(window, "NothingFound");
        Assert.True(nothing.IsEffectivelyVisible);
        Assert.Equal(UiText.NoSettingFound("没有这样的设置"), nothing.Text);

        Assert.True(box.Focus());
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await SettleAsync(window);
        Assert.False(nothing.IsEffectivelyVisible);
        Assert.True(Find<Border>(window, "AppearancePicker").IsEffectivelyVisible);
        Assert.True(Find<StackPanel>(window, "DataSection").IsEffectivelyVisible);
        Assert.True(Find<CheckBox>(window, "NewsInWorkspaceToggle").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_explanation_opens_from_its_icon_and_costs_stay_in_sight()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var row = Find<CheckBox>(window, "PrepareNamesToggle").FindAncestorOfType<SettingRow>()!;
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == UiText.PrepareNamesCost);
        var info = row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_Info");
        Assert.Equal(UiText.PrepareNamesHint, ToolTip.GetTip(info));
        Assert.Equal(UiText.SettingInfo(UiText.PrepareNames), AutomationProperties.GetName(info));
        UiInput.Click(window, info);
        await UiWait.UntilAsync(() => info.Flyout!.IsOpen);
        Assert.Equal(UiText.PrepareNamesHint, Assert.IsType<TextBlock>(((Flyout)info.Flyout!).Content).Text);
        Screenshots.Capture(window, "settings-info");
        info.Flyout!.Hide();

        // A setting without one has no icon.
        var theme = Find<Border>(window, "AppearancePicker").FindAncestorOfType<SettingRow>()!;
        Assert.False(theme.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_Info").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task On_a_narrow_page_only_the_find_box_stays_above_the_settings()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        window.Width = 720;
        await SettleAsync(window);
        Assert.False(Find<RadioButton>(window, "CategoryAppearance").IsEffectivelyVisible);
        var box = Find<TextBox>(window, "FindSettingsBox");
        var picker = Find<Border>(window, "BoldColourPicker");
        Assert.True(box.IsEffectivelyVisible);
        Assert.True(box.TranslatePoint(new Point(0, box.Bounds.Height), window)!.Value.Y < Find<StackPanel>(window, "AppearanceSection").TranslatePoint(default, window)!.Value.Y);
        Assert.True(picker.TranslatePoint(new Point(picker.Bounds.Width, 0), window)!.Value.X <= window.Bounds.Width);
        Assert.True(picker.TranslatePoint(default, window)!.Value.X >= Page(window).TranslatePoint(default, window)!.Value.X - 1);

        // The choices that do not fit beside their label go under it; a switch stays beside its own.
        var row = picker.FindAncestorOfType<SettingRow>()!;
        var label = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == UiText.BoldColourLabel);
        Assert.True(label.Bounds.Width > 30, $"label {label.Bounds}");
        Assert.True(label.TranslatePoint(new Point(0, label.Bounds.Height), window)!.Value.Y <= picker.TranslatePoint(default, window)!.Value.Y);
        Assert.DoesNotContain("stacked", Find<CheckBox>(window, "NewsSoundToggle").FindAncestorOfType<SettingRow>()!.Classes);
        Screenshots.Capture(window, "settings-narrow");

        // And back beside it once there is room.
        window.Width = 1100;
        await SettleAsync(window);
        Assert.True(label.TranslatePoint(new Point(label.Bounds.Width, 0), window)!.Value.X <= picker.TranslatePoint(default, window)!.Value.X);
        window.Close();
    }

    /// <summary>The window's size and the box's text change are told to the page through the dispatcher.</summary>
    private static async Task SettleAsync(Window window)
    {
        for (var settle = 0; settle < 3; settle++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }

    private static SettingsHomeView Page(Window window) => window.GetVisualDescendants().OfType<SettingsHomeView>().Single();

    private static async Task<Window> OpenAsync(DesktopHarness app)
    {
        var window = app.ShowWindow(1100, 720, ThemeVariant.Light);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Workbench.Navigate(new NavigationRequest(MainSection.Settings));
        await Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).Loaded;
        window.UpdateLayout();
        return window;
    }
}
