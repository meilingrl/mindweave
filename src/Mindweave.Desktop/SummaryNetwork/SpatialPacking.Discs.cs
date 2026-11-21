namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>A circle: a footprint, or the disc of a whole subtree.</summary>
internal readonly record struct Circle(double X, double Y, double R);

/// <summary>
/// The two geometric building blocks of <see cref="SpatialPacking"/>: front-chain packing of circles (after Wang et
/// al. 2006, as in d3-hierarchy's <c>packSiblings</c>) and the smallest circle enclosing a set of circles (Welzl's
/// move-to-front idea, as in d3-hierarchy's <c>packEnclose</c>). Both are deterministic.
/// </summary>
public static partial class SpatialPacking
{
    /// <summary>Two circles closer than this overlap; d3 uses the same tolerance for round-off in tangent placement.</summary>
    private const double TouchTolerance = 1e-6;

    /// <summary>
    /// Packs circles in their given order. Circle 0 sits at the origin and circle 1 straight above it (north on
    /// screen); circle 2 goes to its right, so the packing winds clockwise on screen. Each later circle is placed
    /// tangent to two neighbours on the front chain (the outer boundary of the circles placed so far), overlapping no
    /// circle on it.
    /// </summary>
    /// <param name="r">The radii.</param>
    /// <param name="x">Receives the centres' x.</param>
    /// <param name="y">Receives the centres' y.</param>
    /// <param name="spiral">
    /// Whether each circle goes next to the one placed just before it, on whichever side of it lies nearer circle 0:
    /// a clockwise spiral around circle 0, used for a node's children and for chains. Otherwise each circle takes the
    /// place on the whole front chain whose far edge is nearest the reference point: the tightest fit, used for the
    /// roots.
    /// </param>
    /// <param name="mass">
    /// With <paramref name="spiral"/> off, the masses that move the reference point: it is the mass-weighted
    /// centroid of the circles placed so far, each at its centre plus (<paramref name="gx"/>, <paramref name="gy"/>).
    /// Null keeps the reference at circle 0's centre.
    /// </param>
    /// <param name="gx">Each circle's centre of mass, relative to its centre (x).</param>
    /// <param name="gy">Each circle's centre of mass, relative to its centre (y).</param>
    private static void Pack(double[] r, double[] x, double[] y, bool spiral, double[]? mass = null, double[]? gx = null, double[]? gy = null)
    {
        var n = r.Length;
        if (n == 0)
        {
            return;
        }

        x[0] = 0;
        y[0] = 0;
        if (n == 1)
        {
            return;
        }

        x[1] = 0;
        y[1] = -(r[0] + r[1]);
        if (n == 2)
        {
            return;
        }

        // The outside of a chain link a → b is the side where cross(b − a, p − a) < 0. For the first triangle the
        // chain is 0 → 1 → 2 → 0, so circle 2 goes on the outside of 1 → 0: east of the first two.
        (x[2], y[2]) = Tangent(r, x, y, 1, 0, r[2]);
        var next = new int[n];
        var previous = new int[n];
        (next[0], next[1], next[2]) = (1, 2, 0);
        (previous[0], previous[1], previous[2]) = (2, 0, 1);

        var (weight, sumX, sumY) = (0.0, 0.0, 0.0);
        if (mass is not null)
        {
            for (var k = 0; k < 3; k++)
            {
                weight += mass[k];
                sumX += mass[k] * (x[k] + gx![k]);
                sumY += mass[k] * (y[k] + gy![k]);
            }
        }

        var last = 2;
        for (var i = 3; i < n; i++)
        {
            var (referenceX, referenceY) = mass is not null && weight > 0 ? (sumX / weight, sumY / weight) : (0.0, 0.0);
            var a = spiral ? NextToLast(r, x, y, next, previous, last, r[i]) : Tightest(r, x, y, next, last, r[i], referenceX, referenceY);
            var b = next[a];

            // Place the circle between a and b. If it overlaps a circle further along the chain, the circles between
            // are enclosed: drop them from the chain and try again (d3's packSiblings, unchanged).
            for (var attempt = 0; ; attempt++)
            {
                (x[i], y[i]) = Tangent(r, x, y, a, b, r[i]);
                if (attempt > 2 * n || !FirstOverlapOnChain(r, x, y, next, previous, ref a, ref b, i))
                {
                    break;
                }
            }

            previous[i] = a;
            next[i] = b;
            next[a] = i;
            previous[b] = i;
            last = i;
            if (mass is not null)
            {
                weight += mass[i];
                sumX += mass[i] * (x[i] + gx![i]);
                sumY += mass[i] * (y[i] + gy![i]);
            }
        }
    }

