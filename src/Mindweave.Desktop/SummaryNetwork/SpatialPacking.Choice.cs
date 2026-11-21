namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// Choosing between candidate arrangements (docs/design/session-network.md §6.1, §6.4). No one way of packing does
/// best on every tree: nested packing keeps every sub-branch round but can leave a lone tree lopsided and crossed,
/// wedges fill a lone tree round and keep its lines apart but stretch the sub-branches of a bushy tree. So the view
/// arranges a workspace a few deterministic ways and keeps the one that does best on the acceptance checks.
/// </summary>
public static partial class SpatialPacking
{
    /// <summary>The bound on the enclosing circle's radius, as a multiple of the ideal radius (§6.4).</summary>
    internal const double RatioBound = 1.6;

    /// <summary>The bound on the distance from the centre of gravity to the circle's centre, as a fraction of its radius (§6.4).</summary>
    internal const double CentroidBound = 0.15;

    /// <summary>The bound on crossings per branch line.</summary>
    internal const double CrossingBound = 0.05;

    /// <summary>The bound on a sub-branch's enclosing circle (8 dots or more), as a multiple of its ideal radius.</summary>
    internal const double StretchBound = 2.1;

    /// <summary>A sub-branch beyond this multiple of <see cref="StretchBound"/> is badly stretched; such a candidate is kept only when every one is.</summary>
    internal const double SevereStretch = 1.2;

    /// <summary>Sub-branches of at least this many dots count toward the stretch.</summary>
    internal const int StretchSize = 8;

    /// <summary>With several roots, the stiffer alternative to <see cref="RootStiffness"/>: trees keep their shape and cross less.</summary>
    internal const double StiffRoots = 0.1;

    /// <summary>The most wedge fills tried for a lone tree, each from another starting direction.</summary>
    internal const int WedgeTries = 12;

    /// <summary>Wedge fills tried, times dots: a lone tree of n dots gets <c>WedgeBudget / n</c> tries (at least one).</summary>
    internal const int WedgeBudget = 2000;

    /// <summary>Above this many dots a lone tree is filled in wedges only: nested packing would not fit the time budget.</summary>
    internal const int NestedLimit = 600;

    /// <summary>
    /// A lone tree: nested packing, and wedge fills from a few starting directions, each also filled again with
    /// the wedges aimed at a disc moved toward its centre of gravity. Returns the chosen candidate's footprint centres.
    /// The candidates are independent, so they are worked out in parallel; the choice depends only on their order.
    /// </summary>
    private static (int[] Members, double[] X, double[] Y) LoneTree(Forest forest)
    {
        var area = Areas(forest);
        var tries = Math.Clamp(WedgeBudget / Math.Max(1, forest.Nodes.Length), 1, WedgeTries);
        var nested = forest.Nodes.Length <= NestedLimit ? 1 : 0;
        var candidates = new (int[] Members, double[] X, double[] Y)[nested + (2 * tries)];
        Parallel.For(0, nested + tries, k =>
        {
            if (k < nested)
            {
                candidates[0] = Nest(forest, BuildGroups(forest));
                return;
            }

            var turn = 2 * Math.PI * Spread(k - nested);
            var first = FillWedges(forest, area, turn, LabelLean(forest), 0);
            var (dx, dy, _) = Imbalance(forest, first.Members, first.X, first.Y);
            candidates[nested + (2 * (k - nested))] = first;
            candidates[nested + (2 * (k - nested)) + 1] = FillWedges(forest, area, turn, LabelLean(forest) + dx, dy);
        });

        return Choose(forest, [.. candidates]);
    }

    /// <summary>Several roots: nested packing, settled with the usual and with stiffer trees.</summary>
    private static (int[] Members, double[] X, double[] Y) SeveralTrees(Forest forest)
    {
        if (forest.Roots.Count == 0)
        {
            return ([], [], []);
        }

        var groups = BuildGroups(forest);
        var stiffness = new[] { RootStiffness, StiffRoots };
        var candidates = new (int[] Members, double[] X, double[] Y)[stiffness.Length];
        Parallel.For(0, stiffness.Length, k => candidates[k] = Nest(forest, groups, stiffness[k]));
        return Choose(forest, [.. candidates]);
    }

