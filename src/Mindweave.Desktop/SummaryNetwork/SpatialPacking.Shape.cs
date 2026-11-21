namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// What keeps the structure while footprints settle (see <c>Compact</c>): each sub-branch keeps roughly its packed
/// shape, turned toward the node it hangs from, and a node's children keep their creation order and their share of
/// the turn around it. Without these the pull mixes sub-branches and their branch lines cross.
/// </summary>
public static partial class SpatialPacking
{
    /// <summary>How far, per step, a body that has turned away from the node it hangs from is turned back.</summary>
    private const double FacingRate = 0.5;

    /// <summary>
    /// Every body's packed shape (its rest positions) and, each time it is asked, the rigid motion that best maps
    /// that shape onto where the body is now: its centroid and its turn, weighted by footprint area.
    /// </summary>
    private sealed class Frames
    {
        private readonly double[] _restX;
        private readonly double[] _restY;
        private readonly double[] _area;
        private readonly int[] _body;
        private readonly int[] _head;
        private readonly double[] _restCx;
        private readonly double[] _restCy;
        private readonly double[] _cx;
        private readonly double[] _cy;
        private readonly double[] _turn;

        public Frames(double[] x, double[] y, double[] area, int[] body, int bodies)
        {
            (_restX, _restY, _area, _body) = ((double[])x.Clone(), (double[])y.Clone(), area, body);
            (_restCx, _restCy, _cx, _cy, _turn) = (new double[bodies], new double[bodies], new double[bodies], new double[bodies], new double[bodies]);
            _head = Enumerable.Repeat(-1, bodies).ToArray();
            var weight = new double[bodies];
            for (var i = 0; i < x.Length; i++)
            {
                var b = body[i];
                if (_head[b] < 0)
                {
                    _head[b] = i;
                }

                weight[b] += area[i];
                _restCx[b] += area[i] * x[i];
                _restCy[b] += area[i] * y[i];
            }

            for (var b = 0; b < bodies; b++)
            {
                (_restCx[b], _restCy[b]) = (_restCx[b] / weight[b], _restCy[b] / weight[b]);
            }
        }

        /// <summary>
        /// Shape matching: moves every footprint a fraction <paramref name="stiffness"/> of the way to where its
        /// body's packed shape, best fitted to the body now, puts it.
        /// </summary>
        public void Match(double[] x, double[] y, double stiffness, bool skipFirst)
        {
            Fit(x, y);
            for (var i = 0; i < x.Length; i++)
            {
                var b = _body[i];
                if (b == 0 && skipFirst)
                {
                    continue;
                }

                var (cos, sin) = (Math.Cos(_turn[b]), Math.Sin(_turn[b]));
                var (px, py) = (_restX[i] - _restCx[b], _restY[i] - _restCy[b]);
                var (gx, gy) = (_cx[b] + (cos * px) - (sin * py), _cy[b] + (sin * px) + (cos * py));
                x[i] += stiffness * (gx - x[i]);
                y[i] += stiffness * (gy - y[i]);
            }
        }

        /// <summary>
        /// Keeps every body turned as it was packed relative to the node it hangs from: if the body has swung round
        /// that node, or turned about itself, it is turned about its own head (by <see cref="FacingRate"/> of the
        /// difference per step) so that the side that faced the node faces it again. That side is where the branch
        /// line comes in, so the line does not cut through the body's own branches.
        /// </summary>
        public void KeepFacing(double[] x, double[] y, int[] hangsFrom)
        {
            Fit(x, y);
            for (var b = 0; b < _turn.Length; b++)
            {
                var (from, head) = (hangsFrom[b], _head[b]);
                if (from < 0 || head < 0 || _body[from] == b)
                {
                    _turn[b] = 0;
                    continue;
                }

                var swing = Math.Atan2(y[head] - y[from], x[head] - x[from]) - Math.Atan2(_restY[head] - _restY[from], _restX[head] - _restX[from]);
                var t = swing - _turn[b];
                _turn[b] = FacingRate * Math.Atan2(Math.Sin(t), Math.Cos(t));
            }

            for (var i = 0; i < x.Length; i++)
            {
                var b = _body[i];
                if (_turn[b] == 0)
                {
                    continue;
                }

                var head = _head[b];
                var (cos, sin) = (Math.Cos(_turn[b]), Math.Sin(_turn[b]));
                var (dx, dy) = (x[i] - x[head], y[i] - y[head]);
                x[i] = x[head] + (dx * cos) - (dy * sin);
                y[i] = y[head] + (dx * sin) + (dy * cos);
            }
        }

