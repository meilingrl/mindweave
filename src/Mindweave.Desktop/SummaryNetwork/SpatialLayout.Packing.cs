namespace Mindweave.Desktop.SummaryNetwork;

public static partial class SpatialLayout
{
    private sealed partial class Model
    {
        /// <summary>
        /// The packing grid's cell, in canvas units: 8, or coarser for a huge exploration, so its shape spans at most about
        /// <see cref="MaxShapeCells"/> cells and the grid stays small.
        /// </summary>
        private double Cell = 8;

        private const double MaxShapeCells = 256;

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

            public required int GapCells { get; set; }

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

            SetUpGrid();
            foreach (var (root, neighbour) in order)
            {
                PlaceNew(root, neighbour);
            }
        }

        /// <summary>The packing grid's cell, from the largest exploration, and the widest gap any exploration keeps.</summary>
        private void SetUpGrid()
        {
            var largest = roots.Max(r =>
            {
                double l = double.MaxValue, t = double.MaxValue, rr = double.MinValue, b = double.MinValue;
                for (var k = start[r]; k < end[r]; k++)
                {
                    var i = preorder[k];
                    var extra = reach[i] + LabelGap + Math.Max(labelW[i], labelH[i]);
                    (l, t, rr, b) = (Math.Min(l, x[i] - extra), Math.Min(t, y[i] - extra), Math.Max(rr, x[i] + extra), Math.Max(b, y[i] + extra));
                }

                return Math.Max(rr - l, b - t);
            });
            Cell = Math.Max(8, Math.Ceiling(largest / MaxShapeCells));
            maxGapCells = roots.Max(r => GapCells(r));
        }

        /// <summary>
        /// Places an exploration that has no place yet: tried at several turns, beside its neighbour in time (or, the first,
        /// in the middle), where it keeps its gap from everything placed.
        /// </summary>
        private void PlaceNew(int root, int neighbour)
        {
            var (cx, cy) = Centroid(root);
            Translate(root, -cx, -cy);

            // A single dot looks the same at every turn; two or three dots need fewer turns than a big molecule.
            var turns = size[root] == 1 ? 1 : size[root] <= 3 ? Math.Min(4, s.Rotations) : s.Rotations;
            // Titles were settled in the unturned shape; a turn whose titles then cover a dot of their own is not offered.
            var options = new List<Shape>(turns);
            for (var k = 0; k < turns; k++)
            {
                if (k > 0)
                {
                    Rotate(root, 0, 0, 2 * Math.PI / turns);
                    ChooseLabels(start[root], end[root], threads: false);
                }

                if (k == 0 || LabelsClear(start[root], end[root]))
                {
                    options.Add(Capture(root));
                }
            }

            var shapes = options.ToArray();

            if (occupied.Length == 0)
            {
                StartGrid(shapes[0]);
            }

            var (best, bx, by) = neighbour < 0 ? (shapes[0], 0, 0) : Search(shapes, x[neighbour], y[neighbour]);
            Apply(root, best, bx, by);
        }

        /// <summary>Moves an exploration to a chosen shape (its turn and titles) and offset, and marks it on the grid.</summary>
        private void Apply(int root, Shape shape, int bx, int by)
        {
            for (var k = start[root]; k < end[root]; k++)
            {
                var i = preorder[k];
                var (px, py, place) = shape.Dots[k - start[root]];
                x[i] = px + (bx * Cell);
                y[i] = py + (by * Cell);
                side[i] = place;
            }

            Commit(shape, bx, by);
        }

