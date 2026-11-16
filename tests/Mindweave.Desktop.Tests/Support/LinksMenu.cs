using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>The network toolbar's 关联 menu (2026-11-16): 建议关联 and 显示／隐藏关联, opened by a real click on its button.</summary>
internal static class LinksMenu
{
    public static Button Button(Window window) => Find<Button>(window, "LinksMenuButton");

    /// <summary>Clicks the 关联 button and returns its menu, open and laid out.</summary>
    public static MenuFlyout Open(Window window)
    {
        window.UpdateLayout();
        UiInput.Click(window, Button(window));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var flyout = Assert.IsType<MenuFlyout>(Button(window).Flyout);
        Assert.True(flyout.IsOpen);
        return flyout;
    }

    /// <summary>A shown entry of the open menu.</summary>
    public static MenuItem Entry(Window window, string name) =>
        window.GetVisualDescendants().OfType<MenuItem>().Single(m => m.Name == name && m.IsEffectivelyVisible);

    /// <summary>Whether the open menu shows the entry.</summary>
    public static bool Shows(Window window, string name) =>
        window.GetVisualDescendants().OfType<MenuItem>().Any(m => m.Name == name && m.IsEffectivelyVisible);

    /// <summary>Opens the menu and clicks an entry (which closes it).</summary>
    public static void Choose(Window window, string name)
    {
        Open(window);
        UiInput.Click(window, Entry(window, name));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    public static void Close(Window window)
    {
        (Button(window).Flyout as MenuFlyout)?.Hide();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
