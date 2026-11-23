using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Mindweave.Application.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

/// <summary>How full the conversation's context is, as the ring colours it.</summary>
public enum ContextRingLevel
{
    Normal,

    /// <summary>At or past <see cref="ContextRing.NearFraction"/>: older turns will soon be sent as a summary.</summary>
    Near,

    /// <summary>Even summaries cannot make it fit; the whole ring is drawn.</summary>
    TooLong,
}

/// <summary>
/// The composer's context ring (2026-11-17): a small circular gauge beside send whose arc, clockwise from the top, is
/// the share of the model's context the conversation takes (<see cref="ContextUsage.Fraction"/>). Quiet grey normally,
/// the warning colour near the limit, the danger colour (a full ring) when the conversation no longer fits. It
/// is the first of three levels (the owner, 2026-11-23): its button shows the figures on hover and what they are made of
/// on a press (<see cref="ContextUsageView"/>). Every colour is a theme resource.
/// </summary>
public sealed class ContextRing : Control
{
    public static readonly StyledProperty<ContextUsage?> UsageProperty =
        AvaloniaProperty.Register<ContextRing, ContextUsage?>(nameof(Usage));

    /// <summary>The fraction from which the ring takes the warning colour, as the context sentence says "接近上限".</summary>
    public const double NearFraction = 0.8;

    /// <summary>Some use always shows as at least this much arc, so a short conversation is not an empty ring.</summary>
    public const double MinSweep = 8;

    private const double Thickness = 2.5;

    /// <summary>Every theme resource the ring draws with; both themes must define each one as a brush.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["ContextRingTrack", "ContextRingNormal", "ContextRingNear", "ContextRingTooLong"];

    static ContextRing()
    {
        AffectsRender<ContextRing>(UsageProperty);
    }

    public ContextRing()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    public ContextUsage? Usage
    {
        get => GetValue(UsageProperty);
        set => SetValue(UsageProperty, value);
    }

    public static ContextRingLevel LevelOf(ContextUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return usage.TooLong ? ContextRingLevel.TooLong : usage.Fraction >= NearFraction ? ContextRingLevel.Near : ContextRingLevel.Normal;
    }

    /// <summary>The arc in degrees: the fraction of a full turn, at least <see cref="MinSweep"/> once anything is used; full when too long.</summary>
    public static double SweepOf(ContextUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.TooLong)
        {
            return 360;
        }

        var fraction = Math.Clamp(usage.Fraction, 0, 1);
        return fraction <= 0 || usage.Tokens <= 0 ? 0 : Math.Max(MinSweep, fraction * 360);
    }

    public static string BrushKeyOf(ContextRingLevel level) => level switch
    {
        ContextRingLevel.TooLong => "ContextRingTooLong",
        ContextRingLevel.Near => "ContextRingNear",
        _ => "ContextRingNormal",
    };

    /// <summary>The arc's end point on a circle around <paramref name="centre"/>, <paramref name="degrees"/> clockwise from the top.</summary>
    public static Point PointOnCircle(Point centre, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(centre.X + (radius * Math.Sin(radians)), centre.Y - (radius * Math.Cos(radians)));
    }

    protected override Size MeasureOverride(Size availableSize) => new(18, 18);

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= Thickness * 2)
        {
            return;
        }

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size - Thickness) / 2;
        context.DrawEllipse(null, new Pen(Brush("ContextRingTrack"), Thickness), centre, radius, radius);
        if (Usage is not { } usage)
        {
            return;
        }

        var sweep = SweepOf(usage);
        if (sweep <= 0)
        {
            return;
        }

        var pen = new Pen(Brush(BrushKeyOf(LevelOf(usage))), Thickness, lineCap: PenLineCap.Round);
        if (sweep >= 360)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(PointOnCircle(centre, radius, 0), isFilled: false);
            g.ArcTo(PointOnCircle(centre, radius, sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
            g.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;
}
