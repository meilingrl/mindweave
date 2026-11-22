namespace MoleculeLayout;

/// <summary>Tuning knobs, in canvas units (pixels at zoom 1).</summary>
public sealed record Settings
{
    /// <summary>Bond length between two dots' edges, and how much a bond grows with the size of the branch it leads to.</summary>
    public double Bond { get; init; } = 58;
    public double BondGrow { get; init; } = 10;

    /// <summary>The most dots behind a line that still add to the room it takes at a dot.</summary>
    public int ShareCap { get; init; } = 8;

    /// <summary>From this many lines at one dot, bonds lengthen so the lines fit round it, and leaves stagger.</summary>
    public int CrowdFrom { get; init; } = 6;
    public double CrowdSpacing { get; init; } = 10;
    public double CrowdStagger { get; init; } = 1.45;

    /// <summary>How hard a line crossing a title pushes, per step.</summary>
    public double LineShove { get; init; } = 4;

    /// <summary>Gaps kept between dots that are not bonded. Between two sibling groups the gap grows with the smaller group.</summary>
    public double GapNested { get; init; } = 18;
    public double GapSibling { get; init; } = 16;
    public double GapSiblingGrow { get; init; } = 12;
    public double GapTree { get; init; } = 54;
    public double GapTreeGrow { get; init; } = 14;

    public int Iterations { get; init; } = 700;
    public double LabelsFrom { get; init; } = 0.4;
    public double AngleStiffness { get; init; } = 0.22;
    public double BondStiffness { get; init; } = 0.25;
    public double Repulsion { get; init; } = 0.5;

    /// <summary>Packing explorations: turns tried, how many grid rings past the first fit are still considered, and how much a short time thread counts.</summary>
    public int Rotations { get; init; } = 8;
    public int SearchBand { get; init; } = 6;
    public double ThreadPreference { get; init; } = 0.3;
}

/// <summary>
/// The molecule layout. Every dot's lines spread to equal angles (two lines run straight, three at 120°); each branch is a
/// group, and groups keep gaps that grow with their size; separate explorations are separate molecules, pulled gently
/// toward the centre, heaviest nearest; titles go in each dot's widest free angle. Deterministic: a fixed start and a
/// fixed number of steps.
/// </summary>
public sealed class Molecule
{
    private readonly Workspace ws;
    private readonly Settings s;
    private readonly List<Dot> d;
    private readonly int n;
    private readonly double[] gap;
    private readonly int[] preorder;
    private readonly int[] start;
    private readonly int[] end;
    private readonly double[] dx;
    private readonly double[] dy;
    private readonly List<(int A, int B)> bonds = [];
    private readonly Dictionary<int, List<int>> treeDots = [];

    /// <summary>Per dot: the angle of each child's line, measured from the parent's line (or from the first child's, at a root).</summary>
    private readonly double[][] offsets;

    /// <summary>Per dot: +1 if its children follow clockwise from the parent's line, -1 if counter-clockwise.</summary>
    private readonly int[] hand;

    public Molecule(Workspace ws, Settings s)
    {
        this.ws = ws;
        this.s = s;
        d = ws.Dots;
        n = d.Count;
        gap = new double[n * n];
        preorder = new int[n];
        start = new int[n];
        end = new int[n];
        dx = new double[n];
        dy = new double[n];
        var k = 0;
        foreach (var r in ws.Roots)
        {
            Preorder(r, ref k);
            treeDots[r] = [.. preorder[start[r]..end[r]]];
        }

        foreach (var x in d)
        {
            if (x.Parent >= 0)
            {
                bonds.Add((x.Parent, x.Index));
            }
        }

        for (var u = 0; u < n; u++)
        {
            for (var v = 0; v < n; v++)
            {
                gap[(u * n) + v] = Gap(u, v);
            }
        }

        offsets = new double[n][];
        hand = new int[n];
        for (var i = 0; i < n; i++)
        {
            offsets[i] = Offsets(i);
            hand[i] = 1;
        }
    }

    /// <summary>
    /// How much room a line takes at a dot: the square root of how many dots lie behind it, at most <see cref="Settings.ShareCap"/>.
    /// Equal branches get equal angles (three lines at 120°); a big branch pushes harder than a single dot, like a bulky group.
    /// </summary>
    private double Share(int behind) => Math.Sqrt(Math.Min(behind, s.ShareCap));

