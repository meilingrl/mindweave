namespace Mindweave.Desktop.SummaryNetwork;

public static partial class SpatialLayout
{
    private sealed partial class Model
    {
        /// <summary>The packing grid's cell, in canvas units.</summary>
        private const double Cell = 8;

        // The placed molecules on a square grid of cells, with each cell's chamfer distance (3 per step, 4 per diagonal) to
        // the nearest occupied cell: a molecule fits where every one of its cells is farther than its gap from all placed.
        private int half;
        private int width;
        private bool[] occupied = [];
        private int[] distance = [];
        private readonly List<(int X, int Y)> placedCells = [];
        private int maxGapCells;

        private sealed class Shape
        {
            public required (double X, double Y, int Side)[] Dots { get; init; }

            public required (int X, int Y)[] Cells { get; init; }

            public required int GapCells { get; init; }

            public required double RootX { get; init; }

            public required double RootY { get; init; }
        }

        /// <summary>
        /// Packs whole explorations: the heaviest in the middle, then the others outward from it in time, later and earlier
        /// in turn, each beside its neighbour in time. Every exploration is tried at <see cref="SpatialLayoutSettings.Rotations"/>
        /// turns; among the places where it keeps its gap, the one with the shortest time thread, the gentlest pull to the
        /// middle and the least thread over drawn molecules wins.
        /// </summary>
        private void Pack()
        {
            var heaviest = roots.OrderByDescending(r => weight[r]).ThenBy(r => r).First();
            var middle = roots.IndexOf(heaviest);
            var order = new List<(int Root, int Neighbour)> { (heaviest, -1) };
            for (int later = middle + 1, earlier = middle - 1; later < roots.Count || earlier >= 0;)
            {
                if (later < roots.Count)
                {
                    order.Add((roots[later], roots[later - 1]));
                    later++;
                }

                if (earlier >= 0)
                {
                    order.Add((roots[earlier], roots[earlier + 1]));
                    earlier--;
                }
            }

            maxGapCells = roots.Max(r => GapCells(r));
            foreach (var (root, neighbour) in order)
            {
                var (cx, cy) = Centroid(root);
                Translate(root, -cx, -cy);

                // A single dot looks the same at every turn; two or three dots need fewer turns than a big molecule.
                var turns = size[root] == 1 ? 1 : size[root] <= 3 ? Math.Min(4, s.Rotations) : s.Rotations;
                var shapes = new Shape[turns];
                for (var k = 0; k < turns; k++)
                {
                    if (k > 0)
                    {
                        Rotate(root, 0, 0, 2 * Math.PI / turns);
                        ChooseLabels(start[root], end[root], threads: false);
                    }

                    shapes[k] = Capture(root);
                }

                if (occupied.Length == 0)
                {
                    StartGrid(shapes[0]);
                }

                var (best, bx, by) = neighbour < 0 ? (shapes[0], 0, 0) : Search(shapes, x[neighbour], y[neighbour]);

                for (var k = start[root]; k < end[root]; k++)
                {
                    var i = preorder[k];
                    var (px, py, place) = best.Dots[k - start[root]];
                    x[i] = px + (bx * Cell);
                    y[i] = py + (by * Cell);
                    side[i] = place;
                }

                Commit(best, bx, by);
            }
        }

        private int GapCells(int root) => (int)Math.Ceiling((s.GapTree + (s.GapTreeGrow * Math.Log2(1 + size[root]))) / Cell);