    /// <summary>
    /// The chain link next to the circle placed last, on the side whose new circle lies nearer circle 0 (the link
    /// leading on from it on a tie): this keeps consecutive circles touching and winding one way.
    /// </summary>
    private static int NextToLast(double[] r, double[] x, double[] y, int[] next, int[] previous, int last, double radius)
    {
        var ahead = Distance(Tangent(r, x, y, last, next[last], radius));
        var behind = Distance(Tangent(r, x, y, previous[last], last, radius));
        return behind < ahead ? previous[last] : last;

        static double Distance((double X, double Y) p) => Math.Sqrt((p.X * p.X) + (p.Y * p.Y));
    }

    /// <summary>The chain link whose new circle has its far edge nearest the reference point (the first one found on a tie).</summary>
    private static int Tightest(double[] r, double[] x, double[] y, int[] next, int last, double radius, double referenceX, double referenceY)
    {
        var (best, bestScore) = (last, double.PositiveInfinity);
        var a = last;
        do
        {
            var (px, py) = Tangent(r, x, y, a, next[a], radius);
            var score = Math.Sqrt(((px - referenceX) * (px - referenceX)) + ((py - referenceY) * (py - referenceY)));
            if (score < bestScore)
            {
                (best, bestScore) = (a, score);
            }

            a = next[a];
        }
        while (a != last);

        return best;
    }

    /// <summary>
    /// Looks along the chain both ways from the link a → b, nearest first, for a circle that circle
    /// <paramref name="c"/> overlaps. If one is found, the chain is cut short to it (it becomes the new a or b) and the
    /// method returns true.
    /// </summary>
    private static bool FirstOverlapOnChain(double[] r, double[] x, double[] y, int[] next, int[] previous, ref int a, ref int b, int c)
    {
        int j = next[b], k = previous[a];
        double ahead = r[b], behind = r[a];
        do
        {
            if (ahead <= behind)
            {
                if (Overlaps(r, x, y, j, c))
                {
                    b = j;
                    next[a] = b;
                    previous[b] = a;
                    return true;
                }

                ahead += r[j];
                j = next[j];
            }
            else
            {
                if (Overlaps(r, x, y, k, c))
                {
                    a = k;
                    next[a] = b;
                    previous[b] = a;
                    return true;
                }

                behind += r[k];
                k = previous[k];
            }
        }
        while (j != next[k]);

        return false;
    }

    /// <summary>Where a circle of <paramref name="radius"/> touches circles a and b, on the outside of the link a → b.</summary>
    private static (double X, double Y) Tangent(double[] r, double[] x, double[] y, int a, int b, double radius)
    {
        var dx = x[b] - x[a];
        var dy = y[b] - y[a];
        var d2 = (dx * dx) + (dy * dy);
        if (d2 <= 0)
        {
            return (x[a] + r[a] + radius, y[a]);
        }

        var d = Math.Sqrt(d2);
        var toA = r[a] + radius;
        var toB = r[b] + radius;
        var along = (d2 + (toA * toA) - (toB * toB)) / (2 * d);
        var across = Math.Sqrt(Math.Max(0, (toA * toA) - (along * along)));
        var (ux, uy) = (dx / d, dy / d);
        return (x[a] + (along * ux) + (across * uy), y[a] + (along * uy) - (across * ux));
    }

    private static bool Overlaps(double[] r, double[] x, double[] y, int i, int j)
    {
        var reach = r[i] + r[j] - TouchTolerance;
        var dx = x[j] - x[i];
        var dy = y[j] - y[i];
        return reach > 0 && reach * reach > (dx * dx) + (dy * dy);
    }

    /// <summary>
    /// The smallest circle enclosing every circle (d3's <c>packEnclose</c>). The circles are visited in a fixed
    /// scrambled order (a stride coprime to their count) instead of d3's random shuffle, so packed circles, which come
    /// outward in order, do not make it quadratic.
    /// </summary>
    internal static Circle Enclose(IReadOnlyList<Circle> circles)
    {
        var n = circles.Count;
        if (n == 0)
        {
            return new Circle(0, 0, 0);
        }

        var stride = Stride(n);
        var basis = new List<Circle>(3);
        Circle? enclosing = null;
        for (var i = 0; i < n;)
        {
            var p = circles[(int)((long)i * stride % n)];
            if (enclosing is { } e && EnclosesWeak(e, p))
            {
                i++;
                continue;
            }

            if (ExtendBasis(basis, p) is { } extended)
            {
                basis = extended;
                enclosing = EncloseBasis(basis);
            }
            else
            {
                // Numerically degenerate (d3 would throw): grow the circle to take p in; it still encloses.
                basis = [p];
                enclosing = enclosing is { } e2 ? EncloseBasis2(e2, p) : p;
            }

            i = 0;
        }

        return enclosing!.Value;
    }

