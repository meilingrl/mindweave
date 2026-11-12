using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// 设置 › 使用统计. Keyboard handling that is about the controls: the arrow keys move along the scope and period segments
/// (as on 设置's appearance picker), and focus goes into a model's price editor when it opens and back to its 单价…
/// button when it closes, however it closed (保存, 取消, Enter or Esc).
/// </summary>
public sealed partial class UsageStatisticsView : UserControl
{
    private readonly HashSet<ModelUsageRowViewModel> _watched = [];

    public UsageStatisticsView()
    {
        InitializeComponent();
        ScopePicker.AddHandler(KeyDownEvent, OnSegmentKeyDown, RoutingStrategies.Tunnel);
        PeriodPicker.AddHandler(KeyDownEvent, OnSegmentKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Shown again (back from another page or section): the figures are read again.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as UsageStatisticsViewModel)?.Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var row in _watched)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        _watched.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private static void OnSegmentKeyDown(object? sender, KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Left or Key.Up => -1,
            Key.Right or Key.Down => 1,
            _ => 0,
        };
        if (step == 0 || sender is not Visual picker)
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

    private void OnSetPriceClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ModelUsageRowViewModel row && _watched.Add(row))
        {
            // The command runs after Click: the editor opening is seen through IsEditing, as every later close and open.
            row.PropertyChanged += OnRowChanged;
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelUsageRowViewModel.IsEditing) && sender is ModelUsageRowViewModel row)
        {
            FocusIn(row, row.IsEditing ? "InputPriceBox" : "SetPriceButton");
        }
    }

    /// <summary>Once the row has laid out its editor (or button) again, focuses the named control in it.</summary>
    private void FocusIn(ModelUsageRowViewModel row, string name) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                var target = ModelRows.GetVisualDescendants().OfType<Control>()
                    .FirstOrDefault(c => c.Name == name && c.DataContext == row && c.IsEffectivelyVisible);
                if (target is TextBox box)
                {
                    box.Focus(NavigationMethod.Tab);
                    box.SelectAll();
                }
                else
                {
                    target?.Focus(NavigationMethod.Tab);
                }
            },
            DispatcherPriority.Background);
}
