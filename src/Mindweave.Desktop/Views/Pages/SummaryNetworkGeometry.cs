using Avalonia;
using Avalonia.Media;
using Mindweave.Core.Summaries;
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
    /// layout keeps apart therefore never overlap, at any zoom. A dot keeps its drawn size at every zoom, so the box
    /// always covers the dot and its summary ring (final review M5).
    /// </summary>
    internal static Rect HitBox(Point center, NodeView node, double zoom, bool labelVisible)
    {
        ArgumentNullException.ThrowIfNull(node);
        var half = Math.Min(20, SummaryNetworkLayout.NodeHeight * zoom / 2);
        var left = Math.Min(16, (SummaryNetworkLayout.NodeRadius + (SummaryNetworkLayout.LabelPadding / 2)) * zoom);
        var right = labelVisible
            ? Math.Min(
                DotRadius(node.Weight) + SummaryNetworkLayout.LabelGap + (node.LabelWidth * LabelSize(zoom) / SummaryNetworkLayout.LabelEm) + 12,
                (SummaryNetworkLayout.NodeRadius + SummaryNetworkLayout.LabelGap + node.LabelWidth + (SummaryNetworkLayout.LabelPadding / 2)) * zoom)
            : Math.Min(DotRadius(node.Weight) + 4, (SummaryNetworkLayout.NodeRadius + (SummaryNetworkLayout.LabelPadding / 2)) * zoom);
        var reach = SummaryNetworkLayout.DotFootprintRadius(node.Weight, node.HasSummary);
        if (node.Placed is { } placed)
        {
            // A placed label: the dot and the label's block, drawn at most as large as the layout reserved it.
            var dot = new Rect(center.X - reach, center.Y - reach, 2 * reach, 2 * reach);
            return labelVisible ? dot.Union(PlacedBlock(center, node, placed, zoom, SummaryNetworkCanvas.HoverGrowth)) : dot;
        }

        (half, left, right) = (Math.Max(half, reach), Math.Max(left, reach), Math.Max(right, reach));
        return new Rect(center.X - left, center.Y - half, left + right, 2 * half);
    }

    /// <summary>
    /// A placed label's block on screen: the reserved block scaled to the label's drawn size, beside the dot as drawn. The
    /// layout reserves widths at <see cref="SummaryNetworkLayout.LabelEm"/> a wide character and line heights for
    /// <see cref="SummaryNetworkCanvas.LabelMaxSize"/> text, so each scales by its own ratio; when zoomed far out a label
    /// shows only when focused, at least <see cref="SummaryNetworkCanvas.FocusedLabelMinSize"/>. The drawn text, no larger
    /// and anchored the same way, lies inside it.
    /// </summary>
    internal static Rect PlacedBlock(Point center, NodeView node, SpatialLabel placed, double zoom, double grow = 0)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(placed);
        var size = zoom > SummaryNetworkCanvas.LabelHideScale ? LabelSize(zoom) : Math.Max(LabelSize(zoom), SummaryNetworkCanvas.FocusedLabelMinSize);
        var (w, h) = SpatialLayout.BlockSize(placed.Lines, node.Dated);
        var (width, height) = (w * size / SummaryNetworkLayout.LabelEm, h * size / SummaryNetworkCanvas.LabelMaxSize);
        var reach = SummaryNetworkLayout.DotFootprintRadius(node.Weight, node.HasSummary) + grow;
        var (l, t) = SpatialLayout.LabelOffset(placed.Side, reach, width, height);
        return new Rect(center.X + l, center.Y + t, width, height);
    }

    /// <summary>A dot's radius from its weight (<see cref="SummaryNetworkLayout.DotRadius"/>).</summary>
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
    /// The timeline's opening view (spec §5.3, ruling R-F1). When the whole timeline fits at <see cref="ReadableZoom"/> or
    /// above, it is fitted, at most at zoom 1, as the spatial view opens. Otherwise zoom 1 with the newest root (the
    /// rightmost on the main line) and its whole subtree in view, the latest history to its left: the rightmost label end
    /// of the subtree sits at <see cref="TimelineLabelEndAt"/> of the width, or, when the subtree is wider than that
    /// leaves room for, the root's dot at <see cref="TimelineNarrowDotAt"/>. Label ends are the drawn ones:
    /// <paramref name="drawnWidth"/> is a label's width in px as drawn at <see cref="SummaryNetworkCanvas.LabelMaxSize"/>.
    /// "显示全部" still fits all.
    /// </summary>
    internal static Viewport TimelineOpening(IReadOnlyList<NodeView> nodes, Size size, Func<NodeView, double> drawnWidth)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(drawnWidth);
        var widths = nodes.ToDictionary(n => n.Id, drawnWidth);
        if (TimelineFit(nodes, widths, size) is { } fitted)
        {
            return fitted;
        }

        var newest = nodes.Where(n => n.IsRoot).DefaultIfEmpty(nodes[0]).MaxBy(n => n.X)!;
        var subtree = Subtree(nodes, newest.Id);
        var reach = subtree.Max(n => n.X + TimelineLabelEnd(n, widths[n.Id], 1)) - newest.X;
        var dotAt = Math.Max(size.Width * TimelineNarrowDotAt, (size.Width * TimelineLabelEndAt) - reach);
        return new Viewport(newest.X + (size.Width / 2) - dotAt, (subtree.Min(n => n.Y) + subtree.Max(n => n.Y)) / 2, 1);
    }

    /// <summary>Where the newest subtree's last label ends when the timeline opens, as a fraction of the canvas width.</summary>
    internal const double TimelineLabelEndAt = 0.92;

    /// <summary>Where the newest root's dot sits when its subtree is too wide to end at <see cref="TimelineLabelEndAt"/>.</summary>
    internal const double TimelineNarrowDotAt = 0.08;

    /// <summary>Room above the topmost dot for a day caption, and below the lowest for a dot, its hover and a dropped label.</summary>
    private const double TimelineTop = SummaryNetworkCanvas.CaptionGap + 16;
    private const double TimelineBottom = 24;

    /// <summary>From a dot's centre to the end of its drawn label at <paramref name="zoom"/>, in screen px.</summary>
    private static double TimelineLabelEnd(NodeView node, double drawnWidth, double zoom) =>
        DotRadius(node.Weight) + SummaryNetworkLayout.LabelGap + (drawnWidth * LabelSize(zoom) / SummaryNetworkCanvas.LabelMaxSize);

    /// <summary>
    /// The largest zoom from <see cref="ReadableZoom"/> to 1 at which every dot, label and date fits with the margin, the
    /// picture centred; null when it does not fit even at <see cref="ReadableZoom"/>. Wider zooms need more room, so the
    /// zoom is found by halving.
    /// </summary>
    private static Viewport? TimelineFit(IReadOnlyList<NodeView> nodes, Dictionary<SummaryNodeId, double> widths, Size size)
    {
        (double Left, double Right, double Top, double Bottom) Extent(double zoom) => (
            nodes.Min(n => (n.X * zoom) - DotRadius(n.Weight) - SummaryNetworkCanvas.HoverGrowth),
            nodes.Max(n => (n.X * zoom) + TimelineLabelEnd(n, widths[n.Id], zoom)),
            (nodes.Min(n => n.Y) * zoom) - TimelineTop,
            (nodes.Max(n => n.Y) * zoom) + TimelineBottom);

        bool Fits(double zoom)
        {
            var e = Extent(zoom);
            return e.Right - e.Left <= size.Width - (2 * Viewport.FitMargin) && e.Bottom - e.Top <= size.Height - (2 * Viewport.FitMargin);
        }

        double zoom;
        if (Fits(1))
        {
            zoom = 1;
        }
        else if (!Fits(ReadableZoom))
        {
            return null;
        }
        else
        {
            var (low, high) = (ReadableZoom, 1.0);
            for (var i = 0; i < 30; i++)
            {
                var middle = (low + high) / 2;
                (low, high) = Fits(middle) ? (middle, high) : (low, middle);
            }

            zoom = low;
        }

        var fit = Extent(zoom);
        return new Viewport((fit.Left + fit.Right) / (2 * zoom), (fit.Top + fit.Bottom) / (2 * zoom), zoom);
    }

    /// <summary>A node and every node below it by branch parents.</summary>
    private static List<NodeView> Subtree(IReadOnlyList<NodeView> nodes, SummaryNodeId root)
    {
        var children = nodes.Where(n => n.Parent is not null).ToLookup(n => n.Parent!.Value);
        var result = new List<NodeView>();
        var queue = new Queue<NodeView>(nodes.Where(n => n.Id == root));
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            result.Add(node);
            foreach (var child in children[node.Id])
            {
                queue.Enqueue(child);
            }
        }

        return result;
    }

    /// <summary>
    /// The viewport that shows every node, its label and its group's caption. Positions and label widths are canvas
    /// units; dots, the label gap and captions are screen px, so the zoom is solved with both (§11.3: zoom clamped,
    /// at most 1.5 when fitting). Recommended nodes are included.
    /// </summary>
    internal static Viewport FitTo(IReadOnlyList<NodeView> nodes, Size size, IReadOnlyList<RecommendedNodeView>? recommended = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        recommended ??= [];
        const double left = SummaryNetworkLayout.NodeRadius + SummaryNetworkCanvas.HoverGrowth;
        const double right = SummaryNetworkLayout.NodeRadius + SummaryNetworkCanvas.HoverGrowth + SummaryNetworkLayout.LabelGap;
        const double top = SummaryNetworkCanvas.CaptionGap + 16;
        const double bottom = SummaryNetworkLayout.NodeRadius + SummaryNetworkCanvas.HoverGrowth + 4;
        // Recommended nodes count too (§10.3.2: 显示全部 shows them), each with its tag and label.
        // A label the spatial layout placed may lie on any side of its dot: its reserved block counts, in canvas units.
        var blocks = nodes.Select(n => n.Placed is { } p
            ? SpatialLayout.BlockAround(p, SummaryNetworkLayout.DotFootprintRadius(n.Weight, n.HasSummary), n.Dated)
            : (Left: 0, Top: 0, Right: n.LabelWidth, Bottom: 0)).ToList();
        var minX = nodes.Select((n, i) => n.X + blocks[i].Left).Concat(recommended.Select(r => r.X)).Min();
        var maxX = nodes.Select((n, i) => n.X + blocks[i].Right).Concat(recommended.Select(r => r.X + r.LabelWidth)).Max();
        var minY = nodes.Select((n, i) => n.Y + blocks[i].Top).Concat(recommended.Select(r => r.Y)).Min();
        var maxY = nodes.Select((n, i) => n.Y + blocks[i].Bottom).Concat(recommended.Select(r => r.Y)).Max();
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
    /// as a speck. One conversation is that dot at the centre, zoom 1. "显示全部" still fits everything.
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