    private double[] Offsets(int i)
    {
        var kids = d[i].Children;
        if (kids.Count == 0)
        {
            return [];
        }

        var shares = new List<double>();
        var hasParent = d[i].Parent >= 0;
        if (hasParent)
        {
            shares.Add(Share(d[d[i].Root].Size - d[i].Size));
        }

        shares.AddRange(kids.Select(c => Share(d[c].Size)));
        var total = shares.Sum();
        var result = new double[kids.Count];
        double angle = 0;
        var first = hasParent ? 1 : 0;
        for (var k = 0; k < kids.Count; k++)
        {
            var m = first + k;
            if (m > 0)
            {
                angle += Math.PI * (shares[m - 1] + shares[m]) / total;
            }

            result[k] = angle;
        }

        return result;
    }

    private void Preorder(int i, ref int k)
    {
        start[i] = k;
        preorder[k++] = i;
        foreach (var c in d[i].Children)
        {
            Preorder(c, ref k);
        }

        end[i] = k;
    }

    private bool IsAncestor(int a, int b) => start[a] <= start[b] && start[b] < end[a];

    /// <summary>The extra room kept between two dots beyond their own radii; -1 for a bonded pair (the bond decides).</summary>
    private double Gap(int u, int v)
    {
        if (u == v || d[u].Parent == v || d[v].Parent == u)
        {
            return -1;
        }

        if (d[u].Root != d[v].Root)
        {
            return -1;
        }

        if (IsAncestor(u, v) || IsAncestor(v, u))
        {
            return s.GapNested;
        }

        // The two branches that split at the lowest common ancestor.
        var a = u;
        var b = v;
        while (d[a].Depth > d[b].Depth)
        {
            a = d[a].Parent;
        }

        while (d[b].Depth > d[a].Depth)
        {
            b = d[b].Parent;
        }

        var ca = u;
        var cb = v;
        while (a != b)
        {
            ca = a;
            cb = b;
            a = d[a].Parent;
            b = d[b].Parent;
        }

        var smaller = Math.Min(d[ca].Size, d[cb].Size);
        return s.GapSibling + (s.GapSiblingGrow * Math.Log2(1 + smaller) * (smaller > 1 ? 1 : 0.4));
    }

    /// <summary>
    /// A bond's rest length: the two dots, the base length, more for a bigger branch; around a busy dot, long enough for
    /// all its lines to fit round it, and its single-dot leaves alternate between two lengths so their titles interleave.
    /// </summary>
    private double BondLength(int p, int c)
    {
        var length = d[p].Footprint + d[c].Footprint + s.Bond + (s.BondGrow * Math.Log2(d[c].Size));
        var degree = d[p].Children.Count + (d[p].Parent >= 0 ? 1 : 0);
        if (degree >= s.CrowdFrom)
        {
            // Each line needs room on the circle for its dot and, on average, half its title's width and height.
            var round = d[p].Children.Sum(k => (2 * d[k].Footprint) + (0.5 * (d[k].LabelW + d[k].LabelH)) + s.CrowdSpacing);
            length = Math.Max(length, round / (2 * Math.PI));
            if (d[c].Children.Count == 0 && d[p].Children.IndexOf(c) % 2 == 1)
            {
                length *= s.CrowdStagger;
            }
        }

        return length;
    }

    public void Run()
    {
        foreach (var r in ws.Roots)
        {
            Construct(r, 0, 0, null);
            Relax(r);
        }

        if (ws.Roots.Count > 1)
        {
            new Packing(this, ws, s).Pack();
        }

        var all = Enumerable.Range(0, n).ToList();
        ChooseLabels(all, bonds, true);
        ChooseLabels(all, bonds, true);
    }

