using Avalonia;
using Avalonia.Media;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// The network canvas's pure geometry: label size and placement, hit boxes, segment crossing, the decorative pull and
/// fitting the view. No state; the canvas and the tests call the same functions.
/// </summary>
internal static class SummaryNetworkGeometry
{
    /// <summary>Decorative pull towards the pointer: fixed at the sample's default 30% (1.5 px at most), drawing only.</summary>
    internal const double AttractionStrength = 0.3;

    private const double AttractionReach = 90;

    /// <summary>
    /// Label size: <see cref="SummaryNetworkLayout.LabelEm"/> × zoom, at most 13 px. A label is then never wider on screen than
    /// the box the layout reserved for it (its estimated width × zoom), so the no-overlap guarantee holds as drawn.
    /// </summary>
    internal static double LabelSize(double zoom) => Math.Min(SummaryNetworkCanvas.LabelMaxSize, SummaryNetworkLayout.LabelEm * zoom);

    /// <summary>
    /// A node's hit box around its screen centre: from just left of the dot to the end of its label (only the dot when
    /// the label is hidden), clamped to the node's layout box widened by half the layout's spacing. Boxes of nodes the
    /// layout keeps apart therefore never overlap, at any zoom.
    /// </summary>
    internal static Rect HitBox(Point center, NodeView node, double zoom, bool labelVisible, bool sessionNetwork = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        var half = Math.Min(20, SummaryNetworkLayout.NodeHeight * zoom / 2);
        var left = Math.Min(16, (SummaryNetworkLayout.NodeRadius + (SummaryNetworkLayout.LabelPadding / 2)) * zoom);
        var right = labelVisible
            ? Math.Min(
                RadiusOf(node, sessionNetwork) + SummaryNetworkLayout.LabelGap + (node.LabelWidth * LabelSize(zoom) / SummaryNetworkLayout.LabelEm) + 12,
                (SummaryNetworkLayout.NodeRadius + SummaryNetworkLayout.LabelGap + node.LabelWidth + (SummaryNetworkLayout.LabelPadding / 2)) * zoom)
            : Math.Min(RadiusOf(node, sessionNetwork) + 4, (SummaryNetworkLayout.NodeRadius + (SummaryNetworkLayout.LabelPadding / 2)) * zoom);
        return new Rect(center.X - left, center.Y - half, left + right, 2 * half);
    }

    /// <summary>A node's dot radius: by how much was asked in the session network, else larger for an exploration's root.</summary>
    internal static double RadiusOf(NodeView node, bool sessionNetwork = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        return sessionNetwork ? DotRadius(node.Weight) : node.IsRoot ? SummaryNetworkCanvas.RootRadius : SummaryNetworkCanvas.ChildRadius;
    }

    /// <summary>A session's dot radius from its weight (<see cref="SummaryNetworkLayout.DotRadius"/>).</summary>
    internal static double DotRadius(int weight) => SummaryNetworkLayout.DotRadius(weight);

    /// <summary>The dot with its summary ring (<see cref="SummaryNetworkLayout.DotFootprintRadius"/>): the radius the packing keeps clear.</summary>
    internal static double DotFootprintRadius(int weight, bool hasSummary) => SummaryNetworkLayout.DotFootprintRadius(weight, hasSummary);

    /// <summary>
    /// Where a label goes: to the right of the dot, level with it; lifted above (or dropped below) the dot when one of
    /// the node's own edges would run through it, as for a child drawn left of its source, so no edge strikes it out.
    /// </summary>
    internal static Point LabelOrigin(Point center, double radius, Size text, IEnumerable<Point> neighbours)
    {
        ArgumentNullException.ThrowIfNull(neighbours);
        var x = center.X + radius + SummaryNetworkLayout.LabelGap;
        var level = new Point(x, center.Y - 9);
        var others = neighbours.ToList();
        bool Clear(Point origin) => !others.Any(other => Crosses(center, other, new Rect(origin, text).Inflate(new Thickness(2, 0))));
        if (Clear(level))
        {
            return level;
        }

        var above = new Point(x, center.Y - text.Height - 3);
        var below = new Point(x, center.Y + 3);
        return Clear(above) ? above : Clear(below) ? below : level;
    }

