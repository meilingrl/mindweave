using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.SummaryNetwork;

public static partial class SpatialLayout
{
    /// <summary>
    /// One layout run. Dots are numbered in creation order; each exploration's dots are a contiguous range of
    /// <see cref="preorder"/> (a dot, then its branches in creation order), so a branch moves as a whole by a loop over its range.
    /// </summary>
    private sealed partial class Model
    {
        private readonly SpatialLayoutSettings s;
        private readonly SummaryNodeId[] ids;
        private readonly int n;
        private readonly int[] parent;
        private readonly int[][] children;
        private readonly List<int> roots = [];
        private readonly int[] rootOf;
        private readonly int[] depth;
        private readonly int[] size;
        private readonly int[] weight;
        private readonly double[] reach;
        private readonly IReadOnlyList<string>[] lines;
        private readonly bool[] dated;
        private readonly double[] labelW;
        private readonly double[] labelH;
        private readonly int[] side;
        private readonly double[] x;
        private readonly double[] y;
        private readonly double[] dx;
        private readonly double[] dy;
        private readonly int[] preorder;
        private readonly int[] start;
        private readonly int[] end;
        private readonly double[][] offsets;
        private readonly int[] hand;
        private readonly double[] fit;
        private readonly double[] rest;
        private readonly double[] crowd;
        private readonly int[] place;
        private readonly double[] incoming;
        private readonly int[] lastTurn;
        private readonly double maxReach;

        /// <summary>Each root's exploration key (its conversation, else its dot's id), and the saved places of those that have one.</summary>
        private readonly Guid[] key;
        private readonly Dictionary<int, ExplorationPlace> saved = [];

        public Model(IReadOnlyList<SpatialNode> input, SpatialLayoutSettings settings, IReadOnlyDictionary<Guid, ExplorationPlace>? places = null)
        {
            s = settings;

            // Creation order, ids breaking ties, so the input's order does not matter; nodes in a parent cycle are left out.
            var sorted = input.OrderBy(node => node.Order).ThenBy(node => node.Id.Value).ToList();
            var known = sorted.Select(node => node.Id).ToHashSet();
            var parentOf = sorted.ToDictionary(node => node.Id, node => node.Parent is { } p && known.Contains(p) && p != node.Id ? p : (SummaryNodeId?)null);
            var kept = sorted.Where(node => ReachesRoot(node.Id, parentOf)).ToList();
            n = kept.Count;
            ids = [.. kept.Select(node => node.Id)];
            var index = new Dictionary<SummaryNodeId, int>(n);
            for (var i = 0; i < n; i++)
            {
                index[ids[i]] = i;
            }

            parent = new int[n];
            var kids = new List<int>[n];
            for (var i = 0; i < n; i++)
            {
                kids[i] = [];
            }

            for (var i = 0; i < n; i++)
            {
                parent[i] = parentOf[ids[i]] is { } p ? index[p] : -1;
                if (parent[i] >= 0)
                {
                    kids[parent[i]].Add(i);
                }
                else
                {
                    roots.Add(i);
                }
            }

            children = [.. kids.Select(k => k.ToArray())];
            rootOf = new int[n];
            depth = new int[n];
            size = new int[n];
            weight = new int[n];
            reach = new double[n];
            lines = new IReadOnlyList<string>[n];
            dated = new bool[n];
            labelW = new double[n];
            labelH = new double[n];
            side = new int[n];
            x = new double[n];
            y = new double[n];
            dx = new double[n];
            dy = new double[n];
            preorder = new int[n];
            start = new int[n];
            end = new int[n];
            hand = new int[n];
            for (var i = 0; i < n; i++)
            {
                var node = kept[i];
                weight[i] = Math.Max(1, node.Weight);
                reach[i] = node.DotRadius;
                lines[i] = Wrap(node.Label);
                dated[i] = node.Dated;
                (labelW[i], labelH[i]) = BlockSize(lines[i], dated[i]);
                hand[i] = 1;
            }

            key = [.. kept.Select(node => node.Conversation ?? node.Id.Value)];
            if (places is not null)
            {
                foreach (var r in roots)
                {
                    if (places.TryGetValue(key[r], out var place))
                    {
                        saved[r] = place;
                    }
                }
            }

            maxReach = n == 0 ? 0 : reach.Max();
            NumberTrees();
            offsets = new double[n][];
            for (var i = 0; i < n; i++)
            {
                offsets[i] = Offsets(i);
            }

            fit = new double[n];
            rest = new double[n];
            incoming = new double[n];
            lastTurn = new int[n];

            // A child's place among its siblings, and how far round a busy dot all its lines and titles need.
            place = new int[n];
            crowd = new double[n];
            for (var i = 0; i < n; i++)
            {
                for (var c = 0; c < children[i].Length; c++)
                {
                    place[children[i][c]] = c;
                }

                if (children[i].Length + (parent[i] >= 0 ? 1 : 0) >= s.CrowdFrom)
                {
                    crowd[i] = children[i].Sum(k => (2 * reach[k]) + (0.5 * (labelW[k] + labelH[k])) + s.CrowdSpacing) / (2 * Math.PI);
                }
            }

            FitBranches();
        }

