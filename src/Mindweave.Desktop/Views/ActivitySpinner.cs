using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Mindweave.Desktop.Views;

/// <summary>
/// A small rotating arc over a faint ring: "working on it" where a line of text would be too much (the sidebar's
/// running marker, S7 round 4). It turns only while it is in a window and effectively visible, one rotation per
/// <see cref="Period"/>, driven by the window's animation frames; nothing runs while it is hidden, collapsed away,
/// scrolled out of its viewport, in a minimised window or detached. With <see cref="Animates"/> off (tests, screenshots)
/// it draws one fixed frame.
/// </summary>
public sealed class ActivitySpinner : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ActivitySpinner, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<ActivitySpinner, double>(nameof(StrokeThickness), 2.0);

    /// <summary>How long one rotation takes.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(0.9);

    /// <summary>The arc's length; the rest of the ring is drawn faintly.</summary>
    private const double SweepDegrees = 100;

    private readonly RotateTransform _rotation = new();
    private readonly InSightFrames _frames;

    static ActivitySpinner()
    {
        AffectsRender<ActivitySpinner>(ForegroundProperty, StrokeThicknessProperty);
    }

    public ActivitySpinner()
    {
        RenderTransform = _rotation;
        RenderTransformOrigin = RelativePoint.Center;
        IsHitTestVisible = false;
        _frames = new InSightFrames(this, () => Animates, OnFrame);
    }

    /// <summary>Off in tests and screenshots, so every frame shows the arc at the same angle.</summary>
    public static bool Animates { get; set; } = true;

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>
    /// In a window that is not minimised, effectively visible and at least partly inside the viewport its scrolling
    /// ancestors leave it: the spinner turns now (when <see cref="Animates"/> is on).
    /// </summary>
    public bool IsTurning => _frames.IsInSight;

    /// <summary>Animation frames delivered to this spinner, turning or not; tests check that none arrive while it is out of sight.</summary>
    internal int FramesReceived => _frames.Received;

    /// <summary>The current rotation in degrees.</summary>
    public double Angle => _rotation.Angle;

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var thickness = StrokeThickness;
        if (Foreground is not { } brush || size <= thickness)
        {
            return;
        }

        var radius = (size - thickness) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        // The ring under the arc, faint enough that the arc reads as the moving part.
        var ring = new Pen(brush, thickness);
        using (context.PushOpacity(0.22))
        {
            context.DrawEllipse(null, ring, center, radius, radius);
        }

        var start = Polar(center, radius, -90);
        var end = Polar(center, radius, -90 + SweepDegrees);
        var arc = new StreamGeometry();
        using (var geometry = arc.Open())
        {
            geometry.BeginFigure(start, isFilled: false);
            geometry.ArcTo(end, new Size(radius, radius), 0, isLargeArc: SweepDegrees > 180, SweepDirection.Clockwise);
            geometry.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(brush, thickness, lineCap: PenLineCap.Round), arc);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _frames.Attached();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _frames.Detached();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _frames.Request();
    }

    private void OnFrame(TimeSpan time) =>
        _rotation.Angle = time.TotalMilliseconds % Period.TotalMilliseconds / Period.TotalMilliseconds * 360;

    private static Point Polar(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }
}
