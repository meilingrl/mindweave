namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// Choosing between candidate arrangements (docs/design/session-network.md §6.1, §6.4). The acceptance checks are
/// worked out with the same code as <see cref="Measure"/> and are also the order of preference: an arrangement is
/// first (a lone tree) sound, then within the three §6.4 gates or least beyond them, then in creation order round every
/// fan and not badly stretched, and last furthest inside its bounds.
/// </summary>
public static partial class SpatialPacking
{
    /// <summary>The bound on the enclosing circle's radius, as a multiple of the ideal radius (§6.4).</summary>
    internal const double RatioBound = 1.6;

    /// <summary>The bound on the distance from the centre of gravity to the circle's centre, as a fraction of its radius (§6.4).</summary>
    internal const double CentroidBound = 0.15;

    /// <summary>The bound on crossings per branch line of a lone tree (Ruling R-P3), and the scale crossings are ranked on.</summary>
    internal const double CrossingBound = 0.05;

    /// <summary>
    /// The spread no arrangement may pass while another stays within it (Ruling R-P6): the enclosing circle at most
    /// this multiple of the ideal radius.
    /// </summary>
    internal const double SpreadBound = 2.5;

    /// <summary>The bound on a sub-branch's enclosing circle (8 dots or more), as a multiple of its ideal radius.</summary>
    internal const double StretchBound = 2.1;

    /// <summary>A sub-branch beyond this multiple of <see cref="StretchBound"/> is badly stretched; such a candidate is kept only when every one is.</summary>
    internal const double SevereStretch = 1.2;

    /// <summary>Sub-branches of at least this many dots count toward the stretch.</summary>
    internal const int StretchSize = 8;

    /// <summary>
    /// An arrangement's acceptance checks: the enclosing circle over the ideal radius, the centre of gravity's offset
    /// as a fraction of the radius, overlapping footprints, branch lines and their crossings, the most stretched
    /// sub-branch of <see cref="StretchSize"/> dots or more (its circle over its ideal radius), and whether every
    /// node with three or more children has them clockwise in creation order.
    /// </summary>
    internal readonly record struct Checks(double Ratio, double CentroidOffset, int Overlaps, int Edges, int Crossings, double Stretch, bool InOrder)
    {
        public double CrossingsPerLine => Edges > 0 ? (double)Crossings / Edges : 0;
    }

    /// <summary>
    /// The candidate to keep, by its checks. Each step keeps the candidates that pass it, unless none does. For a lone
    /// tree:
    /// <list type="number">
    /// <item>no overlapping footprints;</item>
    /// <item>sound: the enclosing circle within <see cref="SpreadBound"/> (Ruling R-P6);</item>
    /// <item>
    /// within all three §6.4 gates: the enclosing circle within <see cref="RatioBound"/>, the centre of gravity within
    /// <see cref="CentroidBound"/>, crossings within <see cref="CrossingBound"/>; when none is, those least beyond
    /// their worst gate (<see cref="Excess"/>), so that no one gate outweighs the others (Rulings R-P7, R-P11).
    /// </item>
    /// </list>
    /// Then, for a lone tree and for several roots alike (whose ranking starts here, Ruling R-P9):
    /// <list type="number">
    /// <item>every fan in creation order;</item>
    /// <item>no badly stretched sub-branch (<see cref="SevereStretch"/>).</item>
    /// </list>
    /// Last, among those within every bound, the one furthest inside them; else among those within the bounds for
    /// the whole (enclosing circle, centre of gravity, crossings), the one furthest inside its bounds; else the one
    /// least beyond any bound. Ties keep the earlier candidate.
    /// </summary>
    internal static int Pick(IReadOnlyList<Checks> candidates, bool lone)
    {
        var pool = Enumerable.Range(0, candidates.Count).ToList();
        if (lone)
        {
            Keep(c => c.Overlaps == 0);
            Keep(c => c.Ratio <= SpreadBound);
            var least = pool.Min(k => Excess(candidates[k]));
            Keep(c => Excess(c) <= Math.Max(1, least));
        }

        Keep(c => c.InOrder);
        Keep(c => c.Stretch / StretchBound <= SevereStretch);

        var within = pool.Where(k => Excess(candidates[k]) <= 1).ToList();
        var all = within.Where(k => candidates[k].Stretch <= StretchBound).ToList();
        var finalists = all.Count > 0 ? all : within.Count > 0 ? within : pool;
        return finalists.MinBy(k => Worst(candidates[k]));

        void Keep(Func<Checks, bool> passes)
        {
            if (pool.Exists(k => passes(candidates[k])))
            {
                pool = pool.FindAll(k => passes(candidates[k]));
            }
        }

        static double Worst(Checks c) => Math.Max(Excess(c), c.Stretch / StretchBound);
    }

