using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>The bottom-up part of <see cref="SpatialPacking"/>: the tree, the subtree groups and the root packing.</summary>
public static partial class SpatialPacking
{
    /// <summary>How strongly each sub-branch keeps its shape while its parent's group settles.</summary>
    internal const double GroupStiffness = 0.07;

    /// <summary>
    /// A group is packed as one. A fan is a node with two or more children: its footprint, then its children's discs.
    /// A chain is a node with one child: its footprint, then the run of single children below it, ending with the
    /// disc of the first node that branches again, if any. Once packed, every footprint of the head's subtree settles
    /// about the head (<see cref="Settle"/>). Positions are relative to the head's footprint centre, which stays put.
    /// </summary>
    private sealed class Group(int[] members, double[] x, double[] y, double entry)
    {
        /// <summary>Every node of the head's subtree, the head first.</summary>
        public int[] Members { get; } = members;

        public double[] X { get; } = x;

        public double[] Y { get; } = y;

        /// <summary>
        /// The direction, seen from the disc's centre, that should face the parent: for a fan, the seam between its
        /// last and first child; for a chain, its head.
        /// </summary>
        public double Entry { get; } = entry;

        /// <summary>The disc: the smallest circle around every member's footprint.</summary>
        public Circle Disc { get; set; }

        /// <summary>The mass of every dot in the group, and their mass-weighted centroid.</summary>
        public double Mass { get; set; }

        public double CentroidX { get; set; }

        public double CentroidY { get; set; }
    }

    /// <summary>Builds every group's packing, bottom-up (children before parents, without recursion).</summary>
    private static Dictionary<int, Group> BuildGroups(Forest forest)
    {
        var groups = new Dictionary<int, Group>();
        for (var k = forest.PreOrder.Count - 1; k >= 0; k--)
        {
            var head = forest.PreOrder[k];
            if (!forest.IsHead(head))
            {
                continue;
            }

            var items = new List<int> { head };
            var fan = forest.Children[head].Count >= 2;
            if (fan)
            {
                items.AddRange(forest.Children[head]);
            }
            else
            {
                // A chain: follow single children until a leaf, or a node that branches (a head with its own group).
                for (var at = head; forest.Children[at].Count == 1;)
                {
                    at = forest.Children[at][0];
                    items.Add(at);
                    if (forest.IsHead(at))
                    {
                        break;
                    }
                }
            }

            groups[head] = PackGroup(forest, groups, [.. items], fan);
        }

        return groups;
    }

    /// <summary>
    /// Packs a group's items: a fan's children clockwise around the head in creation order (<see cref="Sweep"/>), a
    /// chain as a spiral around the head. Each nested group is turned so that its entry faces the node it hangs from.
    /// Then the whole subtree settles about the head, and the group is measured.
    /// </summary>
    private static Group PackGroup(Forest forest, Dictionary<int, Group> groups, int[] items, bool fan)
    {
        var n = items.Length;
        var (r, ix, iy) = (new double[n], new double[n], new double[n]);
        for (var j = 0; j < n; j++)
        {
            r[j] = j > 0 && groups.TryGetValue(items[j], out var nested) ? nested.Disc.R : forest.Radius[items[j]];
        }

        double[]? wedges = null;
        if (fan)
        {
            wedges = Sweep(r, ix, iy);
        }
        else
        {
            Pack(r, ix, iy, spiral: true);
        }

        var (members, item, x, y) = (new List<int>(), new List<int>(), new List<double>(), new List<double>());
        for (var j = 0; j < n; j++)
        {
            if (j > 0 && groups.Remove(items[j], out var nested))
            {
                // Turn the nested group about its disc's centre so that its entry faces the node it hangs from.
                var from = Array.IndexOf(items, forest.Parent[items[j]]);
                var turn = Math.Atan2(iy[from] - iy[j], ix[from] - ix[j]) - nested.Entry;
                var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
                for (var m = 0; m < nested.Members.Length; m++)
                {
                    var (px, py) = (nested.X[m] - nested.Disc.X, nested.Y[m] - nested.Disc.Y);
                    members.Add(nested.Members[m]);
                    item.Add(j);
                    x.Add(ix[j] + (cos * px) - (sin * py));
                    y.Add(iy[j] + (sin * px) + (cos * py));
                }
            }
            else
            {
                members.Add(items[j]);
                item.Add(j);
                x.Add(ix[j]);
                y.Add(iy[j]);
            }
        }

        double[] groupX = [.. x], groupY = [.. y];
        Settle(forest, [.. members], [.. item], groupX, groupY, new Settling(GroupCompactionSteps, Thread: false, Anchor: 0, wedges, GroupStiffness));
        var radii = members.Select(i => forest.Radius[i]).ToArray();
        var disc = Enclose(radii, groupX, groupY);
        var entry = fan ? -Math.PI / 2 : Math.Atan2(-disc.Y, -disc.X);
        var group = new Group([.. members], groupX, groupY, entry) { Disc = disc };
        var mass = group.Members.Select(i => Math.Max(0, forest.Nodes[i].Mass)).ToArray();
        group.Mass = mass.Sum();
        var weight = group.Mass > 0 ? group.Mass : group.Members.Length;
        group.CentroidX = group.Members.Select((i, m) => (group.Mass > 0 ? mass[m] : 1) * (group.X[m] - forest.Offset[i])).Sum() / weight;
        group.CentroidY = group.Members.Select((_, m) => (group.Mass > 0 ? mass[m] : 1) * group.Y[m]).Sum() / weight;
        return group;
    }

