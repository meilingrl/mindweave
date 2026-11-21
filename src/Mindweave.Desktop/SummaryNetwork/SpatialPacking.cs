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
/// (Spearman) between the roots' creation order and their x; the number of overlapping footprint pairs; the number
/// of branch lines (dot to dot) and of pairs of them that cross; and the number of branch lines that pass through a
/// dot other than their own two.
/// </summary>
public sealed record PackingReport(double EnclosingRadius, double IdealRadius, double CentroidOffset, double RootTimeRankCorrelation, int Overlaps, int BranchEdges = 0, int BranchCrossings = 0, int EdgesThroughDots = 0);

/// <summary>
/// The spatial view (docs/design/session-network.md §6.1): hierarchical, weight-aware circle packing with internal
/// tension. The whole graph settles into as small a circle as it can, centred on its mass-weighted centre of gravity,
/// and the same holds inside every sub-branch: each reads as its own round cluster with its parent inside it. Pure
/// and deterministic: inputs sorted by creation order, fixed step counts, no randomness.
/// </summary>
/// <remarks>
/// <para>The steps of §6.1, as built:</para>
/// <list type="number">
/// <item>Footprints (<see cref="FootprintOf"/>): a circle around each dot and its label.</item>
/// <item>
/// Sub-branches, bottom-up. A node with two or more children is a fan: its children go clockwise in creation order
/// around it, each in a share of the turn (<c>Sweep</c>), leaving a seam open where the line from its own parent
/// will come in. A run of single children is a chain, packed as a spiral. Each nested sub-branch is turned so that
/// its seam (or its head) faces the node it hangs from. Then the whole sub-branch settles about its head
/// (<c>Compact</c>): everything closes in on the head while each child's subtree keeps its shape, its side and its
/// order. The smallest circle around it is its disc.
/// </item>
/// <item>Top level: the root discs are packed heaviest first, each into the place nearest the centre of gravity so far.</item>
/// <item>
/// Centre of gravity: in the final settling every root's tree is pulled as one toward the mass-weighted centroid of
/// all dots (the k-means idea; a lone tree is split one level down so the pull still acts), and the whole is then
/// turned so that centroid meets the enclosing circle's centre.
/// </item>
/// <item>Tension: branch lines and time-thread segments are springs in the settling, which never lets footprints overlap.</item>
/// </list>
/// <para>
/// Measured on the §6.4 metrics and the structure checks of the tests, the settling goes further than the letter of
/// §6.1: discs nested rigidly leave empty space at every level (3.2 × the ideal radius on deep trees, 3.45 on a
/// 1,023-node binary tree), and moving whole discs toward the centroid was blocked by their neighbours.
/// </para>
/// </remarks>
public static partial class SpatialPacking
{
    /// <summary>Height reserved for a label line (the canvas draws it at 13 px, about 17 px tall).</summary>
    internal const double LabelHeight = 18;

    /// <summary>How far above (or below) the dot the canvas lifts a label that an edge would cross.</summary>
    internal const double LabelLift = 3;

    /// <summary>Room around every footprint: the hover growth of the dot, ring pens, and air between neighbours.</summary>
    internal const double FootprintMargin = 6;