        /// <summary>
        /// The bond length each branch needs so that, at its rest angle, the whole branch lies inside its share of the turn
        /// round its parent, as in a radial tree. Each dot keeps the convex outline of its branch (dots and room for titles)
        /// in its own frame, x along the line that leads to it, made symmetric so the alternating turns do not matter.
        /// A point of a child's outline at distance d along its line and w across it is inside the wedge when
        /// |w| ≤ (L + d)·tan(half the wedge), so the line needs L ≥ |w| / tan(half the wedge) − d; in a half-plane, L ≥ −d. Leaves first.
        /// </summary>
        private void FitBranches()
        {
            var outline = new List<(double X, double Y)>[n];
            var points = new List<(double X, double Y)>();
            for (var k = n - 1; k >= 0; k--)
            {
                var i = preorder[k];
                var kids = children[i];
                var hasParent = parent[i] >= 0;
                var off = offsets[i];
                points.Clear();
                var own = reach[i] + LabelGap + (0.5 * labelW[i]);
                for (var a = 0; a < 8; a++)
                {
                    points.Add((own * Math.Cos(a * Math.PI / 4), own * Math.Sin(a * Math.PI / 4)));
                }

                for (var c = 0; c < kids.Length; c++)
                {
                    var child = kids[c];
                    var before = c > 0 ? off[c] - off[c - 1] : hasParent ? off[0] : (2 * Math.PI) - off[^1];
                    var after = c + 1 < kids.Length ? off[c + 1] - off[c] : hasParent ? (2 * Math.PI) - off[c] : (2 * Math.PI) - off[c] + off[0];
                    var halfWedge = (before + after) / 4;
                    fit[child] = 0;
                    if (kids.Length + (hasParent ? 1 : 0) > 1)
                    {
                        // A wedge of half a turn or more is a half-plane here: the branch must not reach back behind its parent.
                        var tan = halfWedge < 0.49 * Math.PI ? Math.Tan(halfWedge) : double.PositiveInfinity;
                        foreach (var (d, w) in outline[child])
                        {
                            fit[child] = Math.Max(fit[child], ((Math.Abs(w) + (s.GapSibling / 2)) / tan) - d + (double.IsInfinity(tan) ? s.GapSibling / 2 : 0));
                        }

                        fit[child] *= s.FitShare;
                    }

                    // The child's outline, turned to its line's direction and moved out along it.
                    rest[child] = BondLength(i, child);
                    var angle = hasParent ? Math.PI + off[c] : off[c];
                    var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
                    var length = rest[child];
                    foreach (var (px, py) in outline[child])
                    {
                        points.Add((length * cos + (px * cos) - (py * sin), length * sin + (px * sin) + (py * cos)));
                    }
                }

                var mirrored = points.Select(q => (q.X, -q.Y)).ToList();
                points.AddRange(mirrored);
                outline[i] = Hull(points);
            }
        }

