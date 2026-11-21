namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>Closing the holes a wedge fill leaves (see <see cref="FillWedges"/>).</summary>
public static partial class SpatialPacking
{
    /// <summary>
    /// Moves each dot (root first, parents before children) in toward the root along its ray, to the nearest place
    /// whose footprint overlaps nothing, as long as its branch lines then cross no more lines than before and the
    /// fans around its parent and around itself keep their creation order. Dots (<paramref name="x"/>,
    /// <paramref name="y"/>) are moved in place; the root stays.
    /// </summary>
    private static void PullIn(Forest forest, List<int> items, double[] x, double[] y)
    {
        var blocked = new List<(double Low, double High)>();
        foreach (var i in items)
        {
            var p = forest.Parent[i];
            if (p < 0)
            {
                continue;
            }

            var reach = Math.Sqrt(Square(x[i]) + Square(y[i]));
            if (reach <= 1)
            {
                continue;
            }

            var (ux, uy) = (x[i] / reach, y[i] / reach);
            var (offset, radius) = (forest.Offset[i], forest.Radius[i]);
            blocked.Clear();
            foreach (var j in items)
            {
                if (j == i)
                {
                    continue;
                }

                var (cx, cy) = (x[j] + forest.Offset[j] - offset, y[j]);
                var along = (ux * cx) + (uy * cy);
                var d = (along * along) - ((cx * cx) + (cy * cy) - Square(radius + forest.Radius[j]));
                if (d > 0)
                {
                    blocked.Add((along - Math.Sqrt(d), along + Math.Sqrt(d)));
                }
            }

            blocked.Sort((a, b) => a.Low.CompareTo(b.Low));
            var before = LineCrossings(forest, items, x, y, i);
            var (homeX, homeY) = (x[i], y[i]);
            var t = 0.0;
            var tried = 0;
            foreach (var (lo, hi) in blocked.Append((double.PositiveInfinity, double.PositiveInfinity)))
            {
                if (hi <= t)
                {
                    continue;
                }

                if (lo > t)
                {
                    if (t >= reach - PullStep)
                    {
                        break;
                    }

                    (x[i], y[i]) = (t * ux, t * uy);
                    if (LineCrossings(forest, items, x, y, i) <= before && FansInOrder(forest, x, y, i))
                    {
                        break;
                    }

                    (x[i], y[i]) = (homeX, homeY);
                    if (++tried >= PullTries)
                    {
                        break;
                    }
                }

                t = Math.Max(t, hi + TouchTolerance);
            }
        }
    }

    /// <summary>The least move worth making, in canvas units.</summary>
    internal const double PullStep = 8;

    /// <summary>Free places tried along a dot's ray before it stays put.</summary>
    internal const int PullTries = 3;

    /// <summary>Crossings between the lines at dot <paramref name="i"/> (to its parent and its children) and every other line.</summary>
    private static int LineCrossings(Forest forest, List<int> items, double[] x, double[] y, int i)
    {
        var count = 0;
        var ends = new List<int>(forest.Children[i]);
        if (forest.Parent[i] >= 0)
        {
            ends.Add(forest.Parent[i]);
        }

        foreach (var e in ends)
        {
            var (ax, ay, bx, by) = (x[i], y[i], x[e], y[e]);
            var (minX, maxX, minY, maxY) = (Math.Min(ax, bx), Math.Max(ax, bx), Math.Min(ay, by), Math.Max(ay, by));
            foreach (var q in items)
            {
                var p = forest.Parent[q];
                if (p < 0 || q == i || p == i || q == e || p == e)
                {
                    continue;
                }

                var (cx, cy, dx, dy) = (x[p], y[p], x[q], y[q]);
                if (Math.Max(cx, dx) < minX || Math.Min(cx, dx) > maxX || Math.Max(cy, dy) < minY || Math.Min(cy, dy) > maxY)
                {
                    continue;
                }

                if (Crosses(ax, ay, bx, by, cx, cy, dx, dy))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Whether the fans round dot i's parent and round i itself keep creation order (by footprint centres).</summary>
    private static bool FansInOrder(Forest forest, double[] x, double[] y, int i) =>
        FanInOrder(forest, x, y, forest.Parent[i]) && FanInOrder(forest, x, y, i);

    private static bool FanInOrder(Forest forest, double[] x, double[] y, int p)
    {
        var children = forest.Children[p];
        if (children.Count < 3)
        {
            return true;
        }

        var (px, py) = (x[p] + forest.Offset[p], y[p]);
        var first = Math.Atan2(y[children[0]] - py, x[children[0]] + forest.Offset[children[0]] - px);
        var previous = 0.0;
        for (var k = 1; k < children.Count; k++)
        {
            var c = children[k];
            var turned = (((Math.Atan2(y[c] - py, x[c] + forest.Offset[c] - px) - first) % (2 * Math.PI)) + (2 * Math.PI)) % (2 * Math.PI);
            if (turned <= previous)
            {
                return false;
            }

            previous = turned;
        }

        return true;
    }
}