    /// <summary>Whether the segment from <paramref name="a"/> to <paramref name="b"/> passes through the rectangle (Liang–Barsky).</summary>
    internal static bool Crosses(Point a, Point b, Rect rect)
    {
        var (t0, t1) = (0.0, 1.0);
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        foreach (var (p, q) in new[] { (-dx, a.X - rect.Left), (dx, rect.Right - a.X), (-dy, a.Y - rect.Top), (dy, rect.Bottom - a.Y) })
        {
            if (p == 0)
            {
                if (q < 0)
                {
                    return false;
                }

                continue;
            }

            var t = q / p;
            if (p < 0)
            {
                t0 = Math.Max(t0, t);
            }
            else
            {
                t1 = Math.Min(t1, t);
            }

            if (t0 > t1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Beside the edge's middle: above a mostly horizontal edge, to the right of a mostly vertical one.</summary>
    internal static Point EdgeLabelOrigin(EdgePaint edge, FormattedText text)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(text);
        var middle = new Point((edge.From.X + edge.To.X) / 2, (edge.From.Y + edge.To.Y) / 2);
        return Math.Abs(edge.To.X - edge.From.X) >= Math.Abs(edge.To.Y - edge.From.Y)
            ? new Point(middle.X - (text.Width / 2), middle.Y - text.Height - 2)
            : new Point(middle.X + 6, middle.Y - (text.Height / 2));
    }

    /// <summary>Where the pull wants a node, in screen px: towards the pointer within 90 px, at most 5 × strength.</summary>
    internal static Vector Attraction(Point node, Point? pointer)
    {
        if (pointer is not { } p)
        {
            return default;
        }

        var delta = p - node;
        var distance = Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y));
        if (distance < 0.5 || distance >= AttractionReach)
        {
            return default;
        }

        var pull = 5 * AttractionStrength * Math.Sin(Math.PI * distance / AttractionReach);
        return delta / distance * pull;
    }

    /// <summary>
    /// The timeline's opening view (spec §5.3): zoom 1 with the main line across the middle. When everything fits at zoom 1
    /// it is centred, as <see cref="FitTo"/> would centre it. Otherwise the newest root (the rightmost on the main line) is
    /// shown with its whole title, the latest history to its left (ruling R-T1a): the title ends at
    /// <see cref="TimelineLabelEndAt"/> of the width, or, when the title alone takes more than
    /// <see cref="TimelineWideLabel"/> of the width, the dot sits at <see cref="TimelineNarrowDotAt"/>. "显示全部" still fits all.
    /// </summary>
    internal static Viewport TimelineOpening(IReadOnlyList<NodeView> nodes, Size size)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        const double left = SummaryNetworkCanvas.RootRadius + SummaryNetworkCanvas.HoverGrowth;
        var minX = nodes.Min(n => n.X);
        var maxX = nodes.Max(n => n.X + n.LabelWidth);
        if (maxX - minX + left + TimelineLabelReach <= size.Width - (2 * Viewport.FitMargin))
        {
            return new Viewport(((minX + maxX) / 2) + ((TimelineLabelReach - left) / 2), 0, 1);
        }

