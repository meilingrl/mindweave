namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>Where each dot of a wedge fill goes (see <see cref="FillWedges"/>).</summary>
public static partial class SpatialPacking
{
    /// <summary>Directions tried, spread evenly over a node's wedge.</summary>
    internal const int WedgeRays = 24;

    /// <summary>Free places tried along each direction, nearest first.</summary>
    internal const int WedgeGaps = 3;

    /// <summary>How much a branch line's length counts, against the distance from the root, when a dot picks its place.</summary>
    internal const double LineWeight = 0.3;

    /// <summary>How much straying from the middle of its wedge counts, per radian and unit of distance from the root, for a dot with siblings.</summary>
    internal const double MidWeight = 0.3;

    /// <summary>How much pushing the siblings still to come away from the root counts, per unit of distance.</summary>
    internal const double LiftWeight = 1;

    /// <summary>What a crossing costs, in canvas units of distance.</summary>
    internal const double CrossingCost = 1000;

    /// <summary>What it costs to pass beyond a placed sibling whose children are still to come, in its wedge.</summary>
    internal const double OcclusionCost = 500;

    /// <summary>What a line through a dot, or a dot on a line, costs.</summary>
    internal const double ThroughCost = 100;

    /// <summary>How far beyond a line a sibling still to come must keep its dot, beyond the dot's radius.</summary>
    internal const double FloorGap = 4;

    /// <summary>Points sampled along a line to find the wedges it crosses.</summary>
    private const int LineSamples = 16;

    /// <summary>
    /// The dots, footprints and branch lines placed so far, and the search for the next dot's place. Each node has a
    /// wedge round the root (<see cref="Low"/> to <see cref="High"/>, radians) and a floor: its dot may come no nearer
    /// the root than its parent's, nor than any line already drawn across its wedge.
    /// </summary>
    private sealed partial class Placing(Forest forest)
    {
        private readonly double _maxDot = forest.Nodes.Length > 0 ? forest.Nodes.Max(m => m.DotRadius) : 0;
        private readonly List<Circle> _placed = [];
        private readonly List<LineBox> _lines = [];
        private readonly List<PlacedDot> _dots = [];
        private readonly bool[] _done = new bool[forest.Nodes.Length];
        private readonly double[] _floor = new double[forest.Nodes.Length];
        private readonly List<(double Low, double High)> _blocked = [];
        private readonly List<Circle> _near = [];
        private readonly List<double> _angles = [];
        private readonly Dictionary<int, double> _raised = [];
        private readonly List<(double Base, double X, double Y)> _candidates = [];

        /// <summary>Dot centres.</summary>
        public double[] X { get; } = new double[forest.Nodes.Length];

        public double[] Y { get; } = new double[forest.Nodes.Length];

        public double[] Low { get; } = new double[forest.Nodes.Length];

        public double[] High { get; } = new double[forest.Nodes.Length];

        /// <summary>Places a dot, and raises the floors of the siblings still to come whose wedges its line crosses.</summary>
        public void Add(int i, double x, double y)
        {
            (X[i], Y[i]) = (x, y);
            _placed.Add(new Circle(x + forest.Offset[i], y, forest.Radius[i]));
            _dots.Add(new PlacedDot(x, y, forest.Nodes[i].DotRadius, i));
            _done[i] = true;
            var p = forest.Parent[i];
            if (p < 0)
            {
                return;
            }

            _lines.Add(new LineBox(Math.Min(X[p], x), Math.Max(X[p], x), Math.Min(Y[p], y), Math.Max(Y[p], y), p, i));
            for (var k = 0; k <= LineSamples; k++)
            {
                var s = (double)k / LineSamples;
                var (sx, sy) = (X[p] + (s * (x - X[p])), Y[p] + (s * (y - Y[p])));
                var w = SiblingAt(p, Math.Atan2(sy, sx));
                if (w >= 0 && !_done[w])
                {
                    _floor[w] = Math.Max(_floor[w], Math.Sqrt((sx * sx) + (sy * sy)) + forest.Nodes[w].DotRadius + FloorGap);
                }
            }
        }

        /// <summary>The child of <paramref name="p"/> whose wedge holds the angle, or -1: the children's wedges follow one another.</summary>
        private int SiblingAt(int p, double angle)
        {
            var children = forest.Children[p];
            var start = Low[children[0]];
            while (angle < start)
            {
                angle += 2 * Math.PI;
            }

            var (lo, hi) = (0, children.Count - 1);
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (High[children[mid]] < angle)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return angle <= High[children[lo]] ? children[lo] : -1;
        }

