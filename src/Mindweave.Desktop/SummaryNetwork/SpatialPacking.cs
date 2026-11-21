using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// One session as the spatial view packs it: its parent (none for a root), its rank by creation time then id, its
/// mass (the weight w: questions asked in it and all its branches), the radius of its dot as drawn (summary ring
/// included) and the reserved width of its label (<see cref="SummaryNetworkLayout.LabelWidth"/>).
/// <paramref name="ThreadNext"/> is the next root on the time thread (roots only).
/// </summary>
public sealed record PackNode(SummaryNodeId Id, SummaryNodeId? Parent, int Order, double Mass, double DotRadius, double LabelWidth, SummaryNodeId? ThreadNext = null);

/// <summary>
/// The acceptance metrics of docs/design/session-network.md §6.4 for one arrangement: the radius of the smallest
/// circle around every footprint; the ideal radius √(Σ footprint areas / π); the distance from the mass-weighted
/// centroid of the dots to that circle's centre, as a fraction of its radius; the absolute rank correlation
/// (Spearman) between the roots' creation order and their x; and the number of overlapping footprint pairs.
/// </summary>
public sealed record PackingReport(double EnclosingRadius, double IdealRadius, double CentroidOffset, double RootTimeRankCorrelation, int Overlaps);

/// <summary>
/// The spatial view (docs/design/session-network.md §6.1): hierarchical, weight-aware circle packing with internal
/// tension. The whole graph settles into as small a circle as it can, centred on its mass-weighted centre of gravity,
/// and the same rule holds inside every sub-branch. Pure and deterministic: inputs sorted by creation order, fixed
/// step counts, no randomness.
/// </summary>
/// <remarks>
/// <para>The steps of §6.1, as built:</para>
/// <list type="number">
/// <item>Footprints (<see cref="FootprintOf"/>): a circle around each dot and its label.</item>
/// <item>
/// Subtree discs, bottom-up: a node's children are packed clockwise in creation order around its footprint by
/// front-chain packing, then every footprint of the subtree settles (<c>Compact</c>), each child's subtree pulled as
/// one body, and the smallest circle around them is the node's disc. A run of single children (a chain) is packed
/// as one spiral, each next to the one before, with the children of the node it ends at around it.
/// </item>
/// <item>Top level: the root discs are packed heaviest first, each into the place nearest the centre of gravity so far.</item>
/// <item>
/// Centre of gravity: in the final settling every root's tree is pulled toward the mass-weighted centroid of all dots
/// (the k-means idea), and the whole is then turned so that centroid meets the enclosing circle's centre.
/// </item>
/// <item>Tension: branch lines and time-thread segments are springs in that settling, which never lets footprints overlap.</item>
/// </list>
/// <para>
/// Measured on the §6.4 metrics, two steps go further than the letter of §6.1. Discs nested rigidly leave empty space
/// at every level, so the enclosing circle reached 3.2 times the ideal radius on deep trees; letting every subtree
/// settle brings it to about 1.25. Moving whole discs toward the centroid was mostly blocked by their neighbours; the
/// pull during settling and the final turn bring the offset from up to 0.37 of the radius to below 0.1.
/// </para>
/// </remarks>
public static partial class SpatialPacking
{
    /// <summary>Height reserved for a label line (the canvas draws it at 13 px, about 17 px tall).</summary>
    public const double LabelHeight = 18;

    /// <summary>How far above (or below) the dot the canvas lifts a label that an edge would cross.</summary>
    public const double LabelLift = 3;

    /// <summary>Room around every footprint: the hover growth of the dot, ring pens, and air between neighbours.</summary>
    public const double FootprintMargin = 6;

    /// <summary>
    /// A node's footprint: the circle around the box of its dot and its label as the canvas draws them (the label to
    /// the right of the dot, at the reserved width, level with the dot or lifted above or dropped below it), centred
    /// on that box, with <see cref="FootprintMargin"/> to spare.
    /// </summary>
    public static (NodePosition Centre, double Radius) FootprintOf(PackNode node, NodePosition dot)
    {
        ArgumentNullException.ThrowIfNull(node);
        var (offset, radius) = Footprint(node);
        return (new NodePosition(dot.X + offset, dot.Y), radius);
    }

    /// <summary>The footprint's centre relative to the dot (to the right only) and its radius.</summary>
    private static (double Offset, double Radius) Footprint(PackNode node)
    {
        var width = (2 * node.DotRadius) + SummaryNetworkLayout.LabelGap + node.LabelWidth;
        var height = 2 * Math.Max(node.DotRadius, LabelHeight + LabelLift);
        return ((SummaryNetworkLayout.LabelGap + node.LabelWidth) / 2, (Math.Sqrt((width * width) + (height * height)) / 2) + FootprintMargin);
    }

    /// <summary>
    /// Arranges every node and returns its dot centre. A node whose parent is not in <paramref name="nodes"/> is a
    /// root; nodes on a parent cycle (which the data cannot hold) are unreachable and left out. The enclosing circle is
    /// centred on the origin.
    /// </summary>
    public static IReadOnlyDictionary<SummaryNodeId, NodePosition> Arrange(IReadOnlyList<PackNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var forest = Forest.Of(nodes);
        var groups = BuildGroups(forest);
        var (members, body, x, y) = PlaceRoots(forest, groups);
        Settle(forest, members, body, x, y, CompactionSteps, thread: true);
        var r = members.Select(i => forest.Radius[i]).ToArray();
        TurnToBalance(forest, members, r, x, y);

        var centre = Enclose(r, x, y);
        var positions = new Dictionary<SummaryNodeId, NodePosition>(members.Length);
        for (var k = 0; k < members.Length; k++)
        {
            var i = members[k];
            positions[forest.Nodes[i].Id] = new NodePosition(x[k] - centre.X - forest.Offset[i], y[k] - centre.Y);
        }

        return positions;
    }

