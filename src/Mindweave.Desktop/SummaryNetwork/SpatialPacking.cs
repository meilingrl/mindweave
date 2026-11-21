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
/// <para>
/// A lone tree (every new workspace starts as one) is also filled in wedges round its root (<c>FillWedges</c>,
/// Ruling R-P5): each sub-branch keeps a share of the turn in proportion to its area, clockwise in creation order,
/// so sub-branches cannot mix and together fill a round whole. Nested packing makes every sub-branch round but can
/// leave a lone tree lopsided and crossed; wedges fill it round with lines apart but stretch the sub-branches of a
/// bushy tree. So a lone tree is arranged both ways, the wedges from several starting directions, and several
/// roots are settled with two stiffnesses; the arrangement that does best on the acceptance checks is kept
/// (<c>Choose</c>).
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
        var (members, x, y) = forest.Roots.Count == 1 ? LoneTree(forest) : SeveralTrees(forest);
        var r = members.Select(i => forest.Radius[i]).ToArray();
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
    /// Nested packing: every sub-branch packed round its head, bottom-up; the trees packed heaviest first, settled
    /// together and turned to balance. Returns every footprint centre.
    /// </summary>
    /// <param name="groups">Every tree's packed sub-branches (<see cref="BuildGroups"/>); only read.</param>
    /// <param name="rootStiffness">With several roots, how strongly each tree keeps its shape while they settle.</param>
    private static (int[] Members, double[] X, double[] Y) Nest(Forest forest, Dictionary<int, Group> groups, double rootStiffness = RootStiffness)
    {
        var (members, body, x, y) = PlaceRoots(forest, groups);
        var stiffness = forest.Roots.Count > 1 ? rootStiffness : 0;
        Settle(forest, members, body, x, y, new Settling(CompactionSteps, Thread: true, Anchor: -1, Wedges: null, stiffness));
        TurnToBalance(forest, members, members.Select(i => forest.Radius[i]).ToArray(), x, y);
        return (members, x, y);
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
}