        /// <summary>The convex hull of points (Andrew's monotone chain), counter-clockwise.</summary>
        private static List<(double X, double Y)> Hull(List<(double X, double Y)> points)
        {
            var sorted = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
            if (sorted.Count < 3)
            {
                return sorted;
            }

            static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
            var hull = new List<(double X, double Y)>();
            foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
            {
                var startCount = hull.Count;
                foreach (var q in pass)
                {
                    while (hull.Count >= startCount + 2 && Cross(hull[^2], hull[^1], q) <= 0)
                    {
                        hull.RemoveAt(hull.Count - 1);
                    }

                    hull.Add(q);
                }

                hull.RemoveAt(hull.Count - 1);
            }

            return hull;
        }

        private static bool ReachesRoot(SummaryNodeId id, Dictionary<SummaryNodeId, SummaryNodeId?> parentOf)
        {
            var seen = new HashSet<SummaryNodeId>();
            var current = id;
            while (parentOf[current] is { } p)
            {
                if (!seen.Add(current))
                {
                    return false;
                }

                current = p;
            }

            return true;
        }

        /// <summary>Preorder ranges, roots, depths and branch sizes, without recursion (a chain may be very deep).</summary>
        private void NumberTrees()
        {
            var k = 0;
            var stack = new Stack<(int Node, bool Leaving)>();
            foreach (var r in roots)
            {
                stack.Push((r, false));
                while (stack.Count > 0)
                {
                    var (i, leaving) = stack.Pop();
                    if (leaving)
                    {
                        end[i] = k;
                        size[i] = end[i] - start[i];
                        continue;
                    }

                    start[i] = k;
                    preorder[k++] = i;
                    rootOf[i] = r;
                    depth[i] = parent[i] >= 0 ? depth[parent[i]] + 1 : 0;
                    stack.Push((i, true));
                    for (var c = children[i].Length - 1; c >= 0; c--)
                    {
                        stack.Push((children[i][c], false));
                    }
                }
            }
        }

        public void Run()
        {
            foreach (var r in roots)
            {
                Construct(r);
                Relax(r);
            }

            if (saved.Count > 0)
            {
                PlaceStable();
            }
            else if (roots.Count > 1)
            {
                Pack();
            }

            ChooseLabels(0, n, threads: true);
            ChooseLabels(0, n, threads: true);
        }

        public SpatialArrangement Result()
        {
            var positions = new Dictionary<SummaryNodeId, NodePosition>(n);
            var labels = new Dictionary<SummaryNodeId, SpatialLabel>(n);
            for (var i = 0; i < n; i++)
            {
                positions[ids[i]] = new NodePosition(x[i], y[i]);
                labels[ids[i]] = new SpatialLabel(side[i], lines[i]);
            }

            return new SpatialArrangement(positions, labels)
            {
                Places = roots.ToDictionary(r => key[r], r => new ExplorationPlace(x[r], y[r], Heading(r), size[r])),
            };
        }

        private bool IsAncestor(int a, int b) => start[a] <= start[b] && start[b] < end[a];

        /// <summary>How much room a line takes at a dot: the square root of how many dots lie behind it, at most <see cref="SpatialLayoutSettings.ShareCap"/>.</summary>
        private double Share(int behind) => Math.Sqrt(Math.Min(behind, s.ShareCap));