        /// <summary>
        /// The best place for an exploration. Places are its centre's grid cell, in rings outward from its neighbour's root
        /// on a lattice of every second cell, walked once for all turns: a place is ruled out at once where no turn's core
        /// can be clear; otherwise each turn is tried there. The walk stops a band past the first place that fits, or once
        /// no farther ring can win; the best place is then refined among the cells round it.
        /// </summary>
        private (Shape Best, int X, int Y) Search(Shape[] shapes, double px, double py)
        {
            const int Step = 2;
            var best = shapes[0];
            int bestX = 0, bestY = 0;
            var bestScore = double.MaxValue;

            // Every turn's core cell lies within `core` cells of the centre, and a chamfer distance changes by at most 4 a
            // cell, so a place whose centre is no farther than limit - 4·core from anything placed fits at no turn.
            var core = shapes.Max(sh => Math.Max(Math.Abs(sh.Cells[0].X), Math.Abs(sh.Cells[0].Y)) + 1);
            var screen = (shapes[0].GapCells * 3) - (4 * core);
            var rootReach = shapes.Max(sh => Math.Sqrt((sh.RootX * sh.RootX) + (sh.RootY * sh.RootY)));

            void Try(Shape shape, int ox, int oy)
            {
                var (fx, fy) = shape.Cells[0];
                var gx = fx + ox + half;
                var gy = fy + oy + half;
                if (gx >= 0 && gy >= 0 && gx < width && gy < width && distance[(gy * width) + gx] <= shape.GapCells * 3)
                {
                    return;
                }

                var rx = shape.RootX + (ox * Cell);
                var ry = shape.RootY + (oy * Cell);
                var score = Math.Sqrt(((rx - px) * (rx - px)) + ((ry - py) * (ry - py))) + (s.CentrePull * Cell * Math.Sqrt((ox * ox) + (oy * oy)));
                if (score >= bestScore || !Fits(shape, ox, oy))
                {
                    return;
                }

                score += s.ThreadOverShape * ThreadHits(px, py, rx, ry);
                if (score < bestScore)
                {
                    (bestScore, best, bestX, bestY) = (score, shape, ox, oy);
                }
            }

            var tx = (int)Math.Round(px / Cell);
            var ty = (int)Math.Round(py / Cell);
            var firstRing = -1;
            for (var rho = 0; ; rho++)
            {
                // On ring rho the thread is at least rho·Step cells less a root's distance from its centre.
                var radius = rho * Step;
                if (((radius - 1) * Cell) - rootReach >= bestScore || (firstRing >= 0 && radius > firstRing + s.SearchBand))
                {
                    break;
                }

                var before = bestScore;
                foreach (var (dx, dy) in Ring(rho))
                {
                    var ox = tx + (dx * Step);
                    var oy = ty + (dy * Step);
                    var gx = ox + half;
                    var gy = oy + half;
                    if (gx >= 0 && gy >= 0 && gx < width && gy < width && distance[(gy * width) + gx] <= screen)
                    {
                        continue;
                    }

                    foreach (var shape in shapes)
                    {
                        Try(shape, ox, oy);
                    }
                }

                if (firstRing < 0 && bestScore < before)
                {
                    firstRing = radius;
                }
            }

            var (cx, cy, chosen) = (bestX, bestY, best);
            for (var dy = -Step + 1; dy < Step; dy++)
            {
                for (var dx = -Step + 1; dx < Step; dx++)
                {
                    Try(chosen, cx + dx, cy + dy);
                }
            }

            return (best, bestX, bestY);
        }

        private readonly List<List<(int X, int Y)>> rings = [];

        /// <summary>The lattice points at a whole-number distance (rounded) from the origin, made once per layout and grown as needed.</summary>
        private List<(int X, int Y)> Ring(int rho)
        {
            if (rho >= rings.Count)
            {
                var reach = Math.Max(16, 2 * rho);
                rings.Clear();
                for (var k = 0; k <= reach; k++)
                {
                    rings.Add([]);
                }

                for (var y = -reach; y <= reach; y++)
                {
                    for (var x = -reach; x <= reach; x++)
                    {
                        var r = (int)Math.Round(Math.Sqrt((x * x) + (y * y)));
                        if (r <= reach)
                        {
                            rings[r].Add((x, y));
                        }
                    }
                }
            }

            return rings[rho];
        }

        private bool[] bitmap = [];
        private int bx0;
        private int by0;
        private int bw;

