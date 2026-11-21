namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// The internal tension at footprint level (docs/design/session-network.md §6.1 steps 4 and 5, carried down to every
/// dot). Nested discs leave empty space at every level, and it compounds with depth. This relaxation lets a subtree,
/// or the whole graph, settle into a smaller circle, while branch lines hold every sub-branch next to its parent,
/// the sub-branches keep their shape, side and order, and footprints never overlap.
/// </summary>
public static partial class SpatialPacking
{
    /// <summary>Relaxation steps for the whole graph.</summary>
    internal const int CompactionSteps = 300;

    /// <summary>Relaxation steps inside each group, whose nested groups have settled already.</summary>
    internal const int GroupCompactionSteps = 100;

    /// <summary>The last part of the steps, in which gravity and springs have cooled to nothing and only overlaps are resolved.</summary>
    internal const double CoolDown = 0.25;

    /// <summary>Pull toward the centre (a group's head, or the centre of gravity), as a fraction of the distance, per step.</summary>
    internal const double Gravity = 0.04;

    /// <summary>Longest pull per step, as a fraction of the (smallest) footprint radius moved, so far parts close in gradually.</summary>
    internal const double StepCap = 1;

    /// <summary>Spring along a branch line, on the part of its length beyond touching.</summary>
    internal const double BranchSpring = 0.3;

    /// <summary>Spring along the time thread between consecutive roots: weak, so it shortens the thread without lining roots up.</summary>
    internal const double ThreadSpring = 0.02;

    /// <summary>Collision passes per step; the last step may take <see cref="FinalPasses"/>.</summary>
    internal const int CollisionPasses = 10;

    /// <summary>Collision passes the last step may take to end without overlap.</summary>
    internal const int FinalPasses = 400;

    /// <summary>Overlapping footprints are pushed this far apart, a little past touching, so a pile clears in fewer passes.</summary>
    private const double PushGap = 2;

    /// <summary>The collision weight of an anchor: it never moves.</summary>
    private const double Immovable = 1e30;

    /// <summary>
    /// How one settling runs. <paramref name="Anchor"/> is the member everything is pulled toward (a group's head),
    /// or -1 to pull every body toward the centre of gravity. <paramref name="Wedges"/> are a fan's shares of the
    /// turn (see <see cref="Sweep"/>). <paramref name="Stiffness"/> is how strongly each body keeps its shape.
    /// </summary>
    private sealed record Settling(int Steps, bool Thread, int Anchor, double[]? Wedges, double Stiffness);

