using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Mindweave.Desktop.Views;

/// <summary>
/// The arrow keys move along a row of segments (radio buttons of one group, Styles/Settings.axaml): left and up to the
/// one before, right and down to the one after, choosing it as they go. Keyboard handling about the control only.
/// </summary>
internal static class SegmentKeys
{
    public static void Attach(Control picker) => picker.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Left or Key.Up => -1,
            Key.Right or Key.Down => 1,
            _ => 0,
        };
        if (step == 0 || sender is not Control picker)
        {
            return;
        }

        var segments = picker.GetVisualDescendants().OfType<RadioButton>().ToList();
        var current = segments.FindIndex(s => s.IsFocused);
        if (current < 0)
        {
            current = segments.FindIndex(s => s.IsChecked == true);
        }

        var next = Math.Clamp(current + step, 0, segments.Count - 1);
        segments[next].IsChecked = true;
        segments[next].Focus(NavigationMethod.Directional);
        e.Handled = true;
    }
}