        /// <summary>
        /// The place for node <paramref name="i"/>'s dot. Candidates lie in its wedge, at or beyond its floor, where its
        /// footprint overlaps nothing: along <see cref="WedgeRays"/> directions and the two beside its parent, the first
        /// <see cref="WedgeGaps"/> free places each. Each scores its distance from the root, a little of its line's
        /// length and of its straying from the wedge's middle; then crossings, lines through dots, passing beyond a
        /// sibling that still has children to come, and the floors it would raise. A place that would break the
        /// siblings' creation order is taken only when nothing else is left.
        /// </summary>
        public (double X, double Y) Place(int i)
        {
            var p = forest.Parent[i];
            var (offset, radius) = (forest.Offset[i], forest.Radius[i]);
            var reachP = Math.Sqrt(Square(X[p]) + Square(Y[p]));
            var floor = Math.Max(_floor[i], reachP);
            _angles.Clear();
            for (var k = 0; k < WedgeRays; k++)
            {
                _angles.Add(Low[i] + ((High[i] - Low[i]) * (k + 0.5) / WedgeRays));
            }

            // Beside the parent, where the evenly spread directions fall far apart.
            if (reachP > 0)
            {
                var parentAngle = Math.Atan2(Y[p], X[p]);
                while (parentAngle < Low[i])
                {
                    parentAngle += 2 * Math.PI;
                }

                var step = 2 * Math.Asin(Math.Min(1, (forest.Radius[p] + radius) / (2 * reachP)));
                foreach (var angle in new[] { parentAngle - step, parentAngle + step, parentAngle - (2 * Math.PI) + step, parentAngle - (2 * Math.PI) - step })
                {
                    if (angle >= Low[i] && angle <= High[i])
                    {
                        _angles.Add(angle);
                    }
                }
            }

            GatherNear(offset, radius, floor, (Low[i] + High[i]) / 2, (High[i] - Low[i]) / 2);
            _candidates.Clear();
            var middle = (Low[i] + High[i]) / 2;
            var siblings = forest.Children[p].Count > 1;
            foreach (var angle in _angles)
            {
                var (ux, uy) = (Math.Cos(angle), Math.Sin(angle));
                foreach (var at in FreePlaces(ux, uy, offset, radius, floor))
                {
                    var (x, y) = (at * ux, at * uy);
                    var score = at + (LineWeight * Math.Sqrt(Square(x - X[p]) + Square(y - Y[p])));
                    if (siblings)
                    {
                        score += MidWeight * at * Math.Abs(angle - middle);
                    }

                    _candidates.Add((score, x, y));
                }
            }

            _candidates.Sort((a, b) => a.Base.CompareTo(b.Base));
            var best = (X: _candidates[0].X, Y: _candidates[0].Y, Score: double.PositiveInfinity);
            var fallback = best;
            foreach (var (score, x, y) in _candidates)
            {
                if (score >= best.Score)
                {
                    break;
                }

                if (!ClockwiseInOrder(forest.Children[p], X[p] + forest.Offset[p], Y[p], new Siblings(this, forest.Offset, i, x + offset, y)))
                {
                    if (fallback.Score == double.PositiveInfinity)
                    {
                        fallback = (x, y, score);
                    }

                    continue;
                }

                // A place whose costs so far already reach the best cannot win: the rest only adds.
                var total = score + Penalty(i, x, y, score, best.Score);
                if (total >= best.Score)
                {
                    continue;
                }

                total += LiftWeight * Lift(i, x, y);
                if (total < best.Score)
                {
                    best = (x, y, total);
                }
            }

            return best.Score < double.PositiveInfinity ? (best.X, best.Y) : (fallback.X, fallback.Y);
        }

        /// <summary>
        /// Gathers into <c>_near</c> the placed footprints that could block a dot's footprint
        /// (<paramref name="offset"/> right of it, <paramref name="radius"/>) on some ray of its wedge (centred on
        /// <paramref name="middle"/>, <paramref name="half"/> wide either side) at or beyond <paramref name="floor"/>:
        /// those whose reach, seen from the root, overlaps the wedge and whose far edge lies beyond the floor. The rest
        /// block no ray there, so <see cref="FreePlaces"/> need not look at them.
        /// </summary>
        private void GatherNear(double offset, double radius, double floor, double middle, double half)
        {
            _near.Clear();
            foreach (var c in _placed)
            {
                var (cx, cy) = (c.X - offset, c.Y);
                var (distance, reach) = (Math.Sqrt((cx * cx) + (cy * cy)), radius + c.R);
                if (distance + reach <= floor)
                {
                    continue;
                }

                if (distance <= reach || half >= Math.PI
                    || Math.Abs(Math.IEEERemainder(Math.Atan2(cy, cx) - middle, 2 * Math.PI)) <= half + Math.Asin(reach / distance) + AngleSlack)
                {
                    _near.Add(c);
                }
            }
        }

        /// <summary>
        /// Along the ray from the root in direction (<paramref name="ux"/>, <paramref name="uy"/>), the first
        /// <see cref="WedgeGaps"/> distances at or beyond <paramref name="floor"/> where a dot's footprint
        /// (<paramref name="offset"/> right of it, <paramref name="radius"/>) overlaps nothing placed.
        /// </summary>
        private List<double> FreePlaces(double ux, double uy, double offset, double radius, double floor)
        {
            _blocked.Clear();
            foreach (var c in _near)
            {
                var (cx, cy) = (c.X - offset, c.Y);
                var along = (ux * cx) + (uy * cy);
                var d = (along * along) - ((cx * cx) + (cy * cy) - Square(radius + c.R));
                if (d > 0)
                {
                    _blocked.Add((along - Math.Sqrt(d), along + Math.Sqrt(d)));
                }
            }

            _blocked.Sort((a, b) => a.Low.CompareTo(b.Low));
            var places = new List<double>(WedgeGaps);
            var t = floor;
            foreach (var (lo, hi) in _blocked)
            {
                if (hi <= t)
                {
                    continue;
                }

                if (lo > t)
                {
                    places.Add(t);
                    if (places.Count >= WedgeGaps)
                    {
                        return places;
                    }
                }

                t = Math.Max(t, hi + TouchTolerance);
            }

            places.Add(t);
            return places;
        }
    }
}
