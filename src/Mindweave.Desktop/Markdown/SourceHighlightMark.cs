using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LiveMarkdown.Avalonia;

namespace Mindweave.Desktop.Markdown;

/// <summary>The colours of the source highlight: the tint behind the text, the text on it, and the line under it.</summary>
/// <param name="Background">The tint painted behind the highlighted text.</param>
/// <param name="Foreground">The text colour on the tint; null keeps the text's own colours.</param>
/// <param name="Mark">The line drawn along the bottom of each highlighted line; null draws none.</param>
public sealed record SourceHighlightPaint(IBrush Background, IBrush? Foreground = null, IBrush? Mark = null);

/// <summary>
/// The second cue of the source highlight (S7, 2026-11-10): a solid line along the bottom of each highlighted line, so
/// the passage stands out by more than its tint. LiveMarkdown's highlight styles only paint a background and a text
/// colour, so this is drawn as an adorner over the text block, in the block's own coordinates; it never takes input.
/// </summary>
public sealed class SourceHighlightMark : Control
{
    /// <summary>Height of the line in device-independent pixels.</summary>
    public const double Thickness = 2;

    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<SourceHighlightMark, IBrush?>(nameof(Brush));

    private readonly MarkdownTextBlock _block;
    private readonly IReadOnlyList<TextHighlightRange> _ranges;

    static SourceHighlightMark() => AffectsRender<SourceHighlightMark>(BrushProperty);

    public SourceHighlightMark(MarkdownTextBlock block, IReadOnlyList<TextHighlightRange> ranges, IBrush? brush)
    {
        _block = block;
        _ranges = ranges;
        Brush = brush;
        IsHitTestVisible = false;
        Focusable = false;
    }

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    /// <summary>
    /// Where the lines are drawn, in the text block's coordinates: the bottom edge of each highlighted line box, snapped
    /// to whole device pixels (line boxes often end between pixels, and a line split over two pixel rows looks blurred).
    /// </summary>
    public IReadOnlyList<Rect> LineRects()
    {
        var scale = TopLevel.GetTopLevel(_block)?.RenderScaling ?? 1;
        double Snap(double value) => Math.Round(value * scale) / scale;
        var height = Math.Max(1, Math.Round(Thickness * scale)) / scale;
        var rects = new List<Rect>();
        foreach (var range in _ranges)
        {
            foreach (var line in _block.GetTextRangeBoundsInControl(range.Start, range.Length))
            {
                var (left, right, bottom) = (Snap(line.Left), Snap(line.Right), Snap(line.Bottom));
                if (right > left)
                {
                    rects.Add(new Rect(left, bottom - height, right - left, height));
                }
            }
        }

        return rects;
    }

    public override void Render(DrawingContext context)
    {
        if (Brush is not { } brush)
        {
            return;
        }

        foreach (var rect in LineRects())
        {
            context.DrawRectangle(brush, null, new RoundedRect(rect, Thickness / 2));
        }
    }

    // The adorner layer arranges this over the block again whenever the block moves or is laid out anew (a narrower
    // window rewraps its text), so the lines are measured again then.
    protected override Size ArrangeOverride(Size finalSize)
    {
        InvalidateVisual();
        return base.ArrangeOverride(finalSize);
    }
}