        /// <summary>The exploration as it now lies: the grid cells of its dots, titles and lines, nearest its centre first.</summary>
        private Shape Capture(int root)
        {
            // A bitmap over the cells the exploration can touch: its dots, its titles and the lines between its dots.
            double l0 = double.MaxValue, t0 = double.MaxValue, r0 = double.MinValue, b0 = double.MinValue;
            for (var k = start[root]; k < end[root]; k++)
            {
                var i = preorder[k];
                var (l, t, r, b) = LabelRect(i, side[i]);
                (l0, t0) = (Math.Min(l0, Math.Min(l, x[i] - reach[i])), Math.Min(t0, Math.Min(t, y[i] - reach[i])));
                (r0, b0) = (Math.Max(r0, Math.Max(r, x[i] + reach[i])), Math.Max(b0, Math.Max(b, y[i] + reach[i])));
            }

            (bx0, by0) = ToCell(l0, t0);
            var (bx1, by1) = ToCell(r0, b0);
            bw = bx1 - bx0 + 1;
            var cellsInBox = bw * (by1 - by0 + 1);
            if (bitmap.Length < cellsInBox)
            {
                bitmap = new bool[cellsInBox];
            }
            else
            {
                Array.Clear(bitmap, 0, cellsInBox);
            }

            for (var k = start[root]; k < end[root]; k++)
            {
                var i = preorder[k];
                Mark(x[i] - reach[i], y[i] - reach[i], x[i] + reach[i], y[i] + reach[i]);
                var (l, t, r, b) = LabelRect(i, side[i]);
                Mark(l, t, r, b);
                if (parent[i] >= 0)
                {
                    var p = parent[i];
                    var len = Math.Sqrt(((x[i] - x[p]) * (x[i] - x[p])) + ((y[i] - y[p]) * (y[i] - y[p])));
                    var steps = (int)Math.Ceiling(len / (Cell / 2));
                    for (var step = 0; step <= steps; step++)
                    {
                        var f = (double)step / Math.Max(1, steps);
                        var (cx, cy) = ToCell(x[p] + ((x[i] - x[p]) * f), y[p] + ((y[i] - y[p]) * f));
                        bitmap[((cy - by0) * bw) + cx - bx0] = true;
                    }
                }
            }

            var count = 0;
            for (var c = 0; c < cellsInBox; c++)
            {
                count += bitmap[c] ? 1 : 0;
            }

            var cells = new (int X, int Y)[count];
            var keys = new long[count];
            var at = 0;
            for (var c = 0; c < cellsInBox; c++)
            {
                if (!bitmap[c])
                {
                    continue;
                }

                var (cx, cy) = (bx0 + (c % bw), by0 + (c / bw));
                cells[at] = (cx, cy);
                keys[at++] = ((long)((cx * cx) + (cy * cy)) << 32) | (uint)c;
            }

            // Nearest the centre first, ties in a fixed order, so overlaps are found on the first cells and runs repeat.
            Array.Sort(keys, cells);
            return new Shape
            {
                Dots = [.. preorder[start[root]..end[root]].Select(i => (x[i], y[i], side[i]))],
                Cells = cells,
                GapCells = GapCells(root),
                RootX = x[root],
                RootY = y[root],
            };
        }

        private static (int, int) ToCell(double px, double py) => ((int)Math.Floor(px / Cell), (int)Math.Floor(py / Cell));

        private void Mark(double l, double t, double r, double b)
        {
            var (x0, y0) = ToCell(l, t);
            var (x1, y1) = ToCell(r, b);
            for (var cy = y0; cy <= y1; cy++)
            {
                Array.Fill(bitmap, true, ((cy - by0) * bw) + x0 - bx0, x1 - x0 + 1);
            }
        }

        private bool Fits(Shape shape, int ox, int oy)
        {
            var limit = shape.GapCells * 3;
            foreach (var (cx, cy) in shape.Cells)
            {
                var gx = cx + ox + half;
                var gy = cy + oy + half;
                if (gx >= 0 && gy >= 0 && gx < width && gy < width && distance[(gy * width) + gx] <= limit)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>How many cells the time thread between two roots would cross on placed molecules.</summary>
        private int ThreadHits(double x1, double y1, double x2, double y2)
        {
            var len = Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));
            var steps = (int)(len / Cell);
            var hits = 0;
            for (var k = 2; k < steps - 1; k++)
            {
                var gx = (int)Math.Floor((x1 + ((x2 - x1) * k / steps)) / Cell) + half;
                var gy = (int)Math.Floor((y1 + ((y2 - y1) * k / steps)) / Cell) + half;
                if (gx >= 0 && gy >= 0 && gx < width && gy < width && occupied[(gy * width) + gx])
                {
                    hits++;
                }
            }

            return hits;
        }