    /// <summary>One exploration on its own: overlaps, bond angles and bond lengths, a fixed number of steps.</summary>
    private void Relax(int root)
    {
        var dots = treeDots[root];
        var treeBonds = bonds.Where(b => d[b.A].Root == root).ToList();
        var labelsFrom = (int)(s.Iterations * s.LabelsFrom);
        for (var t = 0; t < s.Iterations + 60; t++)
        {
            var labels = t >= labelsFrom;
            if (labels && (t - labelsFrom) % 10 == 0)
            {
                ChooseLabels(dots, treeBonds, false);
            }

            Repel(dots, labels);
            Angles(root);
            Bonds(root);
        }

        ChooseLabels(dots, treeBonds, false);
    }

    public List<int> TreeDots(int root) => treeDots[root];

    public List<(int A, int B)> TreeBonds(int root) => bonds.Where(b => d[b.A].Root == root).ToList();

    public void ChooseTreeLabels(int root) => ChooseLabels(treeDots[root], TreeBonds(root), false);

    /// <summary>
    /// The ideal molecule. Children follow in creation order around their parent; each dot turns its heaviest branch the
    /// other way from the turn that led to it, so chains zigzag gently instead of curling into rings.
    /// </summary>
    private void Construct(int i, double x, double y, double? inDir, int lastTurn = 0)
    {
        d[i].X = x;
        d[i].Y = y;
        var kids = d[i].Children;
        if (kids.Count == 0)
        {
            return;
        }

        var off = offsets[i];
        var angles = new double[kids.Count];
        if (inDir is { } dir)
        {
            var back = dir + Math.PI;
            var heavy = Enumerable.Range(0, kids.Count).MaxBy(k => (d[kids[k]].Size, -k));
            var turn = Wrap(back + off[heavy] - dir);
            hand[i] = Math.Abs(turn) < 0.05 || lastTurn == 0 ? 1 : Math.Sign(turn) == lastTurn ? -1 : 1;
            for (var k = 0; k < kids.Count; k++)
            {
                angles[k] = back + (hand[i] * off[k]);
            }
        }
        else
        {
            for (var k = 0; k < kids.Count; k++)
            {
                angles[k] = (-Math.PI / 2) + off[k];
            }
        }

        for (var k = 0; k < kids.Count; k++)
        {
            var c = kids[k];
            var length = BondLength(i, c);
            var turn = inDir is { } incoming ? Wrap(angles[k] - incoming) : 0;
            var sign = Math.Abs(turn) < 0.05 ? 0 : Math.Sign(turn);
            Construct(c, x + (length * Math.Cos(angles[k])), y + (length * Math.Sin(angles[k])), angles[k], sign);
        }
    }

    public (double X, double Y) Centroid(List<int> dots)
    {
        double sx = 0, sy = 0, sw = 0;
        foreach (var i in dots)
        {
            sx += d[i].X * d[i].Weight;
            sy += d[i].Y * d[i].Weight;
            sw += d[i].Weight;
        }

        return (sx / sw, sy / sw);
    }

