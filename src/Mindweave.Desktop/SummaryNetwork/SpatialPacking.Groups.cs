using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>The bottom-up part of <see cref="SpatialPacking"/>: the tree, the subtree groups and the root packing.</summary>
public static partial class SpatialPacking
{
    /// <summary>
    /// A group is packed as one: its head's footprint, the footprints of any run of single children below it (a
    /// chain), then the discs of the children of the node that run ends at. Once packed, every footprint of the head's
    /// subtree settles (<see cref="Settle"/>). Positions are relative to the head's footprint centre as packed.
    /// </summary>
    private sealed class Group(int[] members, double[] x, double[] y)
    {
        /// <summary>Every node of the head's subtree, the head first.</summary>
        public int[] Members { get; } = members;

        public double[] X { get; } = x;

        public double[] Y { get; } = y;

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

            // The head, then any run of single children (a chain), then the children of the node it ends at.
            var items = new List<int> { head };
            var end = head;
            while (forest.Children[end].Count == 1)
            {
                end = forest.Children[end][0];
                items.Add(end);
            }

            if (forest.Children[end].Count >= 2)
            {
                items.AddRange(forest.Children[end]);
            }

            groups[head] = Pack(forest, groups, [.. items]);
        }

        return groups;
    }

    /// <summary>
    /// Packs a group's items (item 0 is the head's footprint; a later item is a nested group's disc, or a chain
    /// node's footprint) clockwise around the head, then settles the whole subtree and measures it.
    /// </summary>
    private static Group Pack(Forest forest, Dictionary<int, Group> groups, int[] items)
    {
        var n = items.Length;
        var (r, ix, iy) = (new double[n], new double[n], new double[n]);
        for (var j = 0; j < n; j++)
        {
            r[j] = j > 0 && groups.TryGetValue(items[j], out var nested) ? nested.Disc.R : forest.Radius[items[j]];
        }

        Pack(r, ix, iy, spiral: true);

        var (members, body, x, y) = (new List<int>(), new List<int>(), new List<double>(), new List<double>());
        for (var j = 0; j < n; j++)
        {
            if (j > 0 && groups.Remove(items[j], out var nested))
            {
                for (var m = 0; m < nested.Members.Length; m++)
                {
                    members.Add(nested.Members[m]);
                    body.Add(j);
                    x.Add(ix[j] - nested.Disc.X + nested.X[m]);
                    y.Add(iy[j] - nested.Disc.Y + nested.Y[m]);
                }
            }
            else
            {
                members.Add(items[j]);
                body.Add(j);
                x.Add(ix[j]);
                y.Add(iy[j]);
            }
        }

        var group = new Group([.. members], [.. x], [.. y]);
        Settle(forest, group.Members, [.. body], group.X, group.Y, GroupCompactionSteps, thread: false);
        group.Disc = Enclose(group.Members.Select(i => forest.Radius[i]).ToArray(), group.X, group.Y);
        var mass = group.Members.Select(i => Math.Max(0, forest.Nodes[i].Mass)).ToArray();
        group.Mass = mass.Sum();
        var weight = group.Mass > 0 ? group.Mass : group.Members.Length;
        group.CentroidX = group.Members.Select((i, m) => (group.Mass > 0 ? mass[m] : 1) * (group.X[m] - forest.Offset[i])).Sum() / weight;
        group.CentroidY = group.Members.Select((_, m) => (group.Mass > 0 ? mass[m] : 1) * group.Y[m]).Sum() / weight;
        return group;
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

        var (members, body, fx, fy) = (new List<int>(forest.PreOrder.Count), new List<int>(), new List<double>(), new List<double>());
        for (var k = 0; k < n; k++)
        {
            var group = groups[roots[k]];
            for (var m = 0; m < group.Members.Length; m++)
            {
                members.Add(group.Members[m]);
                body.Add(k);
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
        public bool IsHead(int i) => Parent[i] < 0 || Children[Parent[i]].Count >= 2;

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