    /// <summary>
    /// The bodies of a lone tree, by node: each node of the run of single children from the root is a body of its
    /// own, and below it, each child's whole subtree is one body.
    /// </summary>
    private static Dictionary<int, int> SingleRootBodies(Forest forest)
    {
        var top = forest.Roots[0];
        var body = new Dictionary<int, int> { [top] = 0 };
        while (forest.Children[top].Count == 1)
        {
            top = forest.Children[top][0];
            body[top] = body.Count;
        }

        var spine = body.Count;
        for (var c = 0; c < forest.Children[top].Count; c++)
        {
            var stack = new Stack<int>([forest.Children[top][c]]);
            while (stack.TryPop(out var q))
            {
                body[q] = spine + c;
                forest.Children[q].ForEach(stack.Push);
            }
        }

        return body;
    }

    /// <summary>The angle left open between a fan's last and first child, where the line from its parent comes in.</summary>
    internal const double Seam = 0.8;

    /// <summary>
    /// A fan: item 0 (the head) at the origin, the children around it clockwise on screen, from just past north
    /// (half the <see cref="Seam"/>) round to just before it. Each child gets a share of the turn in proportion to the
    /// angle it needs when touching the head, and sits on the ray through the middle of its share as near the head as
    /// it can without overlapping anything placed before it. The children's directions therefore follow creation order
    /// within one turn, however many there are.
    /// </summary>
    /// <returns>The edges of each item's share: item j's runs from wedges[j - 1] to wedges[j] (radians).</returns>
    private static double[] Sweep(double[] r, double[] x, double[] y)
    {
        var n = r.Length;
        var wedges = new double[n];
        var need = new double[n];
        for (var j = 1; j < n; j++)
        {
            need[j] = 2 * Math.Asin(Math.Min(1, r[j] / (r[0] + r[j])));
        }

        var scale = ((2 * Math.PI) - Seam) / need.Sum();
        var angle = (-Math.PI / 2) + (Seam / 2);
        wedges[0] = angle;
        for (var j = 1; j < n; j++)
        {
            var direction = angle + (scale * need[j] / 2);
            angle += scale * need[j];
            wedges[j] = angle;
            var (ux, uy) = (Math.Cos(direction), Math.Sin(direction));
            var distance = 0.0;
            for (var moved = true; moved;)
            {
                moved = false;
                for (var k = 0; k < j; k++)
                {
                    // Distances along the ray at which item j would overlap item k.
                    var along = (ux * x[k]) + (uy * y[k]);
                    var reach = r[j] + r[k];
                    var d = (along * along) - ((x[k] * x[k]) + (y[k] * y[k]) - (reach * reach));
                    if (d <= 0)
                    {
                        continue;
                    }

                    var (low, high) = (along - Math.Sqrt(d), along + Math.Sqrt(d));
                    if (distance > low && distance < high)
                    {
                        distance = high + TouchTolerance;
                        moved = true;
                    }
                }
            }

            (x[j], y[j]) = (distance * ux, distance * uy);
        }

        return wedges;
    }