    /// <summary>
    /// How far an arrangement is beyond its worst §6.4 gate, each measured over its bound: the enclosing circle over
    /// <see cref="RatioBound"/>, the centre of gravity over <see cref="CentroidBound"/>, crossings per line over
    /// <see cref="CrossingBound"/>. At most 1 when it is within all three.
    /// </summary>
    private static double Excess(Checks c) =>
        Math.Max(Math.Max(c.Ratio / RatioBound, c.CentroidOffset / CentroidBound), c.CrossingsPerLine / CrossingBound);

    /// <summary>Whether an arrangement is sound: no overlapping footprints, and not spread beyond <see cref="SpreadBound"/>.</summary>
    private static bool Sound(Checks checks) => checks.Overlaps == 0 && checks.Ratio <= SpreadBound;

    /// <summary>
    /// The checks for an arrangement of <paramref name="members"/>: the whole-graph ones with <see cref="Measure"/>'s
    /// code (<see cref="Check"/>), then the stretch of every sub-branch and the creation order round every fan.
    /// </summary>
    private static Checks Score(Forest forest, int[] members, Drawing drawing)
    {
        var whole = Check(drawing, throughDots: false);
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
                var j = slot[forest.PreOrder[k + m]];
                sub[m] = new Circle(drawing.X[j] + drawing.Offset[j], drawing.Y[j], drawing.Radius[j]);
                area += Square(drawing.Radius[j]);
            }

            stretch = Math.Max(stretch, Enclose(sub).R / Math.Sqrt(area));
        }

        var centres = new Slots(slot, drawing);
        var inOrder = members.All(i => ClockwiseInOrder(forest.Children[i], drawing.X[slot[i]] + drawing.Offset[slot[i]], drawing.Y[slot[i]], centres));
        var ratio = whole.Ideal > 0 ? whole.Enclosing.R / whole.Ideal : 0;
        return new Checks(ratio, whole.CentroidOffset, whole.Overlaps, whole.Edges, whole.Crossings, stretch, inOrder);
    }

    /// <summary>Footprint centres of an arrangement's drawing, by node.</summary>
    private readonly struct Slots(int[] slot, Drawing drawing) : ICentres
    {
        public bool TryGet(int node, out double x, out double y)
        {
            var k = slot[node];
            (x, y) = (drawing.X[k] + drawing.Offset[k], drawing.Y[k]);
            return true;
        }
    }

    /// <summary>The drawing of an arrangement of <paramref name="members"/>, given its dot centres.</summary>
    private static Drawing DrawingOf(Forest forest, int[] members, double[] dotX, double[] dotY)
    {
        var n = members.Length;
        var slot = new Dictionary<int, int>(n);
        for (var k = 0; k < n; k++)
        {
            slot[members[k]] = k;
        }

        var drawing = new Drawing(dotX, dotY, new double[n], new double[n], new double[n], new double[n], new int[n]);
        for (var k = 0; k < n; k++)
        {
            var i = members[k];
            (drawing.Offset[k], drawing.Radius[k]) = (forest.Offset[i], forest.Radius[i]);
            (drawing.Mass[k], drawing.DotRadius[k]) = (forest.Nodes[i].Mass, forest.Nodes[i].DotRadius);
            drawing.Parent[k] = forest.Parent[i] >= 0 && slot.TryGetValue(forest.Parent[i], out var at) ? at : -1;
        }

        return drawing;
    }
}