        /// <summary>A grid about four times the area of all explorations; it grows if a placement comes near its edge.</summary>
        private void StartGrid(Shape first)
        {
            var area = roots.Sum(r => (double)(end[r] - start[r])) * 60;
            Resize(Math.Max(first.Cells.Max(c => Math.Max(Math.Abs(c.X), Math.Abs(c.Y))), (int)Math.Sqrt(area)) + (4 * maxGapCells));
        }

        private void Resize(int newHalf)
        {
            half = newHalf;
            width = (2 * half) + 1;
            occupied = new bool[width * width];
            distance = new int[width * width];
            Array.Fill(distance, int.MaxValue / 2);
            foreach (var (cx, cy) in placedCells)
            {
                occupied[((cy + half) * width) + cx + half] = true;
            }

            UpdateDistances(0, 0, width - 1, width - 1);
        }

        private void Commit(Shape shape, int ox, int oy)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (var (cx, cy) in shape.Cells)
            {
                var (ax, ay) = (cx + ox, cy + oy);
                placedCells.Add((ax, ay));
                (x0, y0, x1, y1) = (Math.Min(x0, ax), Math.Min(y0, ay), Math.Max(x1, ax), Math.Max(y1, ay));
            }

            // Everything placed stays a full gap inside the grid, so cells beyond it are free.
            var margin = maxGapCells + 2;
            var need = Math.Max(Math.Max(-x0, x1), Math.Max(-y0, y1)) + margin;
            if (need > half)
            {
                Resize(Math.Max(half * 2, need));
                return;
            }

            foreach (var (cx, cy) in shape.Cells)
            {
                occupied[((cy + oy + half) * width) + cx + ox + half] = true;
            }

            UpdateDistances(x0 + half - margin, y0 + half - margin, x1 + half + margin, y1 + half + margin);
        }

        /// <summary>
        /// Chamfer distances in a window, both passes. Only distances up to the largest gap matter, and every path that
        /// short from a new cell stays inside its window, so updating just the window is enough.
        /// </summary>
        private void UpdateDistances(int wx0, int wy0, int wx1, int wy1)
        {
            (wx0, wy0, wx1, wy1) = (Math.Max(0, wx0), Math.Max(0, wy0), Math.Min(width - 1, wx1), Math.Min(width - 1, wy1));
            for (var gy = wy0; gy <= wy1; gy++)
            {
                for (var gx = wx0; gx <= wx1; gx++)
                {
                    var k = (gy * width) + gx;
                    if (occupied[k])
                    {
                        distance[k] = 0;
                    }
                }
            }

            for (var gy = wy0; gy <= wy1; gy++)
            {
                for (var gx = wx0; gx <= wx1; gx++)
                {
                    var k = (gy * width) + gx;
                    var v = distance[k];
                    if (gx > wx0)
                    {
                        v = Math.Min(v, distance[k - 1] + 3);
                    }

                    if (gy > wy0)
                    {
                        v = Math.Min(v, distance[k - width] + 3);
                        if (gx > wx0)
                        {
                            v = Math.Min(v, distance[k - width - 1] + 4);
                        }

                        if (gx < wx1)
                        {
                            v = Math.Min(v, distance[k - width + 1] + 4);
                        }
                    }

                    distance[k] = v;
                }
            }

            for (var gy = wy1; gy >= wy0; gy--)
            {
                for (var gx = wx1; gx >= wx0; gx--)
                {
                    var k = (gy * width) + gx;
                    var v = distance[k];
                    if (gx < wx1)
                    {
                        v = Math.Min(v, distance[k + 1] + 3);
                    }

                    if (gy < wy1)
                    {
                        v = Math.Min(v, distance[k + width] + 3);
                        if (gx < wx1)
                        {
                            v = Math.Min(v, distance[k + width + 1] + 4);
                        }

                        if (gx > wx0)
                        {
                            v = Math.Min(v, distance[k + width - 1] + 4);
                        }
                    }

                    distance[k] = v;
                }
            }
        }
    }
}
