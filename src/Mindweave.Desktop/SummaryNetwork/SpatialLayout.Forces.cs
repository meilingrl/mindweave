namespace Mindweave.Desktop.SummaryNetwork;

public static partial class SpatialLayout
{
    private sealed partial class Model
    {
        private readonly Grid dotGrid = new();
        private readonly Grid labelGrid = new();
        private readonly Grid bondGrid = new();
        private readonly List<int> near = [];
        private int[] seen = [];
        private int stamp;

        /// <summary>The label block of a dot for a side, grown by a margin, in canvas units.</summary>
        private (double L, double T, double R, double B) LabelRect(int i, int place, double margin = 0)
        {
            var (l, t) = LabelOffset(place, reach[i], labelW[i], labelH[i]);
            return (x[i] + l - margin, y[i] + t - margin, x[i] + l + labelW[i] + margin, y[i] + t + labelH[i] + margin);
        }

        /// <summary>Everything any of a dot's label places may cover: what the label grid files it under.</summary>
        private (double L, double T, double R, double B) LabelReach(int i)
        {
            var w = reach[i] + LabelGap + labelW[i] + 4;
            var h = reach[i] + LabelGap + labelH[i] + 4;
            return (x[i] - w, y[i] - h, x[i] + w, y[i] + h);
        }

        private (double L, double T, double R, double B) BondBox(int child)
        {
            var p = parent[child];
            return (Math.Min(x[p], x[child]), Math.Min(y[p], y[child]), Math.Max(x[p], x[child]), Math.Max(y[p], y[child]));
        }

        private int NextStamp()
        {
            if (seen.Length != n)
            {
                seen = new int[n];
            }

            return ++stamp;
        }

        /// <summary>The dots of the range (or of the whole workspace) as the grids see them.</summary>
        private void BuildGrids(int from, int to, bool labels)
        {
            var span = preorder.AsSpan(from, to - from);
            dotGrid.Build(span, i => (x[i], y[i], x[i], y[i]), 64);
            if (labels)
            {
                labelGrid.Build(span, LabelReach, 96);
                bondGrid.Build(span, i => parent[i] >= 0 ? BondBox(i) : (double.NaN, 0d, 0d, 0d), 96);
            }
        }