    /// <summary>
    /// Packs the root discs heaviest first (ties by creation order), each into the place nearest the centre of gravity
    /// of those before it (§6.1 step 3), and returns every footprint centre with the root tree it belongs to.
    /// </summary>
    private static (int[] Members, int[] Body, double[] X, double[] Y) PlaceRoots(Forest forest, Dictionary<int, Group> groups)
    {
        var roots = forest.Roots.OrderByDescending(i => forest.Nodes[i].Mass).ThenBy(i => i).ToArray();
        var n = roots.Length;
        var (r, x, y) = (new double[n], new double[n], new double[n]);
        var (mass, gx, gy) = (new double[n], new double[n], new double[n]);
        for (var k = 0; k < n; k++)
        {
            var group = groups[roots[k]];
            (r[k], mass[k]) = (group.Disc.R, group.Mass);
            (gx[k], gy[k]) = (group.CentroidX - group.Disc.X, group.CentroidY - group.Disc.Y);
        }

        Pack(r, x, y, spiral: false, mass, gx, gy);

        // Each root's tree is one body. With a single root the centroid's pull would then do nothing, so its tree is
        // split one level down instead (see SingleRootBodies).
        var single = n == 1 ? SingleRootBodies(forest) : null;
        var (members, body, fx, fy) = (new List<int>(forest.PreOrder.Count), new List<int>(), new List<double>(), new List<double>());
        for (var k = 0; k < n; k++)
        {
            var group = groups[roots[k]];
            for (var m = 0; m < group.Members.Length; m++)
            {
                members.Add(group.Members[m]);
                body.Add(single is null ? k : single[group.Members[m]]);
                fx.Add(x[k] - group.Disc.X + group.X[m]);
                fy.Add(y[k] - group.Disc.Y + group.Y[m]);
            }
        }

        return ([.. members], [.. body], [.. fx], [.. fy]);
    }

    /// <summary>The nodes in creation order, their tree, and every footprint.</summary>
    private sealed class Forest
    {
        public required PackNode[] Nodes { get; init; }

        public required Dictionary<SummaryNodeId, int> Index { get; init; }

        public required List<int>[] Children { get; init; }

        public required int[] Parent { get; init; }

        public required List<int> Roots { get; init; }

        /// <summary>Every reachable node, each parent before its children.</summary>
        public required List<int> PreOrder { get; init; }

        public required double[] Offset { get; init; }

        public required double[] Radius { get; init; }

        /// <summary>
        /// Whether a node heads a group: a root, or a child among siblings. A single child belongs to its parent's
        /// group, in its chain.
        /// </summary>
        public bool IsHead(int i) => Parent[i] < 0 || Children[Parent[i]].Count >= 2 || Children[i].Count >= 2;

        public static Forest Of(IReadOnlyList<PackNode> input)
        {
            var nodes = input.OrderBy(n => n.Order).ThenBy(n => n.Id.Value).DistinctBy(n => n.Id).ToArray();
            var index = new Dictionary<SummaryNodeId, int>(nodes.Length);
            for (var i = 0; i < nodes.Length; i++)
            {
                index[nodes[i].Id] = i;
            }

            var parent = new int[nodes.Length];
            var children = new List<int>[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                children[i] = [];
            }

            var roots = new List<int>();
            for (var i = 0; i < nodes.Length; i++)
            {
                parent[i] = nodes[i].Parent is { } p && index.TryGetValue(p, out var at) && at != i ? at : -1;
                if (parent[i] < 0)
                {
                    roots.Add(i);
                }
                else
                {
                    children[parent[i]].Add(i);
                }
            }

            var preOrder = new List<int>(nodes.Length);
            var stack = new Stack<int>();
            foreach (var root in roots)
            {
                stack.Push(root);
                while (stack.TryPop(out var i))
                {
                    preOrder.Add(i);
                    for (var c = children[i].Count - 1; c >= 0; c--)
                    {
                        stack.Push(children[i][c]);
                    }
                }
            }

            var (offset, radius) = (new double[nodes.Length], new double[nodes.Length]);
            for (var i = 0; i < nodes.Length; i++)
            {
                (offset[i], radius[i]) = Footprint(nodes[i]);
            }

            return new Forest
            {
                Nodes = nodes,
                Index = index,
                Children = children,
                Parent = parent,
                Roots = roots,
                PreOrder = preOrder,
                Offset = offset,
                Radius = radius,
            };
        }
    }
}
