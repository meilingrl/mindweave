using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mindweave.Desktop.Views;

/// <summary>
/// Three dots weaving while an answer or a summary is being generated (docs/design/generating-indicator.md): the
/// <see cref="Motion"/> says which stage it is in, and a change of motion glides the dots from where they were into the
/// new path over <see cref="WeavingPaths.GlideDuration"/>. Like the sidebar's spinner it moves only while in sight; with
/// <see cref="Animates"/> off (tests, screenshots) it draws one fixed, representative frame of its motion.
/// </summary>
public sealed class WeavingIndicator : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<WeavingIndicator, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<WeavingMotion> MotionProperty =
        AvaloniaProperty.Register<WeavingIndicator, WeavingMotion>(nameof(Motion));

    private readonly InSightFrames _frames;
    private double _seconds;
    private WeavingFrame? _drawn;
    private WeavingFrame? _glideFrom;
    private double? _glideStart;

    static WeavingIndicator()
    {
        AffectsRender<WeavingIndicator>(ForegroundProperty, MotionProperty);
    }

    public WeavingIndicator()
    {
        IsHitTestVisible = false;
        _frames = new InSightFrames(this, () => Animates, OnFrame);
    }

    /// <summary>Off in tests and screenshots, so every capture shows the same frame.</summary>
    public static bool Animates { get; set; } = true;

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public WeavingMotion Motion
    {
        get => GetValue(MotionProperty);
        set => SetValue(MotionProperty, value);
    }

    /// <summary>In sight now, so it moves (when <see cref="Animates"/> is on).</summary>
    public bool IsMoving => _frames.IsInSight;

    /// <summary>Animation frames delivered, moving or not; tests check that none arrive while it is out of sight.</summary>
    internal int FramesReceived => _frames.Received;

    /// <summary>Gliding from the previous motion's dots into this one's.</summary>
    internal bool IsGliding => _glideFrom is not null;

    /// <summary>The frame last drawn.</summary>
    internal WeavingFrame? Drawn => _drawn;

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var frame = CurrentFrame();
        _drawn = frame;
        if (Foreground is not { } brush || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        // The paths are laid out in a 44 × 20 box; any other size scales it evenly, centred.
        var box = WeavingPaths.Size;
        var scale = Math.Min(Bounds.Width / box.Width, Bounds.Height / box.Height);
        var offset = new Vector((Bounds.Width - (box.Width * scale)) / 2, (Bounds.Height - (box.Height * scale)) / 2);
        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset));

        if (frame.ThreadOpacity > 0)
        {
            using (context.PushOpacity(frame.ThreadOpacity))
            {
                context.DrawGeometry(null, new Pen(brush, 1, lineCap: PenLineCap.Round), Thread(frame.Dots, box));
            }
        }

        foreach (var link in frame.Links)
        {
            using (context.PushOpacity(link.Opacity))
            {
                context.DrawLine(new Pen(brush, 1, lineCap: PenLineCap.Round), link.From, link.To);
            }
        }

        // Smaller dots first, so the dot in front of a braid is drawn over the ones behind it.
        foreach (var dot in frame.Dots.OrderBy(d => d.Radius))
        {
            using (context.PushOpacity(dot.Opacity))
            {
                context.DrawEllipse(brush, null, dot.Centre, dot.Radius, dot.Radius);
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize) => WeavingPaths.Size;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MotionProperty && Animates && _drawn is not null)
        {
            // From wherever the dots are now, mid-glide included; the glide's clock starts with the next frame.
            _glideFrom = _drawn;
            _glideStart = null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _frames.Attached();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _frames.Detached();
        _glideFrom = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _frames.Request();
    }

    private void OnFrame(TimeSpan time)
    {
        _seconds = time.TotalSeconds;
        if (_glideFrom is not null)
        {
            _glideStart ??= _seconds;
        }

        InvalidateVisual();
    }

    private WeavingFrame CurrentFrame()
    {
        var target = WeavingPaths.At(Motion, Animates ? _seconds : StillSeconds(Motion));
        if (_glideFrom is not { } from)
        {
            return target;
        }

        var progress = _glideStart is { } start ? (_seconds - start) / WeavingPaths.GlideDuration.TotalSeconds : 0;
        if (progress >= 1)
        {
            _glideFrom = null;
            return target;
        }

        return WeavingPaths.Blend(from, target, WeavingPaths.Ease(progress));
    }

    /// <summary>A moment that shows each motion clearly when it is not moving: the braid spread out, joining with its links mostly drawn.</summary>
    private static double StillSeconds(WeavingMotion motion) => motion switch
    {
        WeavingMotion.Joining => WeavingPaths.JoiningCycle.TotalSeconds * 0.6,
        WeavingMotion.Thread => 0.25,
        _ => 0.2,
    };

    /// <summary>A faint thread from the left edge through each dot to the right edge, curving smoothly between them.</summary>
    private static StreamGeometry Thread(IReadOnlyList<WeavingDot> dots, Size box)
    {
        var points = new List<Point> { new(4, box.Height / 2) };
        points.AddRange(dots.Select(d => d.Centre).OrderBy(p => p.X));
        points.Add(new Point(box.Width - 4, box.Height / 2));
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(points[0], isFilled: false);
        for (var i = 1; i < points.Count; i++)
        {
            var (a, b) = (points[i - 1], points[i]);
            var middle = (a.X + b.X) / 2;
            g.CubicBezierTo(new Point(middle, a.Y), new Point(middle, b.Y), b);
        }

        g.EndFigure(isClosed: false);
        return geometry;
    }
}
