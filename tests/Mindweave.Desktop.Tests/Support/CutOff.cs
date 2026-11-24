using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// What a window shows only in part: a control or a text that reaches beyond something that cuts it, where no scrolling
/// brings the rest into view, and a text wider or higher than the place it was given (an ellipsis is a choice, not a cut).
/// </summary>
internal static class CutOff
{
    /// <summary>Rounding of the layout to whole pixels.</summary>
    private const double Tolerance = 1;

    public static IReadOnlyList<string> In(Window window)
    {
        window.UpdateLayout();
        var controls = window.GetVisualDescendants().OfType<Control>().Where(IsLookedAt).ToList();
        return [.. controls.Select(Problem).OfType<string>(), .. Overlaps(window, controls)];
    }

    /// <summary>
    /// Two controls, or a control and a text, side by side in the same row, column or grid that lie over each other. What
    /// floats over a page on purpose (a <see cref="Panel"/> or canvas as the common ground, an overlay) is left alone.
    /// </summary>
    private static IEnumerable<string> Overlaps(Window window, List<Control> controls)
    {
        var outer = controls.Where(c => !c.GetVisualAncestors().Any(a => a is Control parent && controls.Contains(parent))).ToList();
        for (var i = 0; i < outer.Count; i++)
        {
            for (var j = i + 1; j < outer.Count; j++)
            {
                if (outer[i] is TextBlock && outer[j] is TextBlock)
                {
                    continue;
                }

                var both = Shown(outer[i], window).Intersect(Shown(outer[j], window));
                if (both.Width > 3 && both.Height > 3 && outer[i].FindCommonVisualAncestor(outer[j]) is StackPanel or DockPanel or WrapPanel or Grid)
                {
                    yield return $"{Name(outer[i])} and {Name(outer[j])} overlap by {Size(both.Width, both.Height)}";
                }
            }
        }
    }

    /// <summary>The part of a control that is drawn, in the window's coordinates.</summary>
    private static Rect Shown(Control control, Window window)
    {
        var shown = new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window) ?? Matrix.Identity);
        foreach (var clip in control.GetVisualAncestors().OfType<Control>().Where(a => a.ClipToBounds))
        {
            shown = shown.Intersect(new Rect(clip.Bounds.Size).TransformToAABB(clip.TransformToVisual(window) ?? Matrix.Identity));
        }

        return shown;
    }

    private static bool IsLookedAt(Control control) =>
        control is Button or TextBox or ComboBox or NumericUpDown or Slider or PathIcon or TextBlock { Text.Length: > 0 } or TextBlock { Inlines.Count: > 0 }
        && control is { IsEffectivelyVisible: true, Bounds.Height: > 0 }
        && !control.GetVisualAncestors().Any(a => a is ScrollBar);

    private static string? Problem(Control control)
    {
        if (control is TextBlock { TextTrimming: var trimming } text && trimming == TextTrimming.None)
        {
            var inner = text.Bounds.Deflate(text.Padding);
            var width = text.TextLayout.TextLines.Select(l => l.Width).DefaultIfEmpty().Max();
            if (width > inner.Width + Tolerance || text.TextLayout.Height > inner.Height + Tolerance)
            {
                return $"{Name(control)} needs {Size(width, text.TextLayout.Height)}, has {Size(inner.Width, inner.Height)}";
            }
        }

        var rect = new Rect(control.Bounds.Size);
        Visual from = control;
        foreach (var ancestor in control.GetVisualAncestors().OfType<Control>())
        {
            rect = rect.TransformToAABB(from.TransformToVisual(ancestor) ?? Matrix.Identity);
            from = ancestor;
            if (!ancestor.ClipToBounds && ancestor is not Window)
            {
                continue;
            }

            var clip = new Rect(ancestor.Bounds.Size);
            if (ancestor is ScrollContentPresenter scroller)
            {
                // Scrolling brings it into view: from here on it counts as lying inside.
                if (scroller.CanHorizontallyScroll && scroller.Extent.Width > scroller.Viewport.Width)
                {
                    rect = new Rect(0, rect.Y, Math.Min(rect.Width, clip.Width), rect.Height);
                }

                if (scroller.CanVerticallyScroll && scroller.Extent.Height > scroller.Viewport.Height)
                {
                    rect = new Rect(rect.X, 0, rect.Width, Math.Min(rect.Height, clip.Height));
                }
            }

            if (rect.Left < -Tolerance || rect.Top < -Tolerance || rect.Right > clip.Width + Tolerance || rect.Bottom > clip.Height + Tolerance)
            {
                return $"{Name(control)} at {rect} is cut by {Name(ancestor)} {Size(clip.Width, clip.Height)}";
            }
        }

        return null;
    }

    private static string Size(double width, double height) => string.Create(CultureInfo.InvariantCulture, $"{width:0.#} × {height:0.#}");

    /// <summary>The control, and the nearest named one around it when it has no name of its own.</summary>
    private static string Name(Control control)
    {
        var own = control.Name is { Length: > 0 } name ? $" {name}" : string.Empty;
        var text = control switch
        {
            TextBlock { Text: { Length: > 0 } words } => $" \"{words[..Math.Min(words.Length, 30)]}\"",
            ContentControl { Content: string words } => $" \"{words}\"",
            _ => string.Empty,
        };
        var around = own.Length > 0 && !own.StartsWith(" PART_", StringComparison.Ordinal)
            ? string.Empty
            : control.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.Name is { Length: > 0 } n && !n.StartsWith("PART_", StringComparison.Ordinal)) is { } parent
                ? $" in {parent.GetType().Name} {parent.Name}"
                : string.Empty;
        return $"{control.GetType().Name}{own}{text}{around}";
    }
}
