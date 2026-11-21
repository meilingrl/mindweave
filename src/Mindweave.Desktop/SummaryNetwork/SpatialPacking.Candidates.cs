namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>A way <see cref="SpatialPacking"/> can arrange a workspace.</summary>
internal enum Arrangement
{
    /// <summary>One dot: nothing to arrange.</summary>
    Single,

    /// <summary>Nested packing, the trees settled together as freely as they go.</summary>
    Nested,

    /// <summary>Nested packing, each tree (a lone tree: each sub-branch below its first fan) keeping its shape more firmly.</summary>
    StiffNested,

    /// <summary>A lone tree filled in wedges round its root.</summary>
    Wedges,

    /// <summary>A lone tree's footprints packed in one spiral, parents before children.</summary>
    Spiral,
}

/// <summary>What <see cref="SpatialPacking.Arrange"/> did: the kinds of arrangement it worked out, and the one it kept with its checks.</summary>
internal sealed record ArrangeTrace(IReadOnlyList<Arrangement> Tried, Arrangement Kept, SpatialPacking.Checks KeptChecks);

/// <summary>
/// The candidate arrangements (docs/design/session-network.md §6.1, §6.4). No one way of packing does best on every
/// tree: nested packing keeps every sub-branch round but can leave a lone tree lopsided and crossed, and its cost grows
/// with the tree's depth; wedges fill a lone tree round and keep its lines apart but stretch the sub-branches of a bushy
/// tree; a spiral of every footprint is round and cheap whatever the shape, but keeps no sub-branch together. So a
/// workspace is arranged a few deterministic ways, within a fixed amount of work, and the best is kept
/// (<see cref="Pick"/>).
/// </summary>
public static partial class SpatialPacking
{
    /// <summary>With several roots, the stiffer alternative to <see cref="RootStiffness"/>: trees keep their shape and cross less.</summary>
    internal const double StiffRoots = 0.1;

    /// <summary>
    /// For a lone tree, how strongly each sub-branch below the first fan keeps its shape in the stiffer nested
    /// arrangement: firm enough that sub-branches seldom cross into each other, loose enough to close up.
    /// </summary>
    internal const double StiffSubBranches = 0.02;

    /// <summary>The settling steps of the stiffer nested arrangement of a lone tree.</summary>
    internal const int StiffSteps = 200;

    /// <summary>The most wedge fills tried for a lone tree, each from another starting direction.</summary>
    internal const int WedgeTries = 12;

    /// <summary>Wedge fills tried, times dots: a lone tree of n dots gets <c>WedgeBudget / n</c> tries (at least one, at most n).</summary>
    internal const int WedgeBudget = 2000;

    /// <summary>
    /// The most work one wedge task may take, in pairs of dots: placing each dot of a fill looks at every dot and line
    /// placed before it, so a fill of n dots costs about n² (the creation-order check round a fan is constant per
    /// place, so a big fan adds nothing). A task fills once and, within this, fills again aimed at the centre of
    /// gravity. This is work for one core, not time: measured at 13 to 52 ns a pair (stars cheapest, brooms dearest),
    /// so up to about 0.2 s for a task. The tasks run side by side, so the time to arrange a tree is about its
    /// slowest task's where there are cores enough, and the sum of them on one core.
    /// </summary>
    internal const long WedgeWork = 4_000_000;

    /// <summary>
    /// The most work nested packing may take, in footprint moves: each group settles every footprint of its
    /// sub-branch <see cref="GroupCompactionSteps"/> times, and the whole is settled <see cref="CompactionSteps"/>
    /// times (<see cref="NestingWork"/>). A deep tree costs far more than a bushy one of the same size; beyond this a
    /// lone tree is arranged without it. This is work for one core, not time: measured at 160 to 410 ns a move, so
    /// up to about 0.2 s, run beside the wedge tasks (see <see cref="WedgeWork"/>).
    /// </summary>
    internal const long NestingBudget = 700_000;

    /// <summary>A candidate arrangement: its dot centres (the enclosing circle centred on the origin) and its checks.</summary>
    private sealed record Candidate(Arrangement Kind, int[] Members, double[] X, double[] Y, Checks Checks, double CentroidX, double CentroidY);

