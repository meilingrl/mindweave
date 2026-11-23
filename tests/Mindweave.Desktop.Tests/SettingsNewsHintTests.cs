using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using static Mindweave.Desktop.Tests.UiTree;
using Scene = Mindweave.Desktop.Tests.WorkspaceNewsHintTests.Scene;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// A new-answer hint never covers the settings (the owner, 2026-11-23): their controls stand at the right edge, where the hint
/// comes up, and a press meant for a switch could close it. While one shows in Settings the page ends above it.
/// </summary>
public class SettingsNewsHintTests
{
    public static TheoryData<double, double> Sizes => new() { { 1100, 720 }, { 720, 480 }, { 1600, 900 } };

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task No_control_of_the_settings_lies_under_a_hint(double width, double height)
    {
        await using var scene = await Scene.StartAsync();
        var window = scene.Window;
        window.Width = width;
        window.Height = height;
        await OpenSettingsAsync(scene);
        var host = Find<ContentControl>(window, "PageHost");
        var whole = host.Bounds.Height;

        await scene.AnswerAsync(scene.Trains, "火车有多快？");
        await SettleAsync(window);
        var hints = Find<ItemsControl>(window, "NewsToasts");
        Assert.True(hints.IsEffectivelyVisible && hints.Bounds.Height > 0);
        Assert.True(host.Bounds.Height < whole);
        AssertClear(window, hints);
        Screenshots.Capture(window, $"settings-news-hint-{width}");

        // Two hints take more room, and the page is never squeezed away.
        await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        await SettleAsync(window);
        AssertClear(window, hints);
        Assert.True(host.Bounds.Height >= 120, $"page {host.Bounds.Height}");

        // Gone with the hints.
        foreach (var toast in scene.News.Toasts.ToList())
        {
            toast.CloseCommand.Execute(null);
        }

        await SettleAsync(window);
        Assert.Equal(whole, host.Bounds.Height, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_switch_that_lay_under_the_hint_is_pressed_and_the_hint_stays()
    {
        await using var scene = await Scene.StartAsync();
        var window = scene.Window;
        window.Width = 1100;
        window.Height = 900;
        var settings = await OpenSettingsAsync(scene);
        await scene.AnswerAsync(scene.Trains, "火车有多快？");
        await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        await SettleAsync(window);
        var before = settings.AutoPreparesRecommendations;
        UiInput.Click(window, Find<CheckBox>(window, "AutoPrepareRecommendationsToggle"));
        Assert.NotEqual(before, settings.AutoPreparesRecommendations);
        Assert.Equal(2, scene.News.Toasts.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pages_outside_settings_keep_their_height_and_the_hint_its_place_above_the_input()
    {
        await using var scene = await Scene.StartAsync();
        var window = scene.Window;
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        await SettleAsync(window);
        var host = Find<ContentControl>(window, "PageHost");
        var whole = host.Bounds.Height;
        var hints = Find<ItemsControl>(window, "NewsToasts");
        await scene.AnswerAsync(scene.Trains, "火车有多快？");
        await SettleAsync(window);
        Assert.Equal(whole, host.Bounds.Height, 1);
        var bottom = hints.Margin.Bottom;
        Assert.Equal(100, bottom);

        // Into Settings with the hint up, and back.
        await OpenSettingsAsync(scene);
        await SettleAsync(window);
        Assert.True(host.Bounds.Height < whole);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        await SettleAsync(window);
        Assert.Equal(whole, host.Bounds.Height, 1);
        Assert.Equal(bottom, hints.Margin.Bottom);
        window.Close();
    }

    /// <summary>Every place of the page, as far as the page shows it, is outside the hints.</summary>
    private static void AssertClear(Window window, ItemsControl hints)
    {
        var page = Find<ContentControl>(window, "PageHost");
        var pageRect = new Rect(page.TranslatePoint(default, window)!.Value, page.Bounds.Size);
        var hintRect = new Rect(hints.TranslatePoint(default, window)!.Value, hints.Bounds.Size);
        Assert.True(pageRect.Bottom <= hintRect.Top, $"page {pageRect} hints {hintRect}");
        Assert.True(hintRect.Bottom <= window.Bounds.Height);
    }

    private static async Task<SettingsHomeViewModel> OpenSettingsAsync(Scene scene)
    {
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(scene.App.Workbench.CurrentPage);
        await settings.Loaded;
        await SettleAsync(scene.Window);
        return settings;
    }

    private static async Task SettleAsync(Window window)
    {
        for (var settle = 0; settle < 4; settle++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }

        window.CaptureRenderedFrame()?.Dispose();
    }
}