        var newest = nodes.Where(n => n.IsRoot).DefaultIfEmpty(nodes[0]).MaxBy(n => n.X)!;
        var title = TimelineLabelReach + newest.LabelWidth;
        var dotAt = title > size.Width * TimelineWideLabel ? size.Width * TimelineNarrowDotAt : (size.Width * TimelineLabelEndAt) - title;
        return new Viewport(newest.X + (size.Width / 2) - dotAt, 0, 1);
    }

    /// <summary>From a dot's centre to the start of its label at zoom 1, the largest dot grown on hover included.</summary>
    internal const double TimelineLabelReach = SummaryNetworkCanvas.RootRadius + SummaryNetworkCanvas.HoverGrowth + SummaryNetworkLayout.LabelGap;

    /// <summary>Where the newest root's title ends when the timeline opens, as a fraction of the canvas width.</summary>
    internal const double TimelineLabelEndAt = 0.92;

    /// <summary>A title wider than this fraction of the canvas opens with its dot at <see cref="TimelineNarrowDotAt"/>.</summary>
    internal const double TimelineWideLabel = 0.6;

    /// <summary>Where the newest root's dot sits when its title is too wide to end at <see cref="TimelineLabelEndAt"/>.</summary>
    internal const double TimelineNarrowDotAt = 0.2;

    /// <summary>
    /// The viewport that shows every node, its label and its group's caption. Positions and label widths are canvas
    /// units; dots, the label gap and captions are screen px, so the zoom is solved with both (§11.3: zoom clamped,
    /// at most 1.5 when fitting). Recommended nodes are included.
    /// </summary>
    internal static Viewport FitTo(IReadOnlyList<NodeView> nodes, Size size, IReadOnlyList<RecommendedNodeView>? recommended = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        recommended ??= [];
        const double left = SummaryNetworkCanvas.RootRadius + SummaryNetworkCanvas.HoverGrowth;
        const double right = SummaryNetworkCanvas.RootRadius + SummaryNetworkCanvas.HoverGrowth + SummaryNetworkLayout.LabelGap;
        const double top = SummaryNetworkCanvas.CaptionGap + 16;
        const double bottom = SummaryNetworkCanvas.RootRadius + SummaryNetworkCanvas.HoverGrowth + 4;
        // Recommended nodes count too (§10.3.2: 显示全部 shows them), each with its tag and label.
        var minX = nodes.Select(n => n.X).Concat(recommended.Select(r => r.X)).Min();
        var maxX = nodes.Select(n => n.X + n.LabelWidth).Concat(recommended.Select(r => r.X + r.LabelWidth)).Max();
        var minY = nodes.Select(n => n.Y).Concat(recommended.Select(r => r.Y)).Min();
        var maxY = nodes.Select(n => n.Y).Concat(recommended.Select(r => r.Y)).Max();
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        var zoom = Math.Min(1.5, Viewport.Clamp(Math.Min(
            (size.Width - (2 * Viewport.FitMargin) - left - right) / width,
            (size.Height - (2 * Viewport.FitMargin) - top - bottom) / height)));
        return new Viewport(((minX + maxX) / 2) + ((right - left) / (2 * zoom)), ((minY + maxY) / 2) + ((bottom - top) / (2 * zoom)), zoom);
    }

    /// <summary>The smallest zoom at which fitting the whole spatial picture still reads (labels at 80% size).</summary>
    internal const double ReadableZoom = 0.8;

    /// <summary>
    /// The spatial view's opening (spec §6.2): everything fitted when that stays at or above <see cref="ReadableZoom"/>;
    /// otherwise zoom 1 on the mass-weighted centroid of the dots, so a large workspace opens readable and centred, not
    /// as a speck. One session is that dot at the centre, zoom 1. "显示全部" still fits everything.
    /// </summary>
    internal static Viewport SpatialOpening(IReadOnlyList<NodeView> nodes, Size size, IReadOnlyList<RecommendedNodeView>? recommended = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 1)
        {
            return new Viewport(nodes[0].X, nodes[0].Y, 1);
        }

        var fitted = FitTo(nodes, size, recommended);
        if (fitted.Zoom >= ReadableZoom)
        {
            return fitted;
        }

        var mass = nodes.Sum(n => (double)Math.Max(1, n.Weight));
        return new Viewport(
            nodes.Sum(n => n.X * Math.Max(1, n.Weight)) / mass,
            nodes.Sum(n => n.Y * Math.Max(1, n.Weight)) / mass,
            1);
    }
}
