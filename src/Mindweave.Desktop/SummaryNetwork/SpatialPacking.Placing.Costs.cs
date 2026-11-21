using System.Runtime.InteropServices;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>What a place costs a dot of a wedge fill (see <see cref="FillWedges"/>).</summary>
public static partial class SpatialPacking
{
    /// <summary>Slack on the angle test that picks the footprints near a wedge, against round-off.</summary>
    private const double AngleSlack = 1e-9;

    /// <summary>A branch line placed so far, from the parent's dot to the child's, with its bounding box.</summary>
    private readonly record struct LineBox(double MinX, double MaxX, double MinY, double MaxY, int From, int To);

    /// <summary>A dot placed so far: its centre, its drawn radius and its node.</summary>
    private readonly record struct PlacedDot(double X, double Y, double Radius, int Node);

    private sealed partial class Placing
    {
        /// <summary>
        /// The creation-order check for one dot's places (<see cref="ClockwiseInOrder"/>, worked out once per dot and
        /// then in constant time per place). The siblings placed so far already lie clockwise in creation order round
        /// their parent (a place that breaks it is taken only as a last resort, and then the fan stays broken), so a
        /// new child keeps the order exactly when it falls between its nearest placed siblings in creation order:
        /// <list type="bullet">
        /// <item>
        /// after some placed sibling: its direction, turned clockwise from the first placed sibling's, lies beyond its
        /// placed predecessor's and short of its placed successor's, if any (the same sums as the full check);
        /// </item>
        /// <item>
        /// before every placed sibling: it becomes the first, and the others keep their order round it exactly when it
        /// lies in the arc from the last placed sibling round to the first.
        /// </item>
        /// </list>
        /// </summary>
        private readonly struct SiblingOrder
        {
            private readonly bool _always;
            private readonly bool _never;
            private readonly bool _before;
            private readonly double _px;
            private readonly double _py;
            private readonly double _first;
            private readonly double _low;
            private readonly double _high;

            public SiblingOrder(Placing placing, Forest forest, int i)
            {
                var p = forest.Parent[i];
                var children = forest.Children[p];
                (_always, _never, _before) = (children.Count < 3, placing._broken[p], false);
                (_px, _py) = (placing.X[p] + forest.Offset[p], placing.Y[p]);
                (_first, _low, _high) = (0, 0, double.PositiveInfinity);
                if (_always || _never)
                {
                    return;
                }

                int first = -1, last = -1, before = -1, after = -1;
                var seen = false;
                foreach (var c in children)
                {
                    if (c == i)
                    {
                        seen = true;
                        continue;
                    }

                    if (!placing._done[c])
                    {
                        continue;
                    }

                    first = first < 0 ? c : first;
                    last = c;
                    if (!seen)
                    {
                        before = c;
                    }
                    else if (after < 0)
                    {
                        after = c;
                    }
                }

                if (first < 0)
                {
                    _always = true;
                    return;
                }

                _first = Angle(placing, forest, first);
                if (before >= 0)
                {
                    _low = Turned(Angle(placing, forest, before) - _first);
                    _high = after >= 0 ? Turned(Angle(placing, forest, after) - _first) : double.PositiveInfinity;
                }
                else
                {
                    (_before, _low) = (true, Turned(Angle(placing, forest, last) - _first));
                }
            }

            /// <summary>Whether the child's footprint centred at (x, y) keeps its siblings in creation order.</summary>
            public bool Allows(double x, double y)
            {
                if (_always || _never)
                {
                    return _always;
                }

                var turned = Turned(Math.Atan2(y - _py, x - _px) - _first);
                return _before ? turned > _low : turned > _low && turned < _high;
            }

            private double Angle(Placing placing, Forest forest, int c) => Math.Atan2(placing.Y[c] - _py, placing.X[c] + forest.Offset[c] - _px);
        }