        /// <summary>The angle of each child's line, measured from the parent's line (from the first child's at a root).</summary>
        private double[] Offsets(int i)
        {
            var kids = children[i];
            if (kids.Length == 0)
            {
                return [];
            }

            var shares = new List<double>();
            var hasParent = parent[i] >= 0;
            if (hasParent)
            {
                shares.Add(Share(size[rootOf[i]] - size[i]));
            }

            shares.AddRange(kids.Select(c => Share(size[c])));
            var total = shares.Sum();
            var result = new double[kids.Length];
            double angle = 0;
            var first = hasParent ? 1 : 0;
            for (var k = 0; k < kids.Length; k++)
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

        /// <summary>
        /// A bond's rest length, worked out once (<see cref="rest"/>): the two dots, the base length, more for a bigger
        /// branch, at least what the branch needs to fit its wedge; round a busy dot, long enough for all its lines and
        /// titles to fit round it, its single-dot leaves alternating between two lengths.
        /// </summary>
        private double BondLength(int p, int c)
        {
            var length = Math.Max(reach[p] + reach[c] + s.Bond + (s.BondGrow * Math.Log2(size[c])), fit[c]);
            if (crowd[p] > 0)
            {
                length = Math.Max(length, crowd[p]);
                if (children[c].Length == 0 && place[c] % 2 == 1)
                {
                    length *= s.CrowdStagger;
                }
            }

            return length;
        }

        /// <summary>
        /// The ideal molecule, parents before branches: children follow in creation order round their parent, and each dot
        /// turns its heaviest branch the other way from the turn that led to it.
        /// </summary>
        private void Construct(int root)
        {
            x[root] = 0;
            y[root] = 0;
            for (var k = start[root]; k < end[root]; k++)
            {
                var i = preorder[k];
                var kids = children[i];
                if (kids.Length == 0)
                {
                    continue;
                }

                var off = offsets[i];
                var angles = new double[kids.Length];
                if (parent[i] >= 0)
                {
                    var back = incoming[i] + Math.PI;
                    var heavy = 0;
                    for (var c = 1; c < kids.Length; c++)
                    {
                        heavy = size[kids[c]] > size[kids[heavy]] ? c : heavy;
                    }

                    var turn = WrapAngle(back + off[heavy] - incoming[i]);
                    hand[i] = Math.Abs(turn) < 0.05 || lastTurn[i] == 0 ? 1 : Math.Sign(turn) == lastTurn[i] ? -1 : 1;
                    for (var c = 0; c < kids.Length; c++)
                    {
                        angles[c] = back + (hand[i] * off[c]);
                    }
                }
                else
                {
                    for (var c = 0; c < kids.Length; c++)
                    {
                        angles[c] = (-Math.PI / 2) + off[c];
                    }
                }

                for (var c = 0; c < kids.Length; c++)
                {
                    var child = kids[c];
                    var length = rest[child];
                    x[child] = x[i] + (length * Math.Cos(angles[c]));
                    y[child] = y[i] + (length * Math.Sin(angles[c]));
                    incoming[child] = angles[c];
                    var bend = parent[i] >= 0 ? WrapAngle(angles[c] - incoming[i]) : 0;
                    lastTurn[child] = Math.Abs(bend) < 0.05 ? 0 : Math.Sign(bend);
                }
            }
        }

        /// <summary>One exploration on its own: a fixed number of steps of overlaps, bond angles and bond lengths.</summary>
        private void Relax(int root)
        {
            var (from, to) = (start[root], end[root]);
            if (to - from < 2)
            {
                ChooseLabels(from, to, threads: false);
                return;
            }

            // Titles are placed every tenth step from LabelsFrom on, the last time a settling run before the end, so the
            // steps after it push anything a title still covers out of the way.
            var labelsFrom = (int)(s.Iterations * s.LabelsFrom);
            var steps = s.Iterations + s.SettleIterations;
            for (var t = 0; t < steps; t++)
            {
                var labels = t >= labelsFrom;
                if (labels && (t - labelsFrom) % 10 == 0 && t < steps - s.SettleIterations)
                {
                    ChooseLabels(from, to, threads: false);
                }

                Repel(from, to, labels);
                Angles(from, to);
                Bonds(from, to);
            }
        }

        /// <summary>The extra room kept between two dots of one exploration beyond their reach; -1 for a bonded pair.</summary>
        private double Gap(int u, int v)
        {
            if (parent[u] == v || parent[v] == u)
            {
                return -1;
            }

            if (IsAncestor(u, v) || IsAncestor(v, u))
            {
                return s.GapNested;
            }

            // The two branches that split where the lines of descent meet.
            var a = u;
            var b = v;
            while (depth[a] > depth[b])
            {
                a = parent[a];
            }

            while (depth[b] > depth[a])
            {
                b = parent[b];
            }

            while (parent[a] != parent[b])
            {
                a = parent[a];
                b = parent[b];
            }

            var smaller = Math.Min(size[a], size[b]);
            return s.GapSibling + (s.GapSiblingGrow * Math.Log2(1 + smaller) * (smaller > 1 ? 1 : 0.4));
        }

        /// <summary>Turns each branch, as a whole, toward its rest angle at its parent.</summary>
        private void Angles(int from, int to)
        {
            for (var k = from; k < to; k++)
            {
                var i = preorder[k];
                var kids = children[i];
                var hasParent = parent[i] >= 0;
                if (kids.Length + (hasParent ? 1 : 0) < 2)
                {
                    continue;
                }

                var off = offsets[i];
                double baseAngle;
                var sign = 1;
                if (hasParent)
                {
                    baseAngle = Math.Atan2(y[parent[i]] - y[i], x[parent[i]] - x[i]);
                    sign = hand[i];
                }
                else
                {
                    // A root has no parent line: its fan may turn freely, so only the spacing is kept.
                    double sx = 0, sy = 0;
                    for (var c = 0; c < kids.Length; c++)
                    {
                        var a = Math.Atan2(y[kids[c]] - y[i], x[kids[c]] - x[i]) - off[c];
                        sx += Math.Cos(a);
                        sy += Math.Sin(a);
                    }

                    baseAngle = Math.Atan2(sy, sx);
                }

                for (var c = 0; c < kids.Length; c++)
                {
                    var child = kids[c];
                    var target = baseAngle + (sign * off[c]);
                    var current = Math.Atan2(y[child] - y[i], x[child] - x[i]);
                    Rotate(child, x[i], y[i], WrapAngle(target - current) * s.AngleStiffness);
                }
            }
        }

        private void Bonds(int from, int to)
        {
            for (var k = from; k < to; k++)
            {
                var c = preorder[k];
                var p = parent[c];
                if (p < 0)
                {
                    continue;
                }

                var ex = x[c] - x[p];
                var ey = y[c] - y[p];
                var dist = Math.Max(1e-6, Math.Sqrt((ex * ex) + (ey * ey)));
                var move = (rest[c] - dist) * s.BondStiffness / dist;
                Translate(c, ex * move, ey * move);
            }
        }

        /// <summary>Turns a dot and everything branched from it round a point.</summary>
        private void Rotate(int top, double cx, double cy, double angle)
        {
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            for (var k = start[top]; k < end[top]; k++)
            {
                var i = preorder[k];
                var px = x[i] - cx;
                var py = y[i] - cy;
                x[i] = cx + (px * cos) - (py * sin);
                y[i] = cy + (px * sin) + (py * cos);
            }
        }

        private void Translate(int top, double mx, double my)
        {
            for (var k = start[top]; k < end[top]; k++)
            {
                x[preorder[k]] += mx;
                y[preorder[k]] += my;
            }
        }

        private (double X, double Y) Centroid(int root)
        {
            double sx = 0, sy = 0, sw = 0;
            for (var k = start[root]; k < end[root]; k++)
            {
                var i = preorder[k];
                sx += x[i] * weight[i];
                sy += y[i] * weight[i];
                sw += weight[i];
            }

            return (sx / sw, sy / sw);
        }

        private static double WrapAngle(double a)
        {
            a %= 2 * Math.PI;
            return a > Math.PI ? a - (2 * Math.PI) : a < -Math.PI ? a + (2 * Math.PI) : a;
        }
    }
}
