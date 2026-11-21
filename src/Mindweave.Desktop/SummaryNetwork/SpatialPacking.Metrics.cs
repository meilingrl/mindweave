using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// The acceptance checks of docs/design/session-network.md §6.4. <see cref="Measure"/> reports them for the tests and
/// the trial shots, and the choice between candidate arrangements (<c>Score</c>) works them out with the same code
/// (<see cref="Check"/>), so the objective the layout is chosen by and the gates it is held to cannot drift apart.
/// </summary>
public static partial class SpatialPacking
{
    /// <summary>
    /// One arrangement as the checks see it, dot by dot: dot centres, each footprint's offset to the right of its
    /// dot and its radius, the mass (the weight w), the dot's drawn radius, and the index of the parent's dot (-1 for
    /// a root).
    /// </summary>
    private readonly record struct Drawing(double[] X, double[] Y, double[] Offset, double[] Radius, double[] Mass, double[] DotRadius, int[] Parent)
    {
        public int Count => X.Length;
    }

    /// <summary>
    /// The whole-graph checks: the circle around every footprint, the ideal radius √(Σ r²), the distance from the
    /// mass-weighted centroid of the dots to that circle's centre as a fraction of its radius, the number of
    /// overlapping footprint pairs, branch lines, pairs of lines that cross, and (when asked) lines through a dot.
    /// </summary>
    private readonly record struct Whole(Circle Enclosing, double Ideal, double CentroidOffset, int Overlaps, int Edges, int Crossings, int ThroughDots);

    /// <summary>The §6.4 metrics for <paramref name="dots"/>; nodes without a dot are left out.</summary>
    public static PackingReport Measure(IReadOnlyList<PackNode> nodes, IReadOnlyDictionary<SummaryNodeId, NodePosition> dots)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(dots);
        var placed = nodes.Where(n => dots.ContainsKey(n.Id)).DistinctBy(n => n.Id).ToList();
        if (placed.Count == 0)
        {
            return new PackingReport(0, 0, 0, 0, 0);
        }

        var index = new Dictionary<SummaryNodeId, int>(placed.Count);
        for (var k = 0; k < placed.Count; k++)
        {
            index[placed[k].Id] = k;
        }

        var n = placed.Count;
        var drawing = new Drawing(new double[n], new double[n], new double[n], new double[n], new double[n], new double[n], new int[n]);
        for (var k = 0; k < n; k++)
        {
            var node = placed[k];
            (drawing.X[k], drawing.Y[k]) = (dots[node.Id].X, dots[node.Id].Y);
            (drawing.Offset[k], drawing.Radius[k]) = Footprint(node);
            (drawing.Mass[k], drawing.DotRadius[k]) = (node.Mass, node.DotRadius);
            drawing.Parent[k] = node.Parent is { } p && index.TryGetValue(p, out var at) ? at : -1;
        }