        /// <summary>Pushes apart dots closer than their gap, titles off other dots and titles, and dots off lines through their titles.</summary>
        private void Repel(int from, int to, bool labels)
        {
            for (var k = from; k < to; k++)
            {
                dx[preorder[k]] = 0;
                dy[preorder[k]] = 0;
            }

            BuildGrids(from, to, labels);
            var root = rootOf[preorder[from]];
            var reachOfGaps = (2 * maxReach) + s.GapSibling + (s.GapSiblingGrow * Math.Log2(1 + size[root])) + s.GapNested;
            for (var k = from; k < to; k++)
            {
                var u = preorder[k];
                var mark = NextStamp();
                dotGrid.Query(x[u] - reachOfGaps, y[u] - reachOfGaps, x[u] + reachOfGaps, y[u] + reachOfGaps, near, seen, mark);
                foreach (var v in near)
                {
                    if (v <= u)
                    {
                        continue;
                    }

                    var g = Gap(u, v);
                    if (g < 0)
                    {
                        continue;
                    }

                    var want = reach[u] + reach[v] + g;
                    var ex = x[v] - x[u];
                    var ey = y[v] - y[u];
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
                for (var k = from; k < to; k++)
                {
                    var u = preorder[k];
                    PushOffLabel(u);
                }
            }

            for (var k = from; k < to; k++)
            {
                var i = preorder[k];
                var m = Math.Sqrt((dx[i] * dx[i]) + (dy[i] * dy[i]));
                var cap = m > 24 ? 24 / m : 1;
                x[i] += dx[i] * cap;
                y[i] += dy[i] * cap;
            }
        }

        /// <summary>A title never covers another dot or title; a line through it pushes its dot off the line.</summary>
        private void PushOffLabel(int u)
        {
            var (l, t, r, b) = LabelRect(u, side[u], 3);
            var mark = NextStamp();
            dotGrid.Query(l - maxReach - 3, t - maxReach - 3, r + maxReach + 3, b + maxReach + 3, near, seen, mark);
            foreach (var v in near)
            {
                if (v == u)
                {
                    continue;
                }

                var qx = Math.Clamp(x[v], l, r);
                var qy = Math.Clamp(y[v], t, b);
                var ex = x[v] - qx;
                var ey = y[v] - qy;
                var dist = Math.Sqrt((ex * ex) + (ey * ey));
                var want = reach[v] + 3;
                if (dist >= want)
                {
                    continue;
                }

                if (dist < 1e-6)
                {
                    // The dot's centre is inside the block: leave by the nearest side.
                    var (gap, ox, oy) = new[] { (x[v] - l, -1.0, 0.0), (r - x[v], 1.0, 0.0), (y[v] - t, 0.0, -1.0), (b - y[v], 0.0, 1.0) }.MinBy(o => o.Item1);
                    (ex, ey, dist, want) = (ox, oy, 0, want + gap);
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

            mark = NextStamp();
            labelGrid.Query(l, t, r, b, near, seen, mark);
            foreach (var v in near)
            {
                if (v <= u)
                {
                    continue;
                }

                var (l2, t2, r2, b2) = LabelRect(v, side[v], 3);
                var ox = Math.Min(r, r2) - Math.Max(l, l2);
                var oy = Math.Min(b, b2) - Math.Max(t, t2);
                if (ox <= 0 || oy <= 0)
                {
                    continue;
                }

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

            var rect = LabelRect(u, side[u], 2);
            var cx = (rect.L + rect.R) / 2;
            var cy = (rect.T + rect.B) / 2;
            mark = NextStamp();
            bondGrid.Query(rect.L, rect.T, rect.R, rect.B, near, seen, mark);
            foreach (var child in near)
            {
                var a = parent[child];
                if (a == u || child == u || !SegmentHitsRect(x[a], y[a], x[child], y[child], rect))
                {
                    continue;
                }

                var ex = x[child] - x[a];
                var ey = y[child] - y[a];
                var along = Math.Clamp((((cx - x[a]) * ex) + ((cy - y[a]) * ey)) / Math.Max(1e-9, (ex * ex) + (ey * ey)), 0, 1);
                var px = cx - (x[a] + (along * ex));
                var py = cy - (y[a] + (along * ey));
                var len = Math.Sqrt((px * px) + (py * py));
                if (len < 1e-6)
                {
                    (px, py, len) = (-ey, ex, Math.Max(1e-6, Math.Sqrt((ex * ex) + (ey * ey))));
                }

                var step = s.LineShove / len;
                dx[u] += px * step;
                dy[u] += py * step;
                dx[a] -= px * step * 0.5;
                dy[a] -= py * step * 0.5;
                dx[child] -= px * step * 0.5;
                dy[child] -= py * step * 0.5;
            }
        }

        /// <summary>
        /// Puts each title where it is clearest, heaviest dots first: away from its own lines (its widest free angle), off
        /// other dots, titles and lines, and, across the workspace, off the time thread.
        /// </summary>
        private void ChooseLabels(int from, int to, bool threads)
        {
            if (to <= from)
            {
                return;
            }

            BuildGrids(from, to, labels: true);
            var order = preorder[from..to].OrderByDescending(i => weight[i]).ThenBy(i => i).ToArray();
            var directions = new List<double>();
            foreach (var i in order)
            {
                directions.Clear();
                if (parent[i] >= 0)
                {
                    directions.Add(Math.Atan2(y[parent[i]] - y[i], x[parent[i]] - x[i]));
                }

                foreach (var c in children[i])
                {
                    directions.Add(Math.Atan2(y[c] - y[i], x[c] - x[i]));
                }

                var bestSide = 0;
                var bestCost = double.MaxValue;
                for (var place = 0; place < Sides.Count; place++)
                {
                    var cost = PlaceCost(i, place, directions, threads);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestSide = place;
                    }
                }

                side[i] = bestSide;
            }
        }

        private double PlaceCost(int i, int place, List<double> directions, bool threads)
        {
            var angle = Sides[place].Angle * Math.PI / 180;
            var cost = Sides[place].Cost;
            foreach (var dir in directions)
            {
                var off = Math.Abs(WrapAngle(angle - dir)) * 180 / Math.PI;
                cost += Math.Max(0, 75 - off) * 3;
            }

            var rect = LabelRect(i, place, 2);
            var mark = NextStamp();
            dotGrid.Query(rect.L - maxReach, rect.T - maxReach, rect.R + maxReach, rect.B + maxReach, near, seen, mark);
            foreach (var v in near)
            {
                if (v == i)
                {
                    continue;
                }

                var qx = Math.Clamp(x[v], rect.L, rect.R) - x[v];
                var qy = Math.Clamp(y[v], rect.T, rect.B) - y[v];
                if ((qx * qx) + (qy * qy) < (reach[v] + 2) * (reach[v] + 2))
                {
                    cost += 5000; // a title never covers another dot (spec §4.2)
                }
            }

            mark = NextStamp();
            labelGrid.Query(rect.L, rect.T, rect.R, rect.B, near, seen, mark);
            foreach (var v in near)
            {
                if (v == i)
                {
                    continue;
                }

                var o = LabelRect(v, side[v], 2);
                var ox = Math.Min(rect.R, o.R) - Math.Max(rect.L, o.L);
                var oy = Math.Min(rect.B, o.B) - Math.Max(rect.T, o.T);
                if (ox > 0 && oy > 0)
                {
                    cost += 1000 + (ox * oy * 0.5);
                }
            }

            mark = NextStamp();
            bondGrid.Query(rect.L, rect.T, rect.R, rect.B, near, seen, mark);
            foreach (var child in near)
            {
                var a = parent[child];
                if (SegmentHitsRect(x[a], y[a], x[child], y[child], rect))
                {
                    cost += 400;
                }
            }

            if (threads)
            {
                for (var k = 0; k + 1 < roots.Count; k++)
                {
                    var (a, b) = (roots[k], roots[k + 1]);
                    if (SegmentHitsRect(x[a], y[a], x[b], y[b], rect))
                    {
                        cost += 120;
                    }
                }
            }

            return cost;
        }
    }

    /// <summary>Whether a segment passes through a rectangle (Liang–Barsky).</summary>
    internal static bool SegmentHitsRect(double x1, double y1, double x2, double y2, (double L, double T, double R, double B) r)
    {
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

    /// <summary>
    /// A uniform grid over boxes (a box with a NaN left edge is left out), rebuilt in linear time: each box is filed under
    /// every cell it touches, and a query returns each box touching the query area once.
    /// </summary>
    private sealed class Grid
    {
        private double cell;
        private double minX;
        private double minY;
        private int nx;
        private int ny;
        private int[] cellStart = [];
        private int[] items = [];
        private (int X0, int Y0, int X1, int Y1)[] spans = [];
        private (double L, double T, double R, double B)[] boxes = [];
        private int[] fill = [];

        public void Build(ReadOnlySpan<int> ids, Func<int, (double L, double T, double R, double B)> box, double size)
        {
            cell = size;
            if (spans.Length < ids.Length)
            {
                spans = new (int, int, int, int)[ids.Length];
                boxes = new (double, double, double, double)[ids.Length];
            }

            (minX, minY) = (double.MaxValue, double.MaxValue);
            var (maxX, maxY) = (double.MinValue, double.MinValue);
            for (var k = 0; k < ids.Length; k++)
            {
                boxes[k] = box(ids[k]);
                if (double.IsNaN(boxes[k].L))
                {
                    continue;
                }

                minX = Math.Min(minX, boxes[k].L);
                minY = Math.Min(minY, boxes[k].T);
                maxX = Math.Max(maxX, boxes[k].R);
                maxY = Math.Max(maxY, boxes[k].B);
            }

            if (minX > maxX)
            {
                (nx, ny, minX, minY) = (1, 1, 0, 0);
                cellStart = [0, 0];
                return;
            }

            nx = Math.Max(1, (int)((maxX - minX) / cell) + 1);
            ny = Math.Max(1, (int)((maxY - minY) / cell) + 1);
            var cells = nx * ny;
            if (cellStart.Length < cells + 1)
            {
                cellStart = new int[cells + 1];
            }
            else
            {
                Array.Clear(cellStart, 0, cells + 1);
            }

            var total = 0;
            for (var k = 0; k < ids.Length; k++)
            {
                if (double.IsNaN(boxes[k].L))
                {
                    spans[k] = (0, 0, -1, -1);
                    continue;
                }

                var span = (Cx(boxes[k].L), Cy(boxes[k].T), Cx(boxes[k].R), Cy(boxes[k].B));
                spans[k] = span;
                for (var cy = span.Item2; cy <= span.Item4; cy++)
                {
                    for (var cx = span.Item1; cx <= span.Item3; cx++)
                    {
                        cellStart[(cy * nx) + cx + 1]++;
                        total++;
                    }
                }
            }

            for (var c = 0; c < cells; c++)
            {
                cellStart[c + 1] += cellStart[c];
            }

            if (items.Length < total)
            {
                items = new int[total];
            }

            if (fill.Length < cells)
            {
                fill = new int[cells];
            }
            else
            {
                Array.Clear(fill, 0, cells);
            }
            for (var k = 0; k < ids.Length; k++)
            {
                var (x0, y0, x1, y1) = spans[k];
                for (var cy = y0; cy <= y1; cy++)
                {
                    for (var cx = x0; cx <= x1; cx++)
                    {
                        var c = (cy * nx) + cx;
                        items[cellStart[c] + fill[c]++] = ids[k];
                    }
                }
            }
        }

        private int Cx(double v) => Math.Clamp((int)Math.Floor((v - minX) / cell), 0, nx - 1);

        private int Cy(double v) => Math.Clamp((int)Math.Floor((v - minY) / cell), 0, ny - 1);

        /// <summary>Fills <paramref name="into"/> with each box filed in a cell the area touches, once (by <paramref name="mark"/> in <paramref name="seen"/>).</summary>
        public void Query(double l, double t, double r, double b, List<int> into, int[] seen, int mark)
        {
            into.Clear();
            if (nx * ny == 0 || cellStart.Length < 2)
            {
                return;
            }

            var (x0, y0, x1, y1) = (Cx(l), Cy(t), Cx(r), Cy(b));
            for (var cy = y0; cy <= y1; cy++)
            {
                for (var cx = x0; cx <= x1; cx++)
                {
                    var c = (cy * nx) + cx;
                    for (var k = cellStart[c]; k < cellStart[c + 1]; k++)
                    {
                        var item = items[k];
                        if (seen[item] != mark)
                        {
                            seen[item] = mark;
                            into.Add(item);
                        }
                    }
                }
            }
        }
    }
}
