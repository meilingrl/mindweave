namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// The internal tension at footprint level (docs/design/session-network.md §6.1 steps 4 and 5, carried down to every
/// dot). Nested discs leave empty space at every level, and it compounds with depth. This relaxation lets a subtree,
/// or the whole graph, settle into a smaller circle around its centre of gravity, while branch lines hold every
/// sub-branch next to its parent and footprints never overlap.
/// </summary>
public static partial class SpatialPacking
{
    /// <summary>Relaxation steps for the whole graph.</summary>
    public const int CompactionSteps = 300;

    /// <summary>Relaxation steps inside each group, whose nested groups have settled already.</summary>
    public const int GroupCompactionSteps = 150;

    /// <summary>The last part of the steps, in which gravity and springs have cooled to nothing and only overlaps are resolved.</summary>
    public const double CoolDown = 0.25;

    /// <summary>Pull of every body toward the centre of gravity, as a fraction of its distance, per step.</summary>
    public const double Gravity = 0.04;

    /// <summary>Longest gravity move per step, as a fraction of the body's smallest footprint radius, so far bodies close in gradually.</summary>
    public const double StepCap = 1;

    /// <summary>Spring along a branch line, on the part of its length beyond touching.</summary>
    public const double BranchSpring = 0.3;

    /// <summary>Spring along the time thread between consecutive roots: weak, so it shortens the thread without lining roots up.</summary>
    public const double ThreadSpring = 0.02;

    /// <summary>Collision passes per step; the last step may take <see cref="FinalPasses"/>.</summary>
    public const int CollisionPasses = 10;

    /// <summary>Collision passes the last step may take to end without overlap.</summary>
    public const int FinalPasses = 400;

    /// <summary>Overlapping footprints are pushed this far apart, a little past touching, so a pile clears in fewer passes.</summary>
    private const double PushGap = 2;

    /// <summary>
    /// Settles the footprints (centres <paramref name="x"/>, <paramref name="y"/>) in <paramref name="steps"/> steps.
    /// Every footprint belongs to a body (a nested subtree, or a root's whole tree), and each body is pulled as one
    /// toward the mass-weighted centroid of all dots, by <see cref="Gravity"/> of its distance (at most
    /// <see cref="StepCap"/> of its smallest footprint's radius), so bodies close up like soft balls without mixing.
    /// Every link then shortens by its spring, and overlapping footprints are pushed apart, the smaller one moving
    /// more. A step applies gravity and springs only after a step that ended without overlap, so the pile never
    /// jams; both cool linearly to nothing by the last <see cref="CoolDown"/> of the steps. The result is the last step
    /// that ended without overlap; the start has none, so there always is one.
    /// </summary>
    /// <param name="mass">Each footprint's mass (the weight w); without any, the footprints' areas.</param>
    /// <param name="offset">How far right of its dot each footprint's centre lies.</param>
    /// <param name="body">The body each footprint belongs to, numbered from 0.</param>
    /// <param name="links">Branch lines (parent, child) and time-thread segments (root, next root), with their spring.</param>
    private static void Compact(double[] x, double[] y, double[] r, double[] mass, double[] offset, int[] body, List<(int A, int B, double Spring)> links, int steps)
    {
        var n = x.Length;
        if (n < 2)
        {
            return;
        }

        var size = r.Select(v => v * v).ToArray();
        var heft = mass.Select(m => Math.Max(m, 0)).ToArray();
        var total = heft.Sum();
        if (total <= 0)
        {
            (heft, total) = (size, size.Sum());
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
        var contacts = new Contacts(n, 2 * r.Max());
        var clear = true;
        for (var step = 0; step < steps; step++)
        {
            var cooling = Math.Max(0, 1 - (step / ((1 - CoolDown) * steps)));
            if (cooling > 0 && clear)
            {
                Pull(x, y, offset, body, heft, total, bodyHeft, bodyCap, pullX, pullY, cooling);
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

            clear = contacts.Separate(x, y, r, size, step == steps - 1 ? FinalPasses : CollisionPasses);
            if (clear)
            {
                Array.Copy(x, bestX, n);
                Array.Copy(y, bestY, n);
            }
        }

        Array.Copy(bestX, x, n);
        Array.Copy(bestY, y, n);
    }

    /// <summary>Moves every body toward the centroid of all dots (see <see cref="Compact"/>).</summary>
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
