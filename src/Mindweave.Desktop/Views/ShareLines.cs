using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views.Pages;

namespace Mindweave.Desktop.Views;

/// <summary>The lines of the share dialog's timeline, drawn as the Mindweave page draws them; what is not shared is faint.</summary>
public sealed class ShareLines : Control
{
    public static readonly StyledProperty<IReadOnlyList<ShareLineView>?> LinesProperty =
        AvaloniaProperty.Register<ShareLines, IReadOnlyList<ShareLineView>?>(nameof(Lines));

    private const double NotShared = 0.35;

    /// <summary>How far a link bows out, so one along a lane does not lie on the lane's own line.</summary>
    private const double Bow = 18;

    private SummaryNetworkPalette? _palette;

    static ShareLines() => AffectsRender<ShareLines>(LinesProperty);

    public ShareLines()
    {
        IsHitTestVisible = false;
        ActualThemeVariantChanged += (_, _) =>
        {
            _palette = null;
            InvalidateVisual();
        };
    }

    public IReadOnlyList<ShareLineView>? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var palette = _palette ??= SummaryNetworkPalette.From(this);
        foreach (var line in Lines ?? [])
        {
            using (context.PushOpacity(line.IsShared ? 1 : NotShared))
            {
                Draw(context, palette, line);
            }
        }
    }

    private static void Draw(DrawingContext context, SummaryNetworkPalette palette, ShareLineView line)
    {
        var (from, to) = (new Point(line.FromX, line.FromY), new Point(line.ToX, line.ToY));
        if (line.Kind == ShareLineKind.Link)
        {
            var length = Math.Max(1, Point.Distance(from, to));
            var control = new Point(
                ((from.X + to.X) / 2) + ((to.Y - from.Y) * 2 * Bow / length),
                ((from.Y + to.Y) / 2) - ((to.X - from.X) * 2 * Bow / length));
            context.DrawGeometry(null, palette.LinkPen(active: false), Figure(from, g => g.QuadraticBezierTo(control, to)));
            return;
        }

        var pen = palette.TimelinePen(EdgeState.Normal, mainLine: line.Kind == ShareLineKind.Main);
        if (Math.Abs(line.FromY - line.ToY) < 0.5)
        {
            context.DrawLine(pen, from, to);
            return;
        }

        // To another lane: leaves the parent and reaches the branch horizontally.
        var middle = (from.X + to.X) / 2;
        context.DrawGeometry(null, pen, Figure(from, g => g.CubicBezierTo(new Point(middle, from.Y), new Point(middle, to.Y), to)));
    }

    private static StreamGeometry Figure(Point from, Action<StreamGeometryContext> to)
    {
        var figure = new StreamGeometry();
        using var g = figure.Open();
        g.BeginFigure(from, false);
        to(g);
        g.EndFigure(false);
        return figure;
    }
}