    /// <summary>
    /// How strongly each root's tree keeps its shape in the final settling, with several roots. Weak: trees close up
    /// against each other without mixing. A lone tree is split one level down instead and settles without it.
    /// </summary>
    internal const double RootStiffness = 0.015;

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
        var stiffness = forest.Roots.Count > 1 ? RootStiffness : 0;
        Settle(forest, members, body, x, y, new Settling(CompactionSteps, Thread: true, Anchor: -1, Wedges: null, stiffness));
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
    /// <see cref="Compact"/>: branch lines between members are springs, and with <see cref="Settling.Thread"/> so are
    /// the time-thread segments between roots; every node with three or more children keeps their order.
    /// </summary>
    private static void Settle(Forest forest, int[] members, int[] body, double[] x, double[] y, Settling settling)
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
            else if (settling.Thread && forest.Nodes[i].ThreadNext is { } next && forest.Index.TryGetValue(next, out var j) && forest.Parent[j] < 0 && slot.TryGetValue(j, out var other) && other != k)
            {
                links.Add((k, other, ThreadSpring));
            }
        }

        // Every node with three or more children here, with each child's subtree as slots, the child first.
        var fans = new List<(int Parent, int[][] Children)>();
        foreach (var i in members)
        {
            var children = forest.Children[i];
            if (children.Count < 3 || !children.TrueForAll(slot.ContainsKey))
            {
                continue;
            }

            var subtrees = children.Select(c =>
            {
                var list = new List<int>();
                var stack = new Stack<int>([c]);
                while (stack.TryPop(out var q))
                {
                    list.Add(slot[q]);
                    forest.Children[q].ForEach(stack.Push);
                }

                return list.ToArray();
            }).ToArray();
            fans.Add((slot[i], subtrees));
        }

        // The member each body hangs from: the parent of its first member, if that is in another body.
        var hangsFrom = Enumerable.Repeat(-1, body.Max() + 1).ToArray();
        var seen = new bool[hangsFrom.Length];
        for (var k = 0; k < n; k++)
        {
            if (seen[body[k]])
            {
                continue;
            }

            seen[body[k]] = true;
            if (forest.Parent[members[k]] >= 0 && slot.TryGetValue(forest.Parent[members[k]], out var from) && body[from] != body[k])
            {
                hangsFrom[body[k]] = from;
            }
        }

        var r = members.Select(i => forest.Radius[i]).ToArray();
        var mass = members.Select(i => forest.Nodes[i].Mass).ToArray();
        var offset = members.Select(i => forest.Offset[i]).ToArray();
        Compact(x, y, r, mass, offset, body, links, settling, fans, hangsFrom);
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

        var (edges, crossings, throughDots) = Lines(placed, dots, known);
        return new PackingReport(enclosing.R, ideal, offset, correlation, overlaps, edges, crossings, throughDots);
    }

    /// <summary>
    /// The branch lines as the canvas draws them, dot to dot: how many there are, how many pairs of them cross (lines
    /// that share a dot do not count), and how many pass through a dot other than their own two.
    /// </summary>
    private static (int Edges, int Crossings, int ThroughDots) Lines(List<PackNode> placed, IReadOnlyDictionary<SummaryNodeId, NodePosition> dots, HashSet<SummaryNodeId> known)
    {
        var lines = placed.Where(n => n.Parent is { } p && known.Contains(p)).Select(n => (From: n.Parent!.Value, To: n.Id)).ToList();
        var crossings = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            for (var j = i + 1; j < lines.Count; j++)
            {
                var (e, f) = (lines[i], lines[j]);
                if (e.From == f.From || e.From == f.To || e.To == f.From || e.To == f.To)
                {
                    continue;
                }

                var (a, b, c, d) = (dots[e.From], dots[e.To], dots[f.From], dots[f.To]);
                if (Side(a, b, c) * Side(a, b, d) < 0 && Side(c, d, a) * Side(c, d, b) < 0)
                {
                    crossings++;
                }
            }
        }

        var throughDots = 0;
        foreach (var (from, to) in lines)
        {
            var (a, b) = (dots[from], dots[to]);
            if (placed.Any(n => n.Id != from && n.Id != to && DistanceToSegment(dots[n.Id], a, b) < n.DotRadius))
            {
                throughDots++;
            }
        }

        return (lines.Count, crossings, throughDots);

        static double Side(NodePosition a, NodePosition b, NodePosition c) => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
    }

    private static double DistanceToSegment(NodePosition p, NodePosition a, NodePosition b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length2 = (dx * dx) + (dy * dy);
        var t = length2 > 0 ? Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / length2, 0, 1) : 0;
        return Math.Sqrt(Square(p.X - (a.X + (t * dx))) + Square(p.Y - (a.Y + (t * dy))));
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
