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
    /// over the ideal radius, the centre of gravity's offset and crossings per branch line, worked out with the same
    /// code as <see cref="Measure"/> (<see cref="Check"/>), and the most stretched sub-branch of
    /// <see cref="StretchSize"/> dots or more.
    /// </summary>
    private static Scores Score(Forest forest, int[] members, double[] x, double[] y)
    {
        var drawing = DrawingOf(forest, members, x, y);
        var whole = Check(drawing, throughDots: false);
        var ratio = whole.Ideal > 0 ? whole.Enclosing.R / whole.Ideal : 0;
        var slot = new int[forest.Nodes.Length];
        var inside = new bool[forest.Nodes.Length];
        for (var k = 0; k < members.Length; k++)
        {
            (slot[members[k]], inside[members[k]]) = (k, true);
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

        var centres = new Slots(slot, x, y);
        var inOrder = members.All(i => ClockwiseInOrder(forest.Children[i], x[slot[i]], y[slot[i]], centres));
        var perLine = whole.Edges > 0 ? (double)whole.Crossings / whole.Edges : 0;
        return new Scores(ratio / RatioBound, whole.CentroidOffset / CentroidBound, perLine / CrossingBound, stretch / StretchBound, inOrder);
    }

    /// <summary>Footprint centres, by node.</summary>
    private readonly struct Slots(int[] slot, double[] x, double[] y) : ICentres
    {
        public bool TryGet(int node, out double cx, out double cy)
        {
            (cx, cy) = (x[slot[node]], y[slot[node]]);
            return true;
        }
    }

    /// <summary>
    /// How far the mass-weighted centroid of the dots lies from the centre of the circle around the footprints
    /// (footprint centres given), and that circle's radius.
    /// </summary>
    private static (double Dx, double Dy, double Radius) Imbalance(Forest forest, int[] members, double[] x, double[] y)
    {
        var circle = Enclose(members.Select(i => forest.Radius[i]).ToArray(), x, y);
        var (gx, gy) = Centroid(DrawingOf(forest, members, x, y));
        return (gx - circle.X, gy - circle.Y, Math.Max(circle.R, 1));
    }

    /// <summary>The drawing of an arrangement of <paramref name="members"/>, given its footprint centres.</summary>
    private static Drawing DrawingOf(Forest forest, int[] members, double[] x, double[] y)
    {
        var n = members.Length;
        var slot = new Dictionary<int, int>(n);
        for (var k = 0; k < n; k++)
        {
            slot[members[k]] = k;
        }

        var drawing = new Drawing(new double[n], y, new double[n], new double[n], new double[n], new double[n], new int[n]);
        for (var k = 0; k < n; k++)
        {
            var i = members[k];
            drawing.X[k] = x[k] - forest.Offset[i];
            (drawing.Offset[k], drawing.Radius[k]) = (forest.Offset[i], forest.Radius[i]);
            (drawing.Mass[k], drawing.DotRadius[k]) = (forest.Nodes[i].Mass, forest.Nodes[i].DotRadius);
            drawing.Parent[k] = forest.Parent[i] >= 0 && slot.TryGetValue(forest.Parent[i], out var at) ? at : -1;
        }

        return drawing;
    }
}
