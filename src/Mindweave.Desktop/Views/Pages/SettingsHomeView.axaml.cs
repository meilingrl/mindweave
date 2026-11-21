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
        Scroller.ScrollChanged += OnScrollChanged;
    }

    /// <summary>
    /// The page keeps how far it is scrolled, so a language switch can carry it over; a carried position is applied as
    /// soon as the page is long enough for it (sections fill in as their settings are read), and kept no longer.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not SettingsHomeViewModel page)
        {
            return;
        }

        if (page.PendingScroll is { } wanted)
        {
            if (Scroller.Extent.Height - Scroller.Viewport.Height >= wanted - 0.5)
            {
                page.PendingScroll = null;
                Scroller.Offset = new Vector(Scroller.Offset.X, wanted);
            }

            return;
        }

        page.ScrollOffset = Scroller.Offset.Y;
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
        if (DataContext is SettingsHomeViewModel page)
        {
            page.Refresh();
            if (page.PendingScroll is not null)
            {
                _ = SettleScrollAsync(page);
            }
        }
    }

    /// <summary>Once the settings are read and laid out, a carried position the page is too short for goes as far as it can.</summary>
    private async Task SettleScrollAsync(SettingsHomeViewModel page)
    {
        await page.Loaded;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        if (page.PendingScroll is { } wanted && ReferenceEquals(DataContext, page))
        {
            page.PendingScroll = null;
            Scroller.Offset = new Vector(Scroller.Offset.X, wanted);
            page.ScrollOffset = Scroller.Offset.Y;
        }
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
