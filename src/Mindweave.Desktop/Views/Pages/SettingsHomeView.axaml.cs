using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// Settings. The arrow keys move along the appearance segments (keyboard handling about the control, not the
/// settings); the model field's own handling lives in <see cref="ModelSettingsForm"/>.
/// </summary>
public sealed partial class SettingsHomeView : UserControl
{
    public SettingsHomeView()
    {
        InitializeComponent();
        AppearancePicker.AddHandler(KeyDownEvent, OnAppearanceKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Each showing builds this view anew: whichever of attaching and receiving the page comes second tells the page it is
    /// shown, so it can read the application's settings again (health check 2026-11-12, ui-vm-3).
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree())
        {
            (DataContext as SettingsHomeViewModel)?.Refresh();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as SettingsHomeViewModel)?.Refresh();
    }

    private void OnAppearanceKeyDown(object? sender, KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Left or Key.Up => -1,
            Key.Right or Key.Down => 1,
            _ => 0,
        };
        if (step == 0)
        {
            return;
        }

        var segments = AppearancePicker.GetVisualDescendants().OfType<RadioButton>().ToList();
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