    /// <summary>The k-th of a sequence spread evenly over [0, 1): 0, 1/2, 1/4, 3/4, 1/8, … (van der Corput, base 2).</summary>
    private static double Spread(int k)
    {
        var (value, unit) = (0.0, 0.5);
        for (; k > 0; k >>= 1, unit /= 2)
        {
            value += (k & 1) * unit;
        }

        return value;
    }

    /// <summary>
    /// The candidate to keep. Candidates that break creation order round a fan go first, and then badly stretched
    /// ones (<see cref="SevereStretch"/>), unless all are.
    /// Then: among those within every bound, the one furthest inside them; else among those within the bounds for
    /// the whole (enclosing circle, centre of gravity, crossings), the one furthest inside its bounds; else the one
    /// least beyond any bound.
    /// </summary>
    private static (int[] Members, double[] X, double[] Y) Choose(Forest forest, List<(int[] Members, double[] X, double[] Y)> candidates)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var scored = candidates.Select(c => (Candidate: c, Score: Score(forest, c.Members, c.X, c.Y))).ToList();
        if (scored.Any(c => c.Score.InOrder))
        {
            scored = [.. scored.Where(c => c.Score.InOrder)];
        }

        if (scored.Any(c => c.Score.Stretch <= SevereStretch))
        {
            scored = [.. scored.Where(c => c.Score.Stretch <= SevereStretch)];
        }

        var within = scored.Where(c => c.Score.Ratio <= 1 && c.Score.Offset <= 1 && c.Score.Crossings <= 1).ToList();
        var all = within.Where(c => c.Score.Stretch <= 1).ToList();
        var pool = all.Count > 0 ? all : within.Count > 0 ? within : scored;
        return pool.MinBy(c => Worst(c.Score)).Candidate;