    /// <summary>
    /// Turns the whole arrangement about the enclosing circle's centre C: footprints are circles, so a turn keeps
    /// every distance, and with it the packing and the circle. Only the labels do not turn; they stay to the right of
    /// their dots, so the dots' centroid G sits left of the footprints' by a fixed amount Δ. The turn that points
    /// the footprints' centroid from C along Δ brings G nearest to C.
    /// </summary>
    private static void TurnToBalance(Forest forest, int[] members, double[] r, double[] x, double[] y)
    {
        var total = members.Sum(i => Math.Max(0, forest.Nodes[i].Mass));
        if (total <= 0 || members.Length < 2)
        {
            return;
        }

        var (ux, uy, shift) = (0.0, 0.0, 0.0);
        for (var k = 0; k < members.Length; k++)
        {
            var weight = Math.Max(0, forest.Nodes[members[k]].Mass) / total;
            ux += weight * x[k];
            uy += weight * y[k];
            shift += weight * forest.Offset[members[k]];
        }

        var centre = Enclose(r, x, y);
        (ux, uy) = (ux - centre.X, uy - centre.Y);
        if ((ux * ux) + (uy * uy) <= 0 || shift <= 0)
        {
            return;
        }

        var turn = -Math.Atan2(uy, ux);
        var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
        for (var k = 0; k < members.Length; k++)
        {
            var (dx, dy) = (x[k] - centre.X, y[k] - centre.Y);
            x[k] = centre.X + (dx * cos) - (dy * sin);
            y[k] = centre.Y + (dx * sin) + (dy * cos);
        }
    }

    /// <summary>
    /// Lets the footprints of <paramref name="members"/> (a whole subtree, or everything) settle with
    /// <see cref="Compact"/>: branch lines between members are springs, and with <paramref name="thread"/> so are the
    /// time-thread segments between roots.
    /// </summary>
    private static void Settle(Forest forest, int[] members, int[] body, double[] x, double[] y, int steps, bool thread)
    {
        var n = members.Length;
        if (n < 2)
        {
            return;
        }

        var slot = new Dictionary<int, int>(n);
        for (var k = 0; k < n; k++)
        {
            slot[members[k]] = k;
        }

        var links = new List<(int A, int B, double Spring)>();
        for (var k = 0; k < n; k++)
        {
            var i = members[k];
            if (forest.Parent[i] >= 0 && slot.TryGetValue(forest.Parent[i], out var parent))
            {
                links.Add((parent, k, BranchSpring));
            }
            else if (thread && forest.Nodes[i].ThreadNext is { } next && forest.Index.TryGetValue(next, out var j) && forest.Parent[j] < 0 && slot.TryGetValue(j, out var other) && other != k)
            {
                links.Add((k, other, ThreadSpring));
            }
        }

        var r = members.Select(i => forest.Radius[i]).ToArray();
        var mass = members.Select(i => forest.Nodes[i].Mass).ToArray();
        var offset = members.Select(i => forest.Offset[i]).ToArray();
        Compact(x, y, r, mass, offset, body, links, steps);
    }

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

        var circles = placed.Select(n =>
        {
            var (centre, radius) = FootprintOf(n, dots[n.Id]);
            return new Circle(centre.X, centre.Y, radius);
        }).ToArray();

        var overlaps = 0;
        for (var i = 0; i < circles.Length; i++)
        {
            for (var j = i + 1; j < circles.Length; j++)
            {
                var reach = circles[i].R + circles[j].R - 0.01;
                if (reach * reach > Square(circles[i].X - circles[j].X) + Square(circles[i].Y - circles[j].Y))
                {
                    overlaps++;
                }
            }
        }

        var enclosing = Enclose(circles);
        var ideal = Math.Sqrt(circles.Sum(c => c.R * c.R));
        var total = placed.Sum(n => n.Mass);
        var weights = placed.Select(n => total > 0 ? n.Mass / total : 1.0 / placed.Count).ToList();
        var gx = placed.Select((n, i) => weights[i] * dots[n.Id].X).Sum();
        var gy = placed.Select((n, i) => weights[i] * dots[n.Id].Y).Sum();
        var offset = enclosing.R > 0 ? Math.Sqrt(Square(gx - enclosing.X) + Square(gy - enclosing.Y)) / enclosing.R : 0;

        var known = placed.Select(n => n.Id).ToHashSet();
        var roots = placed.Where(n => n.Parent is not { } p || !known.Contains(p)).OrderBy(n => n.Order).ThenBy(n => n.Id.Value).ToList();
        var correlation = Math.Abs(Spearman(roots.Select((_, i) => (double)i).ToList(), roots.Select(n => dots[n.Id].X).ToList()));

        return new PackingReport(enclosing.R, ideal, offset, correlation, overlaps);
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