    private void Repel(List<int> dots, bool labels)
    {
        Array.Clear(dx);
        Array.Clear(dy);
        for (var iu = 0; iu < dots.Count; iu++)
        {
            var u = dots[iu];
            for (var iv = iu + 1; iv < dots.Count; iv++)
            {
                var v = dots[iv];
                var g = gap[(u * n) + v];
                if (g < 0)
                {
                    continue;
                }

                var want = d[u].Footprint + d[v].Footprint + g;
                var ex = d[v].X - d[u].X;
                var ey = d[v].Y - d[u].Y;
                var dist = Math.Sqrt((ex * ex) + (ey * ey));
                if (dist >= want)
                {
                    continue;
                }

                if (dist < 1e-6)
                {
                    ex = Math.Cos(u + (v * 0.7));
                    ey = Math.Sin(u + (v * 0.7));
                    dist = 1;
                }

                var push = (want - dist) * 0.5 * s.Repulsion / dist;
                dx[u] -= ex * push;
                dy[u] -= ey * push;
                dx[v] += ex * push;
                dy[v] += ey * push;
            }
        }

        if (labels)
        {
            foreach (var u in dots)
            {
                var (l, t, r, b) = LabelRect(u, d[u].LabelSide, 3);
                foreach (var v in dots)
                {
                    if (v == u)
                    {
                        continue;
                    }

                    // A label never covers another dot.
                    var qx = Math.Clamp(d[v].X, l, r);
                    var qy = Math.Clamp(d[v].Y, t, b);
                    var ex = d[v].X - qx;
                    var ey = d[v].Y - qy;
                    var dist = Math.Sqrt((ex * ex) + (ey * ey));
                    var want = d[v].Footprint + 3;
                    if (dist < want)
                    {
                        if (dist < 1e-6)
                        {
                            // The dot's centre is inside the box: leave by the nearest side.
                            var options = new[] { (d[v].X - l, -1.0, 0.0), (r - d[v].X, 1.0, 0.0), (d[v].Y - t, 0.0, -1.0), (b - d[v].Y, 0.0, 1.0) };
                            var best = options.MinBy(o => o.Item1);
                            ex = best.Item2;
                            ey = best.Item3;
                            dist = 0;
                            want += best.Item1;
                        }
                        else
                        {
                            ex /= dist;
                            ey /= dist;
                        }

                        var push = (want - dist) * 0.5 * s.Repulsion;
                        dx[v] += ex * push;
                        dy[v] += ey * push;
                        dx[u] -= ex * push;
                        dy[u] -= ey * push;
                    }

                    if (v > u)
                    {
                        var (l2, t2, r2, b2) = LabelRect(v, d[v].LabelSide, 3);
                        var ox = Math.Min(r, r2) - Math.Max(l, l2);
                        var oy = Math.Min(b, b2) - Math.Max(t, t2);
                        if (ox > 0 && oy > 0)
                        {
                            if (ox < oy)
                            {
                                var sign = (l + r) < (l2 + r2) ? -1 : 1;
                                dx[u] += sign * ox * 0.5 * s.Repulsion;
                                dx[v] -= sign * ox * 0.5 * s.Repulsion;
                            }
                            else
                            {
                                var sign = (t + b) < (t2 + b2) ? -1 : 1;
                                dy[u] += sign * oy * 0.5 * s.Repulsion;
                                dy[v] -= sign * oy * 0.5 * s.Repulsion;
                            }
                        }
                    }
                }
            }

            // A line through a title pushes the title's dot off the line, and the line the other way.
            var root = d[dots[0]].Root;
            foreach (var u in dots)
            {
                var rect = LabelRect(u, d[u].LabelSide, 2);
                var cx = (rect.L + rect.R) / 2;
                var cy = (rect.T + rect.B) / 2;
                foreach (var (a, b) in bonds)
                {
                    if (a == u || b == u || d[a].Root != root || !SegmentHitsRect(d[a].X, d[a].Y, d[b].X, d[b].Y, rect))
                    {
                        continue;
                    }

                    var ex = d[b].X - d[a].X;
                    var ey = d[b].Y - d[a].Y;
                    var t = Math.Clamp((((cx - d[a].X) * ex) + ((cy - d[a].Y) * ey)) / Math.Max(1e-9, (ex * ex) + (ey * ey)), 0, 1);
                    var px = cx - (d[a].X + (t * ex));
                    var py = cy - (d[a].Y + (t * ey));
                    var len = Math.Sqrt((px * px) + (py * py));
                    if (len < 1e-6)
                    {
                        (px, py, len) = (-ey, ex, Math.Sqrt((ex * ex) + (ey * ey)));
                    }

                    var step = s.LineShove / len;
                    dx[u] += px * step;
                    dy[u] += py * step;
                    dx[a] -= px * step * 0.5;
                    dy[a] -= py * step * 0.5;
                    dx[b] -= px * step * 0.5;
                    dy[b] -= py * step * 0.5;
                }
            }
        }

        foreach (var i in dots)
        {
            var m = Math.Sqrt((dx[i] * dx[i]) + (dy[i] * dy[i]));
            var cap = m > 24 ? 24 / m : 1;
            d[i].X += dx[i] * cap;
            d[i].Y += dy[i] * cap;
        }
    }

