using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>Input on the settings page.</summary>
internal static class SettingsInput
{
    /// <summary>
    /// Clicks a setting's control, scrolled to the upper part of the page first: the controls stand at the right edge, where a
    /// news hint lies over the lower corner, and how far down a control is depends on the font. Drawn there before the press,
    /// which lands on what was last rendered.
    /// </summary>
    public static void Click(Window window, Control control)
    {
        var scroller = Find<ScrollViewer>(window.GetVisualDescendants().OfType<SettingsHomeView>().Single(), "Scroller");
        window.UpdateLayout();
        scroller.Offset = new Vector(0, Math.Max(0, scroller.Offset.Y + control.TranslatePoint(default, scroller)!.Value.Y - 60));
        window.UpdateLayout();
        window.CaptureRenderedFrame()?.Dispose();
        UiInput.Click(window, control);
    }

    /// <summary>
    /// Clicks one of the rows that lead on, once the page has read everything it shows: what is still coming in above the row
    /// moves it, between aiming and pressing too. New-answer hints move it as well: in Settings the page ends above them,
    /// and they come up a moment after the answers, so a test that seeds answers starts from seen ones
    /// (<see cref="SummaryNetworkScenario.SeedSeenAsync"/>); under load a hint landed between press and release (2026-11-27).
    /// </summary>
    public static async Task OpenAsync(Window window, string row)
    {
        var settings = (SettingsHomeViewModel)window.GetVisualDescendants().OfType<SettingsHomeView>().Single().DataContext!;
        await settings.Loaded;
        await settings.Model.Loaded;
        if (settings.Tools is { } tools)
        {
            await tools.Loaded;
            await (tools.Web?.Loaded ?? Task.CompletedTask);
            await (tools.Mcp?.Loaded ?? Task.CompletedTask);
        }

        window.UpdateLayout();
        Click(window, Find<Button>(window, row));
    }
}