    /// <summary>
    /// Settles the footprints (centres <paramref name="x"/>, <paramref name="y"/>). Every footprint belongs to a body
    /// (one item of a group, or a root's whole tree). Each step:
    /// <list type="bullet">
    /// <item>
    /// pulls toward the centre: in a group every footprint toward the head, which stays put, so the subtree shrinks
    /// about it; for the whole graph every body as one toward the mass-weighted centroid of all dots;
    /// </item>
    /// <item>shortens every link by its spring;</item>
    /// <item>
    /// keeps each body near its packed shape (<see cref="Frames.Match"/>), turned toward the node it hangs from
    /// (<see cref="Frames.KeepFacing"/>), a fan's children in creation order (<see cref="KeepFanOrder"/>) and each in
    /// its share of the turn (<see cref="KeepInWedges"/>);
    /// </item>
    /// <item>pushes overlapping footprints apart, the smaller one moving more.</item>
    /// </list>
    /// Pull and springs act only after a step that ended without overlap, so the pile never jams, and they cool
    /// linearly to nothing by the last <see cref="CoolDown"/> of the steps. The result is the last step that ended
    /// without overlap; the start has none, so there always is one.
    /// </summary>
    /// <param name="mass">Each footprint's mass (the weight w); without any, the footprints' areas.</param>
    /// <param name="offset">How far right of its dot each footprint's centre lies.</param>
    /// <param name="body">The body each footprint belongs to, numbered from 0.</param>
    /// <param name="links">Branch lines (parent, child) and time-thread segments (root, next root), with their spring.</param>
    /// <param name="fans">Every node with three or more children here, with each child's subtree (the child first).</param>
    /// <param name="hangsFrom">For each body, the member it hangs from, or -1.</param>
    private static void Compact(double[] x, double[] y, double[] r, double[] mass, double[] offset, int[] body, List<(int A, int B, double Spring)> links, Settling settling, List<(int Parent, int[][] Children)> fans, int[] hangsFrom)
    {
        var n = x.Length;
        if (n < 2)
        {
            return;
        }

        var anchor = settling.Anchor;
        var area = r.Select(v => v * v).ToArray();
        var size = (double[])area.Clone();
        if (anchor >= 0)
        {
            size[anchor] = Immovable;
        }

        var heft = mass.Select(m => Math.Max(m, 0)).ToArray();
        var total = heft.Sum();
        if (total <= 0)
        {
            (heft, total) = (area, area.Sum());
        }

        var bodies = body.Max() + 1;
        var (pullX, pullY, bodyHeft, bodyCap) = (new double[bodies], new double[bodies], new double[bodies], new double[bodies]);
        Array.Fill(bodyCap, double.PositiveInfinity);
        for (var i = 0; i < n; i++)
        {
            bodyHeft[body[i]] += heft[i];
            bodyCap[body[i]] = Math.Min(bodyCap[body[i]], r[i]);
        }

        var (bestX, bestY) = ((double[])x.Clone(), (double[])y.Clone());
        var frames = new Frames(x, y, area, body, bodies);
        var contacts = new Contacts(n, 2 * r.Max());
        var clear = true;
        for (var step = 0; step < settling.Steps; step++)
        {
            var cooling = Math.Max(0, 1 - (step / ((1 - CoolDown) * settling.Steps)));
            if (cooling > 0 && clear)
            {
                if (anchor >= 0)
                {
                    PullToAnchor(x, y, r, anchor, cooling);
                }
                else
                {
                    Pull(x, y, offset, body, heft, total, bodyHeft, bodyCap, pullX, pullY, cooling);
                }

                Tighten(x, y, r, size, links, cooling);
            }

            if (settling.Stiffness > 0)
            {
                // In a group, the head's body (0) is the anchor and needs no shape.
                frames.Match(x, y, settling.Stiffness, skipFirst: anchor >= 0);
            }

            frames.KeepFacing(x, y, hangsFrom);
            KeepFanOrder(x, y, fans);
            if (settling.Wedges is { } wedges && anchor >= 0)
            {
                KeepInWedges(x, y, body, anchor, wedges);
            }

            var last = step == settling.Steps - 1;
            clear = contacts.Separate(x, y, r, size, last ? FinalPasses : CollisionPasses);
            for (var round = 0; last && clear && round < FanOrderRounds && KeepFanOrder(x, y, fans); round++)
            {
                clear = contacts.Separate(x, y, r, size, FinalPasses);
            }

            if (clear)
            {
                Array.Copy(x, bestX, n);
                Array.Copy(y, bestY, n);
            }
        }

        Array.Copy(bestX, x, n);
        Array.Copy(bestY, y, n);
    }

    /// <summary>How many times the last step may restore a fan's order and clear overlaps again.</summary>
    private const int FanOrderRounds = 8;

    /// <summary>Shortens every link beyond touching by its spring, the smaller footprint moving more.</summary>
    private static void Tighten(double[] x, double[] y, double[] r, double[] size, List<(int A, int B, double Spring)> links, double cooling)
    {
        foreach (var (a, b, spring) in links)
        {
            var (dx, dy) = (x[b] - x[a], y[b] - y[a]);
            var d = Math.Sqrt((dx * dx) + (dy * dy));
            var slack = d - r[a] - r[b];
            if (slack <= 0)
            {
                continue;
            }

            var tighten = spring * cooling * slack / d;
            var share = size[a] / (size[a] + size[b]);
            x[b] -= tighten * dx * share;
            y[b] -= tighten * dy * share;
            x[a] += tighten * dx * (1 - share);
            y[a] += tighten * dy * (1 - share);
        }
    }

    /// <summary>
    /// Moves every footprint but the anchor toward the anchor by <see cref="Gravity"/> of its distance (at most
    /// <see cref="StepCap"/> of its radius): the subtree shrinks evenly about its head, keeping every direction, and
    /// collisions let it flow into the gaps.
    /// </summary>
    private static void PullToAnchor(double[] x, double[] y, double[] r, int anchor, double cooling)
    {
        var (ax, ay) = (x[anchor], y[anchor]);
        for (var i = 0; i < x.Length; i++)
        {
            if (i == anchor)
            {
                continue;
            }

            var (mx, my) = (Gravity * cooling * (ax - x[i]), Gravity * cooling * (ay - y[i]));
            var length = Math.Sqrt((mx * mx) + (my * my));
            var cap = StepCap * cooling * r[i];
            var scale = length > cap ? cap / length : 1;
            x[i] += mx * scale;
            y[i] += my * scale;
        }
    }