    /// <summary>
    /// A lone tree: nested packing, freely and stiffly settled, if it fits <see cref="NestingBudget"/>; wedge fills
    /// from a few starting directions, each also filled again with the wedges aimed at a disc moved toward its centre
    /// of gravity if that fits <see cref="WedgeWork"/>; and the spiral, last. The candidates are independent, so they
    /// are worked out (and checked) in parallel; their order, and so the choice, does not depend on it.
    /// </summary>
    private static List<Candidate> LoneTree(Forest forest)
    {
        var n = forest.PreOrder.Count;
        var area = Areas(forest);
        var tries = Math.Clamp(Math.Min(WedgeBudget / n, n), 1, WedgeTries);
        var nested = NestingWork(forest) <= NestingBudget ? 2 : 0;
        var fills = 2L * n * n <= WedgeWork ? 2 : 1;
        var lean = LabelLean(forest);
        var groups = new Lazy<Dictionary<int, Group>>(() => BuildGroups(forest));
        var candidates = new Candidate[nested + (fills * tries) + 1];
        Parallel.For(0, nested + tries + 1, k =>
        {
            if (k < nested)
            {
                candidates[k] = k == 0
                    ? Finish(forest, Arrangement.Nested, Nest(forest, groups.Value, 0, CompactionSteps))
                    : Finish(forest, Arrangement.StiffNested, Nest(forest, groups.Value, StiffSubBranches, StiffSteps));
                return;
            }

            if (k == nested + tries)
            {
                candidates[^1] = Spiral(forest);
                return;
            }

            var at = nested + (fills * (k - nested));
            var turn = 2 * Math.PI * Spread(k - nested);
            candidates[at] = Finish(forest, Arrangement.Wedges, FillWedges(forest, area, turn, lean, 0));
            if (fills == 2)
            {
                candidates[at + 1] = Finish(forest, Arrangement.Wedges, FillWedges(forest, area, turn, lean + candidates[at].CentroidX, candidates[at].CentroidY));
            }
        });

        return [.. candidates];
    }

    /// <summary>Several roots: nested packing, settled with the usual and with stiffer trees.</summary>
    private static List<Candidate> SeveralTrees(Forest forest)
    {
        var groups = BuildGroups(forest);
        var stiffness = new[] { RootStiffness, StiffRoots };
        var candidates = new Candidate[stiffness.Length];
        Parallel.For(0, stiffness.Length, k => candidates[k] = Finish(forest, k == 0 ? Arrangement.Nested : Arrangement.StiffNested, Nest(forest, groups, stiffness[k], CompactionSteps)));
        return [.. candidates];
    }

    /// <summary>
    /// The work nested packing takes, in footprint moves (see <see cref="NestingBudget"/>). Every head's group
    /// holds its whole sub-branch, so a chain of fans (a comb) costs the square of its length.
    /// </summary>
    private static long NestingWork(Forest forest)
    {
        var size = new int[forest.Nodes.Length];
        var work = (long)CompactionSteps * forest.PreOrder.Count;
        for (var k = forest.PreOrder.Count - 1; k >= 0; k--)
        {
            var i = forest.PreOrder[k];
            size[i]++;
            if (forest.Parent[i] >= 0)
            {
                size[forest.Parent[i]] += size[i];
            }

            if (forest.IsHead(i))
            {
                work += (long)GroupCompactionSteps * size[i];
            }
        }

        return work;
    }

    /// <summary>
    /// A lone tree's footprints packed in one spiral round the root, each sub-branch after its parent (front-chain
    /// packing in pre-order), and turned to balance. Round and quick whatever the tree's shape, and its lines stay
    /// short where the tree is a long chain with side branches (a comb), but it keeps no sub-branch together and a
    /// bushy tree's lines cross. So it is the last resort (see <see cref="Trace"/>).
    /// </summary>
    private static Candidate Spiral(Forest forest)
    {
        var members = forest.PreOrder.ToArray();
        var r = members.Select(i => forest.Radius[i]).ToArray();
        var (x, y) = (new double[r.Length], new double[r.Length]);
        Pack(r, x, y, spiral: true);
        TurnToBalance(forest, members, r, x, y);
        return Finish(forest, Arrangement.Spiral, (members, x, y));
    }

    /// <summary>Turns footprint centres into dot centres with the enclosing circle centred on the origin, and checks them.</summary>
    private static Candidate Finish(Forest forest, Arrangement kind, (int[] Members, double[] X, double[] Y) arrangement) =>
        Finish(forest, kind, arrangement.Members, arrangement.X, arrangement.Y);

    private static Candidate Finish(Forest forest, Arrangement kind, int[] members, double[] x, double[] y)
    {
        var centre = Enclose([.. members.Select(i => forest.Radius[i])], x, y);
        var (dotX, dotY) = (new double[members.Length], new double[members.Length]);
        for (var k = 0; k < members.Length; k++)
        {
            dotX[k] = x[k] - centre.X - forest.Offset[members[k]];
            dotY[k] = y[k] - centre.Y;
        }

        var drawing = DrawingOf(forest, members, dotX, dotY);
        var (gx, gy) = Centroid(drawing);
        return new Candidate(kind, members, dotX, dotY, Score(forest, members, drawing), gx, gy);
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
}