    private static Circle Enclose(double[] r, double[] x, double[] y)
    {
        var circles = new Circle[r.Length];
        for (var i = 0; i < r.Length; i++)
        {
            circles[i] = new Circle(x[i], y[i], r[i]);
        }

        return Enclose(circles);
    }

    private static int Stride(int n)
    {
        foreach (var prime in (ReadOnlySpan<int>)[7919, 104_729, 1_299_709])
        {
            if (n % prime != 0)
            {
                return prime % n == 0 ? 1 : prime;
            }
        }

        return 1;
    }

    private static List<Circle>? ExtendBasis(List<Circle> basis, Circle p)
    {
        if (EnclosesWeakAll(p, basis))
        {
            return [p];
        }

        for (var i = 0; i < basis.Count; i++)
        {
            if (EnclosesNot(p, basis[i]) && EnclosesWeakAll(EncloseBasis2(basis[i], p), basis))
            {
                return [basis[i], p];
            }
        }

        for (var i = 0; i < basis.Count - 1; i++)
        {
            for (var j = i + 1; j < basis.Count; j++)
            {
                if (EnclosesNot(EncloseBasis2(basis[i], basis[j]), p)
                    && EnclosesNot(EncloseBasis2(basis[i], p), basis[j])
                    && EnclosesNot(EncloseBasis2(basis[j], p), basis[i])
                    && EnclosesWeakAll(EncloseBasis3(basis[i], basis[j], p), basis))
                {
                    return [basis[i], basis[j], p];
                }
            }
        }

        return null;
    }

    private static bool EnclosesNot(Circle a, Circle b)
    {
        var dr = a.R - b.R;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return dr < 0 || dr * dr < (dx * dx) + (dy * dy);
    }

    private static bool EnclosesWeak(Circle a, Circle b)
    {
        var dr = a.R - b.R + (Math.Max(Math.Max(a.R, b.R), 1) * 1e-9);
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return dr > 0 && dr * dr > (dx * dx) + (dy * dy);
    }

    private static bool EnclosesWeakAll(Circle a, List<Circle> basis) => basis.TrueForAll(b => EnclosesWeak(a, b));

    private static Circle EncloseBasis(List<Circle> basis) => basis.Count switch
    {
        1 => basis[0],
        2 => EncloseBasis2(basis[0], basis[1]),
        _ => EncloseBasis3(basis[0], basis[1], basis[2]),
    };

    private static Circle EncloseBasis2(Circle a, Circle b)
    {
        var (x21, y21, r21) = (b.X - a.X, b.Y - a.Y, b.R - a.R);
        var l = Math.Sqrt((x21 * x21) + (y21 * y21));
        if (l <= 0)
        {
            return a.R >= b.R ? a : b;
        }

        return new Circle((a.X + b.X + (x21 / l * r21)) / 2, (a.Y + b.Y + (y21 / l * r21)) / 2, (l + a.R + b.R) / 2);
    }

    private static Circle EncloseBasis3(Circle a, Circle b, Circle c)
    {
        double x1 = a.X, y1 = a.Y, r1 = a.R;
        double a2 = x1 - b.X, a3 = x1 - c.X, b2 = y1 - b.Y, b3 = y1 - c.Y;
        double c2 = b.R - r1, c3 = c.R - r1;
        var d1 = (x1 * x1) + (y1 * y1) - (r1 * r1);
        var d2 = d1 - (b.X * b.X) - (b.Y * b.Y) + (b.R * b.R);
        var d3 = d1 - (c.X * c.X) - (c.Y * c.Y) + (c.R * c.R);
        var ab = (a3 * b2) - (a2 * b3);
        var xa = (((b2 * d3) - (b3 * d2)) / (ab * 2)) - x1;
        var xb = ((b3 * c2) - (b2 * c3)) / ab;
        var ya = (((a3 * d2) - (a2 * d3)) / (ab * 2)) - y1;
        var yb = ((a2 * c3) - (a3 * c2)) / ab;
        var qa = (xb * xb) + (yb * yb) - 1;
        var qb = 2 * (r1 + (xa * xb) + (ya * yb));
        var qc = (xa * xa) + (ya * ya) - (r1 * r1);
        var radius = -(Math.Abs(qa) > 1e-6 ? (qb + Math.Sqrt((qb * qb) - (4 * qa * qc))) / (2 * qa) : qc / qb);
        return new Circle(x1 + xa + (xb * radius), y1 + ya + (yb * radius), radius);
    }

    private static double Square(double v) => v * v;
}