        var whole = Check(drawing, throughDots: true);
        var roots = Enumerable.Range(0, n).Where(k => drawing.Parent[k] < 0).OrderBy(k => placed[k].Order).ThenBy(k => placed[k].Id.Value).ToList();
        var correlation = Math.Abs(Spearman(roots.Select((_, i) => (double)i).ToList(), roots.Select(k => drawing.X[k]).ToList()));
        return new PackingReport(whole.Enclosing.R, whole.Ideal, whole.CentroidOffset, correlation, whole.Overlaps, whole.Edges, whole.Crossings, whole.ThroughDots);
    }

    /// <summary>The whole-graph checks for a drawing; lines through dots only when <paramref name="throughDots"/>.</summary>
    private static Whole Check(Drawing drawing, bool throughDots)
    {
        var n = drawing.Count;
        var (fx, fy) = (new double[n], drawing.Y);
        for (var k = 0; k < n; k++)
        {
            fx[k] = drawing.X[k] + drawing.Offset[k];
        }

        var enclosing = Enclose(drawing.Radius, fx, fy);
        var ideal = Math.Sqrt(drawing.Radius.Sum(r => r * r));
        var (gx, gy) = Centroid(drawing);
        var offset = enclosing.R > 0 ? Math.Sqrt(Square(gx - enclosing.X) + Square(gy - enclosing.Y)) / enclosing.R : 0;
        var (edges, crossings) = Crossings(drawing);
        return new Whole(enclosing, ideal, offset, Overlaps(drawing.Radius, fx, fy), edges, crossings, throughDots ? ThroughDots(drawing) : 0);
    }

    /// <summary>
    /// The mass-weighted centroid of the dots, a negative mass counting as none; without any mass every dot weighs the
    /// same.
    /// </summary>
    private static (double X, double Y) Centroid(Drawing drawing)
    {
        var n = drawing.Count;
        var total = drawing.Mass.Sum(m => Math.Max(0, m));
        var (gx, gy) = (0.0, 0.0);
        for (var k = 0; k < n; k++)
        {
            var weight = total > 0 ? Math.Max(0, drawing.Mass[k]) / total : 1.0 / n;
            gx += weight * drawing.X[k];
            gy += weight * drawing.Y[k];
        }

        return (gx, gy);
    }

    /// <summary>
    /// Pairs of footprints that overlap by more than a hundredth of a unit. A sweep along x: two circles can overlap
    /// only if their centres lie closer in x than the larger reach.
    /// </summary>
    private static int Overlaps(double[] r, double[] x, double[] y)
    {
        var order = Enumerable.Range(0, r.Length).OrderBy(k => x[k]).ToArray();
        var largest = r.Length > 0 ? r.Max() : 0;
        var overlaps = 0;
        for (var a = 0; a < order.Length; a++)
        {
            var i = order[a];
            for (var b = a + 1; b < order.Length && x[order[b]] - x[i] < r[i] + largest; b++)
            {
                var j = order[b];
                var reach = r[i] + r[j] - 0.01;
                if (reach * reach > Square(x[i] - x[j]) + Square(y[i] - y[j]))
                {
                    overlaps++;
                }
            }
        }

        return overlaps;
    }

    /// <summary>
    /// The branch lines as the canvas draws them, dot to dot, and how many pairs of them cross; lines that share a
    /// dot do not count. A sweep along x: two lines can cross only if their spans in x overlap.
    /// </summary>
    private static (int Edges, int Crossings) Crossings(Drawing drawing)
    {
        var (x, y, parent) = (drawing.X, drawing.Y, drawing.Parent);
        var lines = Enumerable.Range(0, drawing.Count).Where(k => parent[k] >= 0).OrderBy(k => Math.Min(x[k], x[parent[k]])).ToArray();
        var crossings = 0;
        for (var a = 0; a < lines.Length; a++)
        {
            var (c, p) = (lines[a], parent[lines[a]]);
            var right = Math.Max(x[c], x[p]);
            for (var b = a + 1; b < lines.Length; b++)
            {
                var (e, q) = (lines[b], parent[lines[b]]);
                if (Math.Min(x[e], x[q]) > right)
                {
                    break;
                }

                if (e != p && q != p && q != c && Crosses(x[p], y[p], x[c], y[c], x[q], y[q], x[e], y[e]))
                {
                    crossings++;
                }
            }
        }

        return (lines.Length, crossings);
    }

    /// <summary>Branch lines that pass through a dot other than their own two.</summary>
    private static int ThroughDots(Drawing drawing)
    {
        var (x, y, parent) = (drawing.X, drawing.Y, drawing.Parent);
        var through = 0;
        for (var c = 0; c < drawing.Count; c++)
        {
            var p = parent[c];
            if (p < 0)
            {
                continue;
            }

            for (var m = 0; m < drawing.Count; m++)
            {
                if (m != c && m != p && DistanceToSegment(x[m], y[m], x[p], y[p], x[c], y[c]) < drawing.DotRadius[m])
                {
                    through++;
                    break;
                }
            }
        }

        return through;
    }

    /// <summary>
    /// Whether the children of a node, if three or more, lie clockwise in creation order round (<paramref name="px"/>,
    /// <paramref name="py"/>): each child's direction, measured clockwise from the first child's, grows. Children
    /// whose place <paramref name="centres"/> does not know yet are passed over. One check for every use: the
    /// choice, the pull-in after a wedge fill, and the search for each dot's place in it.
    /// </summary>
    private static bool ClockwiseInOrder<TCentres>(List<int> children, double px, double py, TCentres centres)
        where TCentres : struct, ICentres
    {
        if (children.Count < 3)
        {
            return true;
        }

        double? first = null;
        var previous = -1.0;
        foreach (var c in children)
        {
            if (!centres.TryGet(c, out var cx, out var cy))
            {
                continue;
            }

            var angle = Math.Atan2(cy - py, cx - px);
            first ??= angle;
            var turned = Turned(angle - first.Value);
            if (previous >= 0 && turned <= previous)
            {
                return false;
            }

            previous = turned;
        }

        return true;
    }

    /// <summary>Where a node's footprint centre lies, if it is placed.</summary>
    private interface ICentres
    {
        bool TryGet(int node, out double x, out double y);
    }

    /// <summary>An angle turned into [0, 2π).</summary>
    private static double Turned(double a) => ((a % (2 * Math.PI)) + (2 * Math.PI)) % (2 * Math.PI);

    /// <summary>Whether segments a–b and c–d cross (touching or sharing an end does not count).</summary>
    private static bool Crosses(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy) =>
        Math.Max(ax, bx) >= Math.Min(cx, dx) && Math.Max(cx, dx) >= Math.Min(ax, bx) && Math.Max(ay, by) >= Math.Min(cy, dy) && Math.Max(cy, dy) >= Math.Min(ay, by)
        && Side(ax, ay, bx, by, cx, cy) * Side(ax, ay, bx, by, dx, dy) < 0
        && Side(cx, cy, dx, dy, ax, ay) * Side(cx, cy, dx, dy, bx, by) < 0;

    /// <summary>Which side of the line a → b the point c lies on (the sign), times twice the triangle's area.</summary>
    private static double Side(double ax, double ay, double bx, double by, double cx, double cy) => ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var (dx, dy) = (bx - ax, by - ay);
        var length2 = (dx * dx) + (dy * dy);
        var t = length2 > 0 ? Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / length2, 0, 1) : 0;
        return Math.Sqrt(Square(px - (ax + (t * dx))) + Square(py - (ay + (t * dy))));
    }

    /// <summary>Spearman's rank correlation (average ranks for ties); 0 for fewer than two values or no spread.</summary>
    private static double Spearman(List<double> a, List<double> b)
    {
        if (a.Count < 2)
        {
            return 0;
        }

        var (ra, rb) = (Ranks(a), Ranks(b));
        var (ma, mb) = (ra.Average(), rb.Average());
        double num = 0, da = 0, db = 0;
        for (var i = 0; i < a.Count; i++)
        {
            num += (ra[i] - ma) * (rb[i] - mb);
            da += Square(ra[i] - ma);
            db += Square(rb[i] - mb);
        }

        return da > 0 && db > 0 ? num / Math.Sqrt(da * db) : 0;

        static double[] Ranks(List<double> values)
        {
            var order = Enumerable.Range(0, values.Count).OrderBy(i => values[i]).ToArray();
            var ranks = new double[values.Count];
            for (var start = 0; start < order.Length;)
            {
                var end = start;
                while (end + 1 < order.Length && values[order[end + 1]] == values[order[start]])
                {
                    end++;
                }

                for (var k = start; k <= end; k++)
                {
                    ranks[order[k]] = (start + end) / 2.0;
                }

                start = end + 1;
            }

            return ranks;
        }
    }
}
