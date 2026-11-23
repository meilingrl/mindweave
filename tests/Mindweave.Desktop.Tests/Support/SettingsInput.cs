using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
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
}