        /// <summary>
        /// Puts explorations back where they were (design session-network §16, option C). Each saved exploration returns to
        /// its place: its root dot where it was, its oldest branch pointing the same way, its molecule laid out again.
        /// Changed ones go first and redraw in place; an unchanged one they grew into steps aside to the nearest place where
        /// it keeps part of its gap. New explorations go beside their neighbour in time.
        /// </summary>
        private void PlaceStable()
        {
            SetUpGrid();
            // Unchanged explorations first, so what the reader has seen stays put; a changed one that no longer keeps part of
            // its gap where it was steps aside to the nearest place that does (no large shifts of the rest, owner 2026-11-23).
            var known = roots.Where(saved.ContainsKey).OrderBy(r => saved[r].Size == size[r] ? 0 : 1).ThenBy(r => roots.IndexOf(r)).ToList();
            foreach (var root in known)
            {
                // Turn and move only by real amounts, so an unchanged exploration comes back to the very same coordinates.
                var place = saved[root];
                var turn = WrapAngle(place.Angle - Heading(root));
                if (children[root].Length > 0 && Math.Abs(turn) > 1e-9)
                {
                    Rotate(root, x[root], y[root], turn);
                }

                if (Math.Abs(place.X - x[root]) > 1e-9 || Math.Abs(place.Y - y[root]) > 1e-9)
                {
                    Translate(root, place.X - x[root], place.Y - y[root]);
                }
                ChooseLabels(start[root], end[root], threads: false);
                if (!LabelsClear(start[root], end[root]))
                {
                    // Turned, a title may now lie on a dot of its own exploration: settle briefly, then back to the place.
                    Settle(root);
                    Translate(root, place.X - x[root], place.Y - y[root]);
                }

                var shape = Capture(root);

                // Back in its place, an exploration may come closer to its neighbours than a new one would (no large
                // shifts, owner 2026-11-23): only when even part of its gap is not kept does it step aside.
                // The kept gap is rounded down for an unchanged exploration, so a coarser grid on a later load does not move it.
                var unchanged = saved[root].Size == size[root];
                var kept = BaseGap(root) * s.KeptGapShare / Cell;
                shape.GapCells = unchanged ? (int)Math.Floor(kept) : Math.Max(1, (int)Math.Ceiling(kept));
                if (occupied.Length == 0)
                {
                    StartGrid(shape);
                }

                var (best, bx, by) = Fits(shape, 0, 0) ? (shape, 0, 0) : Search([shape], place.X, place.Y, (0, 0), centrePull: 0, threadCost: 0);
                Apply(root, best, bx, by);
            }

            var placed = known.ToHashSet();
            foreach (var root in roots.Where(r => !placed.Contains(r)).ToList())
            {
                // The nearest exploration in time already placed: before it, else after it.
                var index = roots.IndexOf(root);
                var neighbour = Enumerable.Range(1, roots.Count)
                    .SelectMany(d => new[] { index - d, index + d })
                    .Where(k => k >= 0 && k < roots.Count && placed.Contains(roots[k]))
                    .Select(k => roots[k])
                    .DefaultIfEmpty(-1)
                    .First();
                PlaceNew(root, neighbour);
                placed.Add(root);
            }
        }

        /// <summary>The direction of an exploration's oldest branch from its root: what keeps its turn from one layout to the next.</summary>
        private double Heading(int root) => children[root].Length == 0 ? 0 : Math.Atan2(y[children[root][0]] - y[root], x[children[root][0]] - x[root]);

        /// <summary>The gap an exploration keeps round it, in canvas units, growing with its size.</summary>
        private double BaseGap(int root) => s.GapTree + (s.GapTreeGrow * Math.Log2(1 + size[root]));

        /// <summary>
        /// The gap, in cells, an exploration keeps when it is placed anew: its own gap plus room to grow
        /// (<see cref="SpatialLayoutSettings.GrowRoom"/>), so that later branches usually fit where it is (design §16).
        /// </summary>
        private int GapCells(int root) => (int)Math.Ceiling((BaseGap(root) + s.GrowRoom) / Cell);

        /// <summary>
        /// The best place for an exploration. Places are its centre's grid cell, in rings outward from its neighbour's root
        /// on a lattice of every second cell, walked once for all turns: a place is ruled out at once where no turn's core
        /// can be clear; otherwise each turn is tried there. The walk stops a band past the first place that fits, or once
        /// no farther ring can win; the best place is then refined among the cells round it.
        /// </summary>
        private (Shape Best, int X, int Y) Search(Shape[] shapes, double px, double py, (int X, int Y)? centre = null, double? centrePull = null, double? threadCost = null)
        {
            var pull = centrePull ?? s.CentrePull;
            var overShape = threadCost ?? s.ThreadOverShape;
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
                var score = Math.Sqrt(((rx - px) * (rx - px)) + ((ry - py) * (ry - py))) + (pull * Cell * Math.Sqrt((ox * ox) + (oy * oy)));
                if (score >= bestScore || !Fits(shape, ox, oy))
                {
                    return;
                }

                score += overShape > 0 ? overShape * ThreadHits(px, py, rx, ry) : 0;
                if (score < bestScore)
                {
                    (bestScore, best, bestX, bestY) = (score, shape, ox, oy);
                }
            }

            var (tx, ty) = centre ?? ((int)Math.Round(px / Cell), (int)Math.Round(py / Cell));
            if (centre is not null)
            {
                // Searching round a fixed place: the distance from it is the score, and nothing else bounds the walk.
                rootReach = 0;
            }
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

        private (int, int) ToCell(double px, double py) => ((int)Math.Floor(px / Cell), (int)Math.Floor(py / Cell));

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
