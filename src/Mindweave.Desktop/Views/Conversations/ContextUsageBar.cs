using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Mindweave.Application.Conversations;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

/// <summary>
/// The context used, as one bar of its parts against the room there is. Each part keeps a sliver and a gap however small, so
/// every row below finds its colour here; the words are the rows'. Every colour is a theme resource.
/// </summary>
public sealed class ContextUsageBar : Control
{
    public static readonly StyledProperty<ContextUsage?> UsageProperty =
        AvaloniaProperty.Register<ContextUsageBar, ContextUsage?>(nameof(Usage));

    /// <summary>The narrowest a part is drawn.</summary>
    public const double MinPart = 3;

    private const double Gap = 1;

    static ContextUsageBar() => AffectsRender<ContextUsageBar>(UsageProperty);

    public ContextUsageBar()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    public ContextUsage? Usage
    {
        get => GetValue(UsageProperty);
        set => SetValue(UsageProperty, value);
    }

    /// <summary>The theme resource a part is drawn with.</summary>
    public static string BrushKeyOf(ContextUsageKind kind) => "ContextPart" + kind;

    /// <summary>The parts' widths on a bar <paramref name="width"/> wide, in the order of the rows; together never wider than the bar.</summary>
    public static IReadOnlyList<(ContextUsageKind Kind, double Start, double Width)> Segments(ContextUsage usage, double width)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var rows = new ContextUsageDetails(usage).Rows;
        if (rows.Count == 0 || width <= 0)
        {
            return [];
        }

        var used = usage.TooLong ? width : width * Math.Clamp(usage.Fraction, 0, 1);
        var total = (double)rows.Sum(r => r.Tokens);
        var widths = rows.Select(r => Math.Max(MinPart, used * r.Tokens / total)).ToList();
        var room = width - (Gap * (rows.Count - 1));
        var scale = Math.Min(1, room / widths.Sum());
        var segments = new List<(ContextUsageKind, double, double)>();
        var x = 0.0;
        for (var i = 0; i < rows.Count; i++)
        {
            segments.Add((rows[i].Kind, x, widths[i] * scale));
            x += (widths[i] * scale) + Gap;
        }

        return segments;
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var radius = Bounds.Height / 2;
        var bar = new Rect(Bounds.Size);
        context.DrawRectangle(Brush("ContextRingTrack"), null, bar, radius, radius);
        if (Usage is not { } usage)
        {
            return;
        }

        using (context.PushClip(new RoundedRect(bar, radius)))
        {
            foreach (var (kind, start, width) in Segments(usage, Bounds.Width))
            {
                context.FillRectangle(Brush(BrushKeyOf(kind)), new Rect(start, 0, width, Bounds.Height));
            }
        }
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;
}

/// <summary>A part's small square in its colour, beside its name.</summary>
public sealed class ContextPartMark : Control
{
    public static readonly StyledProperty<ContextUsageKind> KindProperty =
        AvaloniaProperty.Register<ContextPartMark, ContextUsageKind>(nameof(Kind));

    static ContextPartMark() => AffectsRender<ContextPartMark>(KindProperty);

    public ContextPartMark()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    public ContextUsageKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (this.TryFindResource(ContextUsageBar.BrushKeyOf(Kind), ActualThemeVariant, out var value) && value is IBrush brush)
        {
            context.DrawRectangle(brush, null, new Rect(Bounds.Size), 2, 2);
        }
    }
}