        static double Worst(Scores s) => Math.Max(Math.Max(s.Ratio, s.Offset), Math.Max(s.Crossings, s.Stretch));
    }

    /// <summary>
    /// An arrangement's acceptance checks, each as a fraction of its bound (1 is on the bound), and whether every
    /// node with three or more children has them clockwise in creation order.
    /// </summary>
    private readonly record struct Scores(double Ratio, double Offset, double Crossings, double Stretch, bool InOrder);

    /// <summary>
    /// The acceptance checks for an arrangement (footprint centres given), each over its bound: the enclosing circle
    /// over the ideal radius, the centre of gravity's offset, crossings per branch line, and the most stretched
    /// sub-branch of <see cref="StretchSize"/> dots or more.
    /// </summary>
    private static Scores Score(Forest forest, int[] members, double[] x, double[] y)
    {
        var n = members.Length;
        var r = members.Select(i => forest.Radius[i]).ToArray();
        var circle = Enclose(r, x, y);
        var ratio = circle.R / Math.Sqrt(r.Sum(v => v * v));

        var (dx, dy, _) = Imbalance(forest, members, x, y);
        var offset = circle.R > 0 ? Math.Sqrt((dx * dx) + (dy * dy)) / circle.R : 0;

        var slot = new int[forest.Nodes.Length];
        var inside = new bool[forest.Nodes.Length];
        var (px, py) = (new double[forest.Nodes.Length], new double[forest.Nodes.Length]);
        for (var k = 0; k < n; k++)
        {
            var i = members[k];
            (slot[i], inside[i]) = (k, true);
            (px[i], py[i]) = (x[k] - forest.Offset[i], y[k]);
        }

        var lines = members.Where(i => forest.Parent[i] >= 0).ToArray();
        var crossings = 0;
        for (var a = 0; a < lines.Length; a++)
        {
            var (c, p) = (lines[a], forest.Parent[lines[a]]);
            for (var b = a + 1; b < lines.Length; b++)
            {
                var (e, q) = (lines[b], forest.Parent[lines[b]]);
                if (e != p && q != p && q != c && Crosses(px[p], py[p], px[c], py[c], px[q], py[q], px[e], py[e]))
                {
                    crossings++;
                }
            }
        }

        // Sub-branches: in pre-order each is a contiguous run.
        var size = new int[forest.Nodes.Length];
        var stretch = 0.0;
        for (var k = forest.PreOrder.Count - 1; k >= 0; k--)
        {
            var i = forest.PreOrder[k];
            size[i]++;
            if (forest.Parent[i] >= 0)
            {
                size[forest.Parent[i]] += size[i];
            }

            if (size[i] < StretchSize || forest.Parent[i] < 0 || !inside[i])
            {
                continue;
            }

            var sub = new Circle[size[i]];
            var area = 0.0;
            for (var m = 0; m < size[i]; m++)
            {
                var j = forest.PreOrder[k + m];
                sub[m] = new Circle(x[slot[j]], y[slot[j]], forest.Radius[j]);
                area += Square(forest.Radius[j]);
            }

            stretch = Math.Max(stretch, Enclose(sub).R / Math.Sqrt(area));
        }

        var perLine = lines.Length > 0 ? (double)crossings / lines.Length : 0;
        var inOrder = members.All(i => InOrder(forest, x, y, slot, i));
        return new Scores(ratio / RatioBound, offset / CentroidBound, perLine / CrossingBound, stretch / StretchBound, inOrder);
    }

    /// <summary>
    /// Whether node <paramref name="p"/>'s children, if three or more, lie clockwise in creation order round it, by
    /// footprint centres (given in <paramref name="x"/>, <paramref name="y"/> at each node's slot).
    /// </summary>
    private static bool InOrder(Forest forest, double[] x, double[] y, int[] slot, int p)
    {
        var children = forest.Children[p];
        if (children.Count < 3)
        {
            return true;
        }

        var (px, py) = (x[slot[p]], y[slot[p]]);
        var first = Math.Atan2(y[slot[children[0]]] - py, x[slot[children[0]]] - px);
        var previous = 0.0;
        for (var k = 1; k < children.Count; k++)
        {
            var c = slot[children[k]];
            var turned = (((Math.Atan2(y[c] - py, x[c] - px) - first) % (2 * Math.PI)) + (2 * Math.PI)) % (2 * Math.PI);
            if (turned <= previous)
            {
                return false;
            }

            previous = turned;
        }

        return true;
    }

    /// <summary>
    /// How far the mass-weighted centroid of the dots lies from the centre of the circle around the footprints
    /// (footprint centres given), and that circle's radius.
    /// </summary>
    private static (double Dx, double Dy, double Radius) Imbalance(Forest forest, int[] members, double[] x, double[] y)
    {
        var circle = Enclose(members.Select(i => forest.Radius[i]).ToArray(), x, y);
        var mass = members.Select(i => Math.Max(0, forest.Nodes[i].Mass)).ToArray();
        var total = mass.Sum();
        var (gx, gy) = (0.0, 0.0);
        for (var k = 0; k < members.Length; k++)
        {
            var weight = total > 0 ? mass[k] / total : 1.0 / members.Length;
            gx += weight * (x[k] - forest.Offset[members[k]]);
            gy += weight * y[k];
        }

        return (gx - circle.X, gy - circle.Y, Math.Max(circle.R, 1));
    }

    /// <summary>Whether segments a–b and c–d cross (touching or sharing an end does not count).</summary>
    private static bool Crosses(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy) =>
        Math.Max(ax, bx) >= Math.Min(cx, dx) && Math.Max(cx, dx) >= Math.Min(ax, bx) && Math.Max(ay, by) >= Math.Min(cy, dy) && Math.Max(cy, dy) >= Math.Min(ay, by)
        && Side(ax, ay, bx, by, cx, cy) * Side(ax, ay, bx, by, dx, dy) < 0
        && Side(cx, cy, dx, dy, ax, ay) * Side(cx, cy, dx, dy, bx, by) < 0;

    /// <summary>Which side of the line a → b the point c lies on (the sign), times twice the triangle's area.</summary>
    private static double Side(double ax, double ay, double bx, double by, double cx, double cy) => ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
}
