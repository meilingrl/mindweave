using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Mindweave.Desktop.Views;

/// <summary>
/// Moves keyboard focus to a control when a view-model flag turns true (an editor or picker opening), so keyboard users
/// land where they can act. Focus is taken once per change to true, after layout, and only while the control is shown.
/// </summary>
public static class FocusRequest
{
    public static readonly AttachedProperty<bool> WhenProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("When", typeof(FocusRequest));

    static FocusRequest() => WhenProperty.Changed.AddClassHandler<InputElement>(OnChanged);

    public static bool GetWhen(InputElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(WhenProperty);
    }

    public static void SetWhen(InputElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(WhenProperty, value);
    }

    private static void OnChanged(InputElement element, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (GetWhen(element) && element.IsEffectivelyVisible)
                {
                    element.Focus(NavigationMethod.Tab);
                    if (element is TextBox box)
                    {
                        box.CaretIndex = box.Text?.Length ?? 0;
                    }
                }
            },
            DispatcherPriority.Loaded);
    }
}