    /// <summary>Moves every body as one toward the mass-weighted centroid of all dots (see <see cref="Compact"/>).</summary>
    private static void Pull(double[] x, double[] y, double[] offset, int[] body, double[] heft, double total, double[] bodyHeft, double[] bodyCap, double[] pullX, double[] pullY, double cooling)
    {
        var (gx, gy) = (0.0, 0.0);
        Array.Clear(pullX);
        Array.Clear(pullY);
        for (var i = 0; i < x.Length; i++)
        {
            gx += heft[i] * (x[i] - offset[i]);
            gy += heft[i] * y[i];
            pullX[body[i]] += heft[i] * (x[i] - offset[i]);
            pullY[body[i]] += heft[i] * y[i];
        }

        (gx, gy) = (gx / total, gy / total);
        for (var b = 0; b < pullX.Length; b++)
        {
            var (cx, cy) = bodyHeft[b] > 0 ? (pullX[b] / bodyHeft[b], pullY[b] / bodyHeft[b]) : (gx, gy);
            var (mx, my) = (Gravity * cooling * (gx - cx), Gravity * cooling * (gy - cy));
            var length = Math.Sqrt((mx * mx) + (my * my));
            var cap = StepCap * cooling * bodyCap[b];
            var scale = length > cap ? cap / length : 1;
            (pullX[b], pullY[b]) = (mx * scale, my * scale);
        }

        for (var i = 0; i < x.Length; i++)
        {
            x[i] += pullX[body[i]];
            y[i] += pullY[body[i]];
        }
    }

    /// <summary>
    /// Finds footprints that overlap or nearly touch, with a uniform grid of cells as wide as the largest footprint
    /// (hashed into buckets): two footprints that overlap always lie in the same or neighbouring cells.
    /// </summary>
    private sealed class Contacts(int count, double cell)
    {
        /// <summary>Pairs closer than this beyond touching are kept as candidates for a whole step's passes.</summary>
        private readonly double _skin = cell / 8;

        private readonly int[] _head = new int[Math.Max(16, 2 * count)];
        private readonly int[] _next = new int[count];
        private readonly long[] _cellOf = new long[count];
        private readonly List<(int I, int J)> _pairs = [];

        /// <summary>
        /// Pushes overlapping pairs apart until they are <see cref="PushGap"/> apart, the smaller footprint moving
        /// more, for up to <paramref name="passes"/> passes over the candidate pairs. A pass that finds nothing is
        /// confirmed with a fresh look at every pair. Returns whether the footprints end with no overlap.
        /// </summary>
        public bool Separate(double[] x, double[] y, double[] r, double[] size, int passes)
        {
            Collect(x, y, r);
            for (var pass = 0; pass < passes; pass++)
            {
                if (!Push(x, y, r, size))
                {
                    Collect(x, y, r);
                    if (!Push(x, y, r, size))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>One pass over the candidate pairs; returns whether any overlapped.</summary>
        private bool Push(double[] x, double[] y, double[] r, double[] size)
        {
            var any = false;
            foreach (var (i, j) in _pairs)
            {
                var (dx, dy) = (x[j] - x[i], y[j] - y[i]);
                var reach = r[i] + r[j];
                var d2 = (dx * dx) + (dy * dy);
                if (d2 >= Square(reach - TouchTolerance))
                {
                    continue;
                }

                any = true;
                var d = Math.Sqrt(d2);
                var (ux, uy) = d > 0 ? (dx / d, dy / d) : (1.0, 0.0);
                var push = reach + PushGap - d;
                var share = size[j] / (size[i] + size[j]);
                x[i] -= ux * push * share;
                y[i] -= uy * push * share;
                x[j] += ux * push * (1 - share);
                y[j] += uy * push * (1 - share);
            }

            return any;
        }

        /// <summary>Every pair closer than touching plus the skin.</summary>
        private void Collect(double[] x, double[] y, double[] r)
        {
            Array.Fill(_head, -1);
            for (var i = 0; i < x.Length; i++)
            {
                var (cx, cy) = Cell(x[i], y[i]);
                _cellOf[i] = Key(cx, cy);
                var bucket = Bucket(_cellOf[i]);
                _next[i] = _head[bucket];
                _head[bucket] = i;
            }

            _pairs.Clear();
            for (var i = 0; i < x.Length; i++)
            {
                var (cx, cy) = Cell(x[i], y[i]);
                for (var ox = -1; ox <= 1; ox++)
                {
                    for (var oy = -1; oy <= 1; oy++)
                    {
                        var key = Key(cx + ox, cy + oy);
                        for (var j = _head[Bucket(key)]; j >= 0; j = _next[j])
                        {
                            if (j > i && _cellOf[j] == key && Square(x[j] - x[i]) + Square(y[j] - y[i]) < Square(r[i] + r[j] + _skin))
                            {
                                _pairs.Add((i, j));
                            }
                        }
                    }
                }
            }
        }

        private (long X, long Y) Cell(double x, double y) => ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell));

        private static long Key(long cx, long cy) => (cx * 73_856_093) ^ (cy * 19_349_663);

        private int Bucket(long key) => (int)((ulong)key % (ulong)_head.Length);
    }
}
