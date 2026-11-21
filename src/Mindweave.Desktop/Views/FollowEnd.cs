using Avalonia;
using Avalonia.Controls;

namespace Mindweave.Desktop.Views;

/// <summary>
/// Keeps a <see cref="ScrollViewer"/> at its end while its content grows, as long as the reader has not scrolled away from
/// the end (the live reasoning fold, requirements §10.4.4 v0.21).
/// </summary>
public static class FollowEnd
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(FollowEnd));

    static FollowEnd() => IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
    {
        viewer.ScrollChanged -= OnScrollChanged;
        if (e.NewValue is true)
        {
            viewer.ScrollChanged += OnScrollChanged;
        }
    });

    public static bool GetIsEnabled(ScrollViewer viewer) => viewer.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer viewer, bool value) => viewer.SetValue(IsEnabledProperty, value);

    private static void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.ExtentDelta.Y <= 0)
        {
            return;
        }

        // Where the end was before the content grew: at it (within a pixel or two) means the reader is following.
        var formerEnd = viewer.Extent.Height - e.ExtentDelta.Y;
        if (viewer.Offset.Y + viewer.Viewport.Height >= formerEnd - 2)
        {
            viewer.ScrollToEnd();
        }
    }
}