    /// <summary>Turns each branch, as a whole, toward its ideal bond angle at its parent.</summary>
    private void Angles(int root)
    {
        foreach (var i in treeDots[root])
        {
            var kids = d[i].Children;
            if (kids.Count == 0)
            {
                continue;
            }

            var hasParent = d[i].Parent >= 0;
            if (kids.Count + (hasParent ? 1 : 0) < 2)
            {
                continue;
            }

            var off = offsets[i];
            double baseAngle;
            var sign = 1;
            if (hasParent)
            {
                baseAngle = Math.Atan2(d[d[i].Parent].Y - d[i].Y, d[d[i].Parent].X - d[i].X);
                sign = hand[i];
            }
            else
            {
                // A root has no parent line: the fan may turn freely, so only the spacing is enforced.
                double sx = 0, sy = 0;
                for (var k = 0; k < kids.Count; k++)
                {
                    var a = Math.Atan2(d[kids[k]].Y - d[i].Y, d[kids[k]].X - d[i].X) - off[k];
                    sx += Math.Cos(a);
                    sy += Math.Sin(a);
                }

                baseAngle = Math.Atan2(sy, sx);
            }

            for (var k = 0; k < kids.Count; k++)
            {
                var c = kids[k];
                var target = baseAngle + (sign * off[k]);
                var current = Math.Atan2(d[c].Y - d[i].Y, d[c].X - d[i].X);
                var error = Wrap(target - current);
                Rotate(c, d[i].X, d[i].Y, error * s.AngleStiffness);
            }
        }
    }

    private void Bonds(int root)
    {
        foreach (var c in treeDots[root])
        {
            var p = d[c].Parent;
            if (p < 0)
            {
                continue;
            }

            var ex = d[c].X - d[p].X;
            var ey = d[c].Y - d[p].Y;
            var dist = Math.Max(1e-6, Math.Sqrt((ex * ex) + (ey * ey)));
            var move = (BondLength(p, c) - dist) * s.BondStiffness / dist;
            Translate(c, ex * move, ey * move);
        }
    }

