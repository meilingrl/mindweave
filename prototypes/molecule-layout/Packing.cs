namespace MoleculeLayout;

/// <summary>
/// Places whole explorations (each already a finished molecule) next to each other: the heaviest in the middle, then
/// the others outward from it in time order, each turned and moved as close to its neighbour in time as it fits while
/// keeping its gap from everything placed, with a gentle pull toward the middle. Things close in time end up close in
/// space, so the time thread has no long jumps. Shapes are compared as drawn (dots, lines, titles) on a grid.
/// </summary>
public sealed class Packing(Molecule m, Workspace ws, Settings s)
{
    private const double Cell = 8;

    // The placed shapes on a square grid, with each cell's chamfer distance (3 per step, 4 per diagonal) to the nearest
    // occupied cell, so a candidate fits when every one of its cells is farther than its gap from everything placed.
    private int half;
    private int side;
    private bool[] occupied = [];
    private int[] distance = [];

    private sealed class Shape
    {
        public required (double X, double Y, int Side, double Dx, double Dy)[] Dots;
        public required List<(int X, int Y)> Cells;
        public int Radius;
        public double RootX;
        public double RootY;
    }

    public void Pack()
    {
        var d = ws.Dots;
        var roots = ws.Roots;

        // The heaviest exploration first, in the middle; then outward from it in time, later and earlier in turn, so each
        // next one has its neighbour in time already placed and can sit right beside it.
        var heaviest = roots.OrderByDescending(r => d[r].Weight).ThenBy(r => d[r].Created).First();
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

        half = (int)(roots.Sum(r => Math.Sqrt(m.TreeDots(r).Count) * 60) / Cell) + 80;
        side = (2 * half) + 1;
        occupied = new bool[side * side];
        distance = new int[side * side];
        Array.Fill(distance, int.MaxValue / 2);
        foreach (var (root, neighbour) in order)
        {
            var dots = m.TreeDots(root);
            var (cx, cy) = m.Centroid(dots);
            m.Translate(root, -cx, -cy);
            var gap = s.GapTree + (s.GapTreeGrow * Math.Log2(1 + d[root].Size));
            var shapes = new List<Shape>();
            for (var k = 0; k < s.Rotations; k++)
            {
                if (k > 0)
                {
                    m.Rotate(root, 0, 0, 2 * Math.PI / s.Rotations);
                    m.ChooseTreeLabels(root);
                }

                shapes.Add(Capture(root, gap));
            }

            Shape best = shapes[0];
            int bestX = 0, bestY = 0;
            if (neighbour >= 0)
            {
                // For each turn, search outward from the place that would put this root on its neighbour's root; keep the
                // nearest fits, scored by the length of the time thread plus a gentle pull toward the middle.
                var (px, py) = (d[neighbour].X, d[neighbour].Y);
                var bestScore = double.MaxValue;
                foreach (var shape in shapes)
                {
                    var tx = (int)Math.Round((px - shape.RootX) / Cell);
                    var ty = (int)Math.Round((py - shape.RootY) / Cell);
                    var firstRing = -1;
                    for (var rho = 0; firstRing < 0 || rho <= firstRing + s.SearchBand; rho++)
                    {
                        var samples = rho == 0 ? 1 : (int)Math.Ceiling(2 * Math.PI * rho);
                        for (var a = 0; a < samples; a++)
                        {
                            var angle = 2 * Math.PI * a / samples;
                            var ox = tx + (int)Math.Round(rho * Math.Cos(angle));
                            var oy = ty + (int)Math.Round(rho * Math.Sin(angle));
                            if (!Fits(shape, ox, oy))
                            {
                                continue;
                            }

                            firstRing = firstRing < 0 ? rho : firstRing;
                            var rx = shape.RootX + (ox * Cell) - px;
                            var ry = shape.RootY + (oy * Cell) - py;
                            var score = Math.Sqrt((rx * rx) + (ry * ry)) + (s.CentrePull * Cell * Math.Sqrt((ox * ox) + (oy * oy)))
                                + (s.ThreadOverShape * ThreadHits(px, py, shape.RootX + (ox * Cell), shape.RootY + (oy * Cell)));
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = shape;
                                bestX = ox;
                                bestY = oy;
                            }
                        }
                    }
                }
            }

            for (var k = 0; k < dots.Count; k++)
            {
                var (x, y, side, ldx, ldy) = best.Dots[k];
                var dot = d[dots[k]];
                dot.X = x + (bestX * Cell);
                dot.Y = y + (bestY * Cell);
                dot.LabelSide = side;
                dot.LabelDx = ldx;
                dot.LabelDy = ldy;
            }

            foreach (var (x, y) in best.Cells)
            {
                var gx = x + bestX + half;
                var gy = y + bestY + half;
                if (gx >= 0 && gy >= 0 && gx < side && gy < side)
                {
                    occupied[(gy * side) + gx] = true;
                }
            }

            UpdateDistances();
        }
    }

    /// <summary>How many grid cells the time thread from a placed root to a candidate root would cross on placed shapes.</summary>
    private int ThreadHits(double x1, double y1, double x2, double y2)
    {
        var len = Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));
        var steps = (int)(len / Cell);
        var hits = 0;
        for (var k = 2; k < steps - 1; k++)
        {
            var gx = (int)Math.Floor((x1 + ((x2 - x1) * k / steps)) / Cell) + half;
            var gy = (int)Math.Floor((y1 + ((y2 - y1) * k / steps)) / Cell) + half;
            if (gx >= 0 && gy >= 0 && gx < side && gy < side && occupied[(gy * side) + gx])
            {
                hits++;
            }
        }

        return hits;
    }

    private bool Fits(Shape shape, int ox, int oy)
    {
        var limit = shape.Radius * 3;
        foreach (var (x, y) in shape.Cells)
        {
            var gx = x + ox + half;
            var gy = y + oy + half;
            if (gx >= 0 && gy >= 0 && gx < side && gy < side && distance[(gy * side) + gx] <= limit)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateDistances()
    {
        const int big = int.MaxValue / 2;
        for (var k = 0; k < distance.Length; k++)
        {
            distance[k] = occupied[k] ? 0 : big;
        }

        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var k = (y * side) + x;
                var v = distance[k];
                if (x > 0) v = Math.Min(v, distance[k - 1] + 3);
                if (y > 0)
                {
                    v = Math.Min(v, distance[k - side] + 3);
                    if (x > 0) v = Math.Min(v, distance[k - side - 1] + 4);
                    if (x + 1 < side) v = Math.Min(v, distance[k - side + 1] + 4);
                }

                distance[k] = v;
            }
        }

        for (var y = side - 1; y >= 0; y--)
        {
            for (var x = side - 1; x >= 0; x--)
            {
                var k = (y * side) + x;
                var v = distance[k];
                if (x + 1 < side) v = Math.Min(v, distance[k + 1] + 3);
                if (y + 1 < side)
                {
                    v = Math.Min(v, distance[k + side] + 3);
                    if (x + 1 < side) v = Math.Min(v, distance[k + side + 1] + 4);
                    if (x > 0) v = Math.Min(v, distance[k + side - 1] + 4);
                }

                distance[k] = v;
            }
        }
    }

    /// <summary>The tree as it now lies: its cells on the grid, and the gap it must keep, in cells.</summary>
    private Shape Capture(int root, double gap)
    {
        var d = ws.Dots;
        var dots = m.TreeDots(root);
        var cells = new HashSet<(int, int)>();
        foreach (var i in dots)
        {
            var x = d[i];
            MarkRect(cells, x.X - x.Footprint, x.Y - x.Footprint, x.X + x.Footprint, x.Y + x.Footprint);
            var r = m.LabelRect(i, x.LabelSide);
            MarkRect(cells, r.L, r.T, r.R, r.B);
        }

        foreach (var (a, b) in m.TreeBonds(root))
        {
            var len = Math.Sqrt(((d[b].X - d[a].X) * (d[b].X - d[a].X)) + ((d[b].Y - d[a].Y) * (d[b].Y - d[a].Y)));
            var steps = (int)Math.Ceiling(len / (Cell / 2));
            for (var k = 0; k <= steps; k++)
            {
                var t = (double)k / Math.Max(1, steps);
                cells.Add(ToCell(d[a].X + ((d[b].X - d[a].X) * t), d[a].Y + ((d[b].Y - d[a].Y) * t)));
            }
        }

        return new Shape
        {
            Dots = [.. dots.Select(i => (d[i].X, d[i].Y, d[i].LabelSide, d[i].LabelDx, d[i].LabelDy))],
            Cells = [.. cells.OrderBy(c => (c.Item1 * c.Item1) + (c.Item2 * c.Item2)).ThenBy(c => c.Item1).ThenBy(c => c.Item2)],
            Radius = (int)Math.Ceiling(gap / Cell),
            RootX = d[root].X,
            RootY = d[root].Y,
        };
    }

    private static (int, int) ToCell(double x, double y) => ((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell));

    private static void MarkRect(HashSet<(int, int)> cells, double l, double t, double r, double b)
    {
        var (x0, y0) = ToCell(l, t);
        var (x1, y1) = ToCell(r, b);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                cells.Add((x, y));
            }
        }
    }
}