        /// <summary>How far a line to (x, y) would push the siblings still to come away from the root, in all.</summary>
        private double Lift(int i, double x, double y)
        {
            var p = forest.Parent[i];
            var reachP = Math.Sqrt(Square(X[p]) + Square(Y[p]));
            _raised.Clear();
            for (var k = 0; k <= LineSamples; k++)
            {
                var s = (double)k / LineSamples;
                var (sx, sy) = (X[p] + (s * (x - X[p])), Y[p] + (s * (y - Y[p])));
                var w = SiblingAt(p, Math.Atan2(sy, sx));
                if (w < 0 || w == i || _done[w])
                {
                    continue;
                }

                var raised = Math.Sqrt((sx * sx) + (sy * sy)) + forest.Nodes[w].DotRadius + FloorGap;
                _raised[w] = Math.Max(raised, _raised.GetValueOrDefault(w, double.NegativeInfinity));
            }

            var lift = 0.0;
            foreach (var (w, raised) in _raised)
            {
                lift += Math.Max(0, raised - Math.Max(_floor[w], reachP));
            }

            return lift;
        }

        /// <summary>
        /// The cost of the line from the parent's dot to a dot at (x, y): lines it crosses, dots it passes through or
        /// lies on, and placed siblings with children still to come that it passes beyond, in their wedges.
        /// </summary>
        /// <param name="score">The place's score before this cost.</param>
        /// <param name="best">The best total so far: once the score and the cost reach it, the rest is not counted.</param>
        private double Penalty(int i, double x, double y, double score, double best)
        {
            var p = forest.Parent[i];
            var (ax, ay) = (X[p], Y[p]);
            var dot = forest.Nodes[i].DotRadius;
            var (minX, maxX, minY, maxY) = (Math.Min(ax, x) - _maxDot, Math.Max(ax, x) + _maxDot, Math.Min(ay, y) - _maxDot, Math.Max(ay, y) + _maxDot);
            var cost = 0.0;
            // Lines and dots are kept with their boxes side by side in memory: most fall outside this line's box.
            foreach (ref readonly var line in CollectionsMarshal.AsSpan(_lines))
            {
                if (line.From == p || line.To == p || line.MaxX < minX || line.MinX > maxX || line.MaxY < minY || line.MinY > maxY)
                {
                    continue;
                }

                var (cx, cy, dx, dy) = (X[line.From], Y[line.From], X[line.To], Y[line.To]);
                if (Crosses(ax, ay, x, y, cx, cy, dx, dy))
                {
                    cost += CrossingCost;
                }
                else if (Near(x, y, cx, cy, dx, dy, dot))
                {
                    cost += ThroughCost;
                }
                else
                {
                    continue;
                }

                if (score + cost >= best)
                {
                    return cost;
                }
            }

            foreach (ref readonly var m in CollectionsMarshal.AsSpan(_dots))
            {
                if (m.Node != p && m.X >= minX && m.X <= maxX && m.Y >= minY && m.Y <= maxY && Near(m.X, m.Y, ax, ay, x, y, m.Radius))
                {
                    cost += ThroughCost;
                    if (score + cost >= best)
                    {
                        return cost;
                    }
                }
            }

            foreach (var m in forest.Children[p])
            {
                if (!_done[m] || forest.Children[m].Count == 0)
                {
                    continue;
                }

                var reach = Math.Sqrt(Square(X[m]) + Square(Y[m]));
                for (var k = 1; k < LineSamples; k++)
                {
                    var s = (double)k / LineSamples;
                    var (sx, sy) = (ax + (s * (x - ax)), ay + (s * (y - ay)));
                    if (Square(sx) + Square(sy) > reach * reach && Within(Math.Atan2(sy, sx), Low[m], High[m]))
                    {
                        cost += OcclusionCost;
                        break;
                    }
                }
            }

            return cost;
        }

        /// <summary>Whether an angle lies in the wedge from <paramref name="low"/> to <paramref name="high"/>, round the circle.</summary>
        private static bool Within(double angle, double low, double high)
        {
            while (angle < low)
            {
                angle += 2 * Math.PI;
            }

            return angle <= high;
        }

        /// <summary>Whether point p lies within <paramref name="reach"/> of the segment a–b.</summary>
        private static bool Near(double px, double py, double ax, double ay, double bx, double by, double reach)
        {
            var (dx, dy) = (bx - ax, by - ay);
            var length2 = (dx * dx) + (dy * dy);
            var t = length2 > 0 ? Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / length2, 0, 1) : 0;
            return Square(px - (ax + (t * dx))) + Square(py - (ay + (t * dy))) < reach * reach;
        }
    }
}