    public void Rotate(int top, double cx, double cy, double angle)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        for (var k = start[top]; k < end[top]; k++)
        {
            var i = preorder[k];
            var x = d[i].X - cx;
            var y = d[i].Y - cy;
            d[i].X = cx + (x * cos) - (y * sin);
            d[i].Y = cy + (x * sin) + (y * cos);
        }
    }

    public void Translate(int top, double x, double y)
    {
        for (var k = start[top]; k < end[top]; k++)
        {
            d[preorder[k]].X += x;
            d[preorder[k]].Y += y;
        }
    }

    private static double Wrap(double a)
    {
        while (a > Math.PI)
        {
            a -= 2 * Math.PI;
        }

        while (a < -Math.PI)
        {
            a += 2 * Math.PI;
        }

        return a;
    }

    // ----- Labels -----

    /// <summary>
    /// Where a title can sit around its dot: the direction it lies in (for keeping off the dot's own lines), a small cost
    /// that prefers reading-friendly places (beside the dot first), and how its text is aligned.
    /// </summary>
    public static readonly (double Angle, double Cost, string Anchor)[] Sides =
    [
        (0, 0, "start"), (45, 10, "start"), (90, 14, "middle"), (135, 10, "end"),
        (180, 3, "end"), (225, 10, "end"), (270, 14, "middle"), (315, 10, "start"),
        (340, 4, "start"), (20, 4, "start"), (200, 6, "end"), (160, 6, "end"),
        (290, 12, "start"), (250, 12, "end"), (70, 12, "start"), (110, 12, "end"),
    ];

    public (double L, double T, double R, double B) LabelRect(int i, int side, double margin = 0)
    {
        var (l, t) = LabelOffset(d[i], side);
        return (d[i].X + l - margin, d[i].Y + t - margin, d[i].X + l + d[i].LabelW + margin, d[i].Y + t + d[i].LabelH + margin);
    }

    public static (double L, double T) LabelOffset(Dot x, int side)
    {
        const double g = 6;
        var f = x.Footprint;
        var w = x.LabelW;
        var h = x.LabelH;
        var c = (f * 0.62) + 3;
        return side switch
        {
            0 => (f + g, -h / 2),
            1 => (c, c),
            2 => (-w / 2, f + g - 2),
            3 => (-c - w, c),
            4 => (-f - g - w, -h / 2),
            5 => (-c - w, -c - h),
            6 => (-w / 2, -f - g - h + 2),
            7 => (c, -c - h),
            8 => (f + g, -h + 5),
            9 => (f + g, -5),
            10 => (-f - g - w, -h + 5),
            11 => (-f - g - w, -5),
            12 => (-6, -f - g - h + 2),
            13 => (6 - w, -f - g - h + 2),
            14 => (-6, f + g - 2),
            _ => (6 - w, f + g - 2),
        };
    }

    /// <summary>Puts each title where it is clearest: away from its own lines (the widest free angle), off other dots, labels and lines.</summary>
    private void ChooseLabels(List<int> dots, List<(int A, int B)> lines, bool threads)
    {
        var order = dots.OrderByDescending(i => d[i].Weight).ThenBy(i => i).ToArray();
        foreach (var i in order)
        {
            var directions = new List<double>();
            if (d[i].Parent >= 0)
            {
                directions.Add(Math.Atan2(d[d[i].Parent].Y - d[i].Y, d[d[i].Parent].X - d[i].X));
            }

            foreach (var c in d[i].Children)
            {
                directions.Add(Math.Atan2(d[c].Y - d[i].Y, d[c].X - d[i].X));
            }

            var bestSide = 0;
            var bestCost = double.MaxValue;
            for (var side = 0; side < Sides.Length; side++)
            {
                var angle = Sides[side].Angle * Math.PI / 180;
                var cost = Sides[side].Cost;
                foreach (var dir in directions)
                {
                    var off = Math.Abs(Wrap(angle - dir)) * 180 / Math.PI;
                    cost += Math.Max(0, 75 - off) * 3;
                }

                var rect = LabelRect(i, side, 2);
                foreach (var v in dots)
                {
                    if (v == i)
                    {
                        continue;
                    }

                    var qx = Math.Clamp(d[v].X, rect.L, rect.R);
                    var qy = Math.Clamp(d[v].Y, rect.T, rect.B);
                    if (Math.Sqrt(((d[v].X - qx) * (d[v].X - qx)) + ((d[v].Y - qy) * (d[v].Y - qy))) < d[v].Footprint + 2)
                    {
                        cost += 1000;
                    }

                    var o = LabelRect(v, d[v].LabelSide, 2);
                    var ox = Math.Min(rect.R, o.R) - Math.Max(rect.L, o.L);
                    var oy = Math.Min(rect.B, o.B) - Math.Max(rect.T, o.T);
                    if (ox > 0 && oy > 0)
                    {
                        cost += 300 + (ox * oy * 0.5);
                    }
                }

                foreach (var (a, b) in lines)
                {
                    if (SegmentHitsRect(d[a].X, d[a].Y, d[b].X, d[b].Y, rect))
                    {
                        cost += 400;
                    }
                }

                foreach (var (a, b) in threads ? ThreadPairs() : [])
                {
                    if (SegmentHitsRect(d[a].X, d[a].Y, d[b].X, d[b].Y, rect))
                    {
                        cost += 120;
                    }
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestSide = side;
                }
            }

            d[i].LabelSide = bestSide;
            (d[i].LabelDx, d[i].LabelDy) = LabelOffset(d[i], bestSide);
        }
    }

    public IEnumerable<(int A, int B)> ThreadPairs()
    {
        for (var k = 0; k + 1 < ws.Roots.Count; k++)
        {
            yield return (ws.Roots[k], ws.Roots[k + 1]);
        }
    }

    public static bool SegmentHitsRect(double x1, double y1, double x2, double y2, (double L, double T, double R, double B) r)
    {
        // Liang–Barsky clipping.
        double t0 = 0, t1 = 1;
        var ddx = x2 - x1;
        var ddy = y2 - y1;
        foreach (var (p, q) in new[] { (-ddx, x1 - r.L), (ddx, r.R - x1), (-ddy, y1 - r.T), (ddy, r.B - y1) })
        {
            if (Math.Abs(p) < 1e-12)
            {
                if (q < 0)
                {
                    return false;
                }

                continue;
            }

            var t = q / p;
            if (p < 0)
            {
                t0 = Math.Max(t0, t);
            }
            else
            {
                t1 = Math.Min(t1, t);
            }

            if (t0 > t1)
            {
                return false;
            }
        }

        return true;
    }

    public IReadOnlyList<(int A, int B)> BondList => bonds;
}