        /// <summary>Each body's area-weighted centroid now, and its turn from the packed shape (least squares).</summary>
        private void Fit(double[] x, double[] y)
        {
            var bodies = _turn.Length;
            var weight = new double[bodies];
            Array.Clear(_cx);
            Array.Clear(_cy);
            for (var i = 0; i < x.Length; i++)
            {
                var b = _body[i];
                weight[b] += _area[i];
                _cx[b] += _area[i] * x[i];
                _cy[b] += _area[i] * y[i];
            }

            for (var b = 0; b < bodies; b++)
            {
                (_cx[b], _cy[b]) = (_cx[b] / weight[b], _cy[b] / weight[b]);
            }

            var (dot, cross) = (new double[bodies], new double[bodies]);
            for (var i = 0; i < x.Length; i++)
            {
                var b = _body[i];
                var (px, py) = (_restX[i] - _restCx[b], _restY[i] - _restCy[b]);
                var (qx, qy) = (x[i] - _cx[b], y[i] - _cy[b]);
                dot[b] += _area[i] * ((px * qx) + (py * qy));
                cross[b] += _area[i] * ((px * qy) - (py * qx));
            }

            for (var b = 0; b < bodies; b++)
            {
                _turn[b] = Math.Atan2(cross[b], dot[b]);
            }
        }
    }

    /// <summary>
    /// Keeps the children of every node with three or more children in creation order clockwise around it: a child
    /// that is not between its neighbours (round the circle) is turned about its parent, with its whole subtree, to
    /// the middle of the arc between them. Returns whether anything moved.
    /// </summary>
    private static bool KeepFanOrder(double[] x, double[] y, List<(int Parent, int[][] Children)> fans)
    {
        var moved = false;
        foreach (var (parent, children) in fans)
        {
            var (px, py) = (x[parent], y[parent]);
            var k = children.Length;
            for (var i = 0; i < k; i++)
            {
                var before = Angle(children[(i + k - 1) % k][0]);
                var here = Angle(children[i][0]);
                var after = Angle(children[(i + 1) % k][0]);
                var arc = Turned(after - before);
                var at = Turned(here - before);
                if (at > 0 && at < arc)
                {
                    continue;
                }

                var turn = before + (arc / 2) - here;
                var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
                foreach (var m in children[i])
                {
                    var (dx, dy) = (x[m] - px, y[m] - py);
                    x[m] = px + (dx * cos) - (dy * sin);
                    y[m] = py + (dx * sin) + (dy * cos);
                }

                moved = true;
            }

            double Angle(int m) => Math.Atan2(y[m] - py, x[m] - px);
        }

        return moved;
    }

    /// <summary>
    /// Keeps each body (a fan child's subtree) in its share of the turn around the anchor (the fan's head): a
    /// footprint that has drifted out of it is turned back about the anchor to the nearer edge. Siblings then never
    /// trade places or mix, and the seam toward the parent stays open.
    /// </summary>
    private static void KeepInWedges(double[] x, double[] y, int[] body, int anchor, double[] wedges)
    {
        var (ax, ay) = (x[anchor], y[anchor]);
        var start = wedges[0];
        for (var i = 0; i < x.Length; i++)
        {
            var b = body[i];
            if (b == 0)
            {
                continue;
            }

            var (dx, dy) = (x[i] - ax, y[i] - ay);
            var angle = Math.Atan2(dy, dx);
            while (angle < start)
            {
                angle += 2 * Math.PI;
            }

            var (low, high) = (wedges[b - 1], wedges[b]);
            if (angle >= low && angle <= high)
            {
                continue;
            }

            // Outside: the nearer edge, measured round the circle.
            var toLow = angle < low ? low - angle : low + (2 * Math.PI) - angle;
            var toHigh = angle > high ? angle - high : angle + (2 * Math.PI) - high;
            var target = toLow <= toHigh ? low : high;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            x[i] = ax + (distance * Math.Cos(target));
            y[i] = ay + (distance * Math.Sin(target));
        }
    }
}
