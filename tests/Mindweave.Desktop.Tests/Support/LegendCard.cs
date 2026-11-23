using Avalonia.Controls;
using Avalonia.Interactivity;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>The summary network's legend: a card its icon opens (the owner, 2026-11-23).</summary>
internal static class LegendCard
{
    /// <summary>Whether the legend, once opened, shows its part <paramref name="name"/>; false where there is no legend (the list view).</summary>
    public static bool Shows(Window window, string name)
    {
        window.UpdateLayout();
        if (!Find<Button>(window, "LegendButton").IsEffectivelyVisible)
        {
            return false;
        }

        Toggle(window);
        var shown = Find<Border>(window, "LegendCard").IsVisible && Find<StackPanel>(window, name).IsEffectivelyVisible;
        Toggle(window);
        return shown;
    }

    /// <summary>The icon pressed (Enter or a click): opens the card and keeps it open, or closes it.</summary>
    public static void Toggle(Window window)
    {
        Find<Button>(window, "LegendButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
    }
}
