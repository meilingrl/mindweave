using Avalonia;
using Avalonia.Controls;

namespace Mindweave.Desktop.Views;

/// <summary>
/// Gives a control the class <c>narrow</c> while it is narrower than <c>Narrow.Below</c>, so styles can arrange what is in
/// it anew instead of letting it be cut off.
/// </summary>
public static class Narrow
{
    public static readonly AttachedProperty<double> BelowProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Below", typeof(Narrow));

    static Narrow()
    {
        BelowProperty.Changed.AddClassHandler<Control>((control, _) => Mark(control));
        Visual.BoundsProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (control.IsSet(BelowProperty))
            {
                Mark(control);
            }
        });
    }

    public static double GetBelow(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(BelowProperty);
    }

    public static void SetBelow(Control control, double value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(BelowProperty, value);
    }

    private static void Mark(Control control) =>
        control.Classes.Set("narrow", control.Bounds.Width > 0 && control.Bounds.Width < GetBelow(control));
}
