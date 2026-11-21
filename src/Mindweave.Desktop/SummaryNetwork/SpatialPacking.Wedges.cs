namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// A lone tree filled in wedges round its root (Ruling R-P5 on docs/design/session-network.md §6.1): each sub-branch
/// keeps its own share of the turn, in proportion to its footprint area and clockwise in creation order, and its dots
/// fill that share out from the root. Sub-branches therefore never mix, their lines stay apart, and together they
/// fill a round whole.
/// </summary>
public static partial class SpatialPacking
{
    /// <summary>
    /// How far left of the root's footprint the disc the wedges aim for is centred, in average label offsets. Labels
    /// lie right of their dots, so a tree packed round its root has its centre of gravity left of its circle's centre;
    /// aiming the wedges at a disc further left puts more of the tree there.
    /// </summary>
    internal const double LabelLeanShare = 1;

    /// <summary>The radius of the disc the wedges aim for, as a multiple of the ideal radius.</summary>
    internal const double AimRadius = 1.45;

    /// <summary>
    /// Fills a lone tree in wedges. The root's footprint is centred on the origin, the apex of every wedge, so its
    /// children's order round it is their order round the origin. Its turn, starting from north plus
    /// <paramref name="turn"/>, is shared out so that shares filled out from the root would make a disc round
    /// (<paramref name="aimX"/>, <paramref name="aimY"/>) (see <see cref="Warp"/>). Every node's share is split among
    /// its children, clockwise in creation order, in proportion to their sub-branches' footprint areas, and the dots
    /// are placed breadth first (see <see cref="Placing"/>). Then the holes are closed (<see cref="PullIn"/>).
    /// Returns every footprint centre.
    /// </summary>
    private static (int[] Members, double[] X, double[] Y) FillWedges(Forest forest, double[] area, double turn, double aimX, double aimY)
    {
        var root = forest.Roots[0];
        var n = forest.Nodes.Length;
        var warp = new Warp(aimX, aimY, AimRadius * Math.Sqrt(forest.Radius.Sum(r => r * r)), (-Math.PI / 2) + turn);
        var state = new Placing(forest);
        var (low, high) = (new double[n], new double[n]);
        (low[root], high[root]) = (0, 1);
        (state.Low[root], state.High[root]) = (warp.Angle(0), warp.Angle(1));
        state.Add(root, -forest.Offset[root], 0);
        var items = new List<int> { root };
        for (var q = 0; q < items.Count; q++)
        {
            var i = items[q];
            var children = forest.Children[i];
            var total = children.Sum(c => area[c]);
            var start = low[i];
            foreach (var c in children)
            {
                low[c] = start;
                start += (high[i] - low[i]) * area[c] / total;
                high[c] = start;
                (state.Low[c], state.High[c]) = (warp.Angle(low[c]), warp.Angle(high[c]));
            }

            // The child whose wedge lies farthest round from the parent's dot goes first: its line crosses only the
            // wedges of siblings still to come, and they then sit beyond it. The root's children go in order.
            var angle = Math.Atan2(state.Y[i], state.X[i]);
            var order = i == root
                ? children
                : [.. children.OrderByDescending(c => Math.Abs(Math.IEEERemainder(((state.Low[c] + state.High[c]) / 2) - angle, 2 * Math.PI))).ThenBy(c => c)];
            foreach (var c in order)
            {
                var (x, y) = state.Place(c);
                state.Add(c, x, y);
                items.Add(c);
            }
        }

        PullIn(forest, items, state.X, state.Y);
        var members = items.ToArray();
        return (members, [.. members.Select(i => state.X[i] + forest.Offset[i])], [.. members.Select(i => state.Y[i])]);
    }

    /// <summary>Where the first wedge fill of a lone tree aims: <see cref="LabelLeanShare"/> average label offsets left of the root's footprint.</summary>
    private static double LabelLean(Forest forest) => -LabelLeanShare * forest.Offset.Average();

    /// <summary>The footprint area of every node's sub-branch.</summary>
    private static double[] Areas(Forest forest)
    {
        var area = new double[forest.Nodes.Length];
        for (var k = forest.PreOrder.Count - 1; k >= 0; k--)
        {
            var i = forest.PreOrder[k];
            area[i] += Square(forest.Radius[i]);
            if (forest.Parent[i] >= 0)
            {
                area[forest.Parent[i]] += area[i];
            }
        }

        return area;
    }

    /// <summary>
    /// Turns a share of the tree's area (0 to 1) into an angle round the root, so that shares filled out from the root
    /// make a disc of the given radius round the given centre: directions in which that disc reaches less far from
    /// the root get more angle for the same area.
    /// </summary>
    private sealed class Warp
    {
        private const int Steps = 360;
        private readonly double[] _share = new double[Steps + 1];
        private readonly double _start;

        public Warp(double cx, double cy, double radius, double start)
        {
            _start = start;
            for (var k = 1; k <= Steps; k++)
            {
                var angle = start + ((k - 0.5) * 2 * Math.PI / Steps);
                var (ux, uy) = (Math.Cos(angle), Math.Sin(angle));
                var along = (ux * cx) + (uy * cy);
                var reach = along + Math.Sqrt(Math.Max(0, (along * along) - ((cx * cx) + (cy * cy) - (radius * radius))));
                _share[k] = _share[k - 1] + (reach * reach);
            }

            for (var k = 1; k <= Steps; k++)
            {
                _share[k] /= _share[Steps];
            }
        }

        public double Angle(double share)
        {
            var k = Array.BinarySearch(_share, share);
            if (k >= 0)
            {
                return _start + (k * 2 * Math.PI / Steps);
            }

            k = ~k;
            if (k == 0)
            {
                return _start;
            }

            if (k > Steps)
            {
                return _start + (2 * Math.PI);
            }

            var t = (share - _share[k - 1]) / (_share[k] - _share[k - 1]);
            return _start + ((k - 1 + t) * 2 * Math.PI / Steps);
        }
    }
}
