using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// One conversation as the timeline sees it: its parent (none for a root), its local start day (used for roots only)
/// and its rank by creation time, then id.
/// </summary>
public sealed record TimelineNode(SummaryNodeId Id, SummaryNodeId? Parent, DateOnly Day, int Order);

/// <summary>Every dot's position, and the roots that start a new day (they carry the day label).</summary>
public sealed record TimelineArrangement(IReadOnlyDictionary<SummaryNodeId, NodePosition> Positions, IReadOnlyList<SummaryNodeId> DayStarts);

/// <summary>
/// The world-line timeline (docs/design/session-network.md §5.1). Roots sit on the main line (lane 0) in creation order.
/// Branches run in lanes parallel to it: lanes with negative numbers lie above the main line, lanes with positive
/// numbers below. Pure and deterministic, and computed from the data alone, so nothing is saved.
/// </summary>
/// <remarks>
/// <para>
/// A connector from a parent to a child in a new lane leaves and arrives horizontally, so it crosses the lanes
/// between them only at an x strictly between the two dots. The layout keeps two promises:
/// </para>
/// <list type="bullet">
/// <item>dots on one lane are at least <see cref="Step"/> apart;</item>
/// <item>no connector passes a dot: there is no dot strictly between a connector's ends in both x and y.</item>
/// </list>
/// <para>
/// The design's "lanes never cross" is read as "lanes never overlap". With lanes that never move, a connector sometimes
/// has to cross another lane's line, for example a later root's branch crossing an earlier root's world line that
/// runs past it. It crosses only between two dots of that lane, never through a dot.
/// </para>
/// </remarks>
public static class TimelineLayout
{
    /// <summary>Distance between consecutive roots on one day, and from a dot to its child.</summary>
    public const double Step = 110;

    /// <summary>Vertical distance between lanes.</summary>
    public const double LaneGap = 64;

    /// <summary>
    /// Extra room between two roots started on different days. A whole step, so every x is a multiple of
    /// <see cref="Step"/>: no dot can then sit strictly between two neighbouring x values, and opening a lane always
    /// succeeds (ruling R7).
    /// </summary>
    public const double DayGap = Step;

    /// <summary>
    /// Arranges every node. A node whose parent is not in <paramref name="nodes"/> is placed as a root. Children are
    /// placed depth-first in rank order with an explicit stack, because chains can be deep. Nodes on a parent cycle
    /// (which the data cannot hold) are unreachable from a root and left out.
    /// </summary>
    public static TimelineArrangement Arrange(IReadOnlyList<TimelineNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var ordered = nodes.OrderBy(n => n.Order).ThenBy(n => n.Id.Value).ToList();
        var known = ordered.Select(n => n.Id).ToHashSet();
        var roots = ordered.Where(n => n.Parent is not { } parent || !known.Contains(parent)).ToList();
        var children = ordered
            .Where(n => n.Parent is { } parent && known.Contains(parent))
            .GroupBy(n => n.Parent!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var positions = new Dictionary<SummaryNodeId, NodePosition>(ordered.Count);
        var dayStarts = new List<SummaryNodeId>();
        var rootXs = new List<double>(roots.Count);
        var gaps = 0;
        for (var i = 0; i < roots.Count; i++)
        {
            if (i == 0 || roots[i].Day != roots[i - 1].Day)
            {
                gaps += i == 0 ? 0 : 1;
                dayStarts.Add(roots[i].Id);
            }

            var x = (i * Step) + (gaps * DayGap);
            rootXs.Add(x);
            positions[roots[i].Id] = new NodePosition(x, 0);
        }

        var lanes = new Lanes(rootXs);
        var laneOf = new Dictionary<SummaryNodeId, int>(ordered.Count);
        var stack = new Stack<(TimelineNode Node, bool ContinuesLane)>();
        foreach (var root in roots)
        {
            laneOf[root.Id] = 0;
            PushChildren(root, isRoot: true);
            while (stack.TryPop(out var item))
            {
                var parentId = item.Node.Parent!.Value;
                var parent = positions[parentId];
                var parentLane = laneOf[parentId];
                // A continuation is always exactly one step: the parent was the last thing placed in its lane.
                var (lane, x) = item.ContinuesLane
                    ? (parentLane, Math.Max(parent.X + Step, lanes.End(parentLane) + Step))
                    : lanes.Open(parentLane, parent.X);
                lanes.Add(lane, x);
                laneOf[item.Node.Id] = lane;
                positions[item.Node.Id] = new NodePosition(x, lane * LaneGap);
                PushChildren(item.Node, isRoot: false);
            }
        }

        return new TimelineArrangement(positions, dayStarts);

        void PushChildren(TimelineNode node, bool isRoot)
        {
            if (!children.TryGetValue(node.Id, out var list))
            {
                return;
            }

            // Pushed last-first so they are placed in rank order, each with its whole subtree before the next.
            for (var i = list.Count - 1; i >= 0; i--)
            {
                stack.Push((list[i], !isRoot && i == 0));
            }
        }
    }

    /// <summary>The dots in each lane, in x order (a lane only ever grows to the right), and the roots on the main line.</summary>
    private sealed class Lanes(List<double> rootXs)
    {
        private readonly Dictionary<int, List<double>> _dots = [];

        /// <summary>
        /// The rightmost x taken in a lane: its last dot, or the x where a connector crossed it, whichever is larger.
        /// A later dot can only go a step past this.
        /// </summary>
        private readonly Dictionary<int, double> _end = [];

        public double End(int lane) => _end.TryGetValue(lane, out var end) ? end : double.NegativeInfinity;

        public void Add(int lane, double x)
        {
            if (!_dots.TryGetValue(lane, out var dots))
            {
                _dots[lane] = dots = [];
            }

            dots.Add(x);
            _end[lane] = Math.Max(End(lane), x);
        }

        /// <summary>
        /// A new lane for a child of the dot at (<paramref name="parentX"/>, <paramref name="parentLane"/>), at
        /// x0 = parentX + Step. Lanes are tried nearest the main line first. A root's child may go either side, above
        /// before below; a branch's child stays on its parent's side, so no sub-branch crosses the main line (ruling R9).
        /// <list type="bullet">
        /// <item>The target lane must have no dot past x0 − Step.</item>
        /// <item>Every lane strictly between the parent's lane and the target, including the main line, must have no
        /// dot in the open interval (x0 − Step, x0). A dot exactly at x0 does not block, since the connector is
        /// already in its own lane there; this lets siblings fan out from one parent.</item>
        /// </list>
        /// The search stops one lane beyond the outermost lane in use on each side; lanes further out would fail for
        /// the same reason. Because every x is on the <see cref="Step"/> grid, no lane ever has a dot in the open
        /// interval, so the empty lane one past the outermost on either side always fits; finding none means the grid was
        /// broken.
        /// </summary>
        public (int Lane, double X) Open(int parentLane, double parentX)
        {
            var x0 = parentX + Step;
            var top = Math.Min(0, _end.Keys.DefaultIfEmpty(0).Min()) - 1;
            var bottom = Math.Max(0, _end.Keys.DefaultIfEmpty(0).Max()) + 1;
            var blocked = new int[bottom - top + 2]; // prefix counts of lanes with a dot in (x0 − Step, x0); the parent's lane lies in range
            for (var lane = top; lane <= bottom; lane++)
            {
                blocked[lane - top + 1] = blocked[lane - top] + (HasDotBetween(lane, parentX, x0) ? 1 : 0);
            }

            var side = Math.Sign(parentLane);
            for (var k = 1; -k >= top || k <= bottom; k++)
            {
                foreach (var target in side == 0 ? (ReadOnlySpan<int>)[-k, k] : [side * k])
                {
                    if (target < top || target > bottom || target == parentLane || End(target) > x0 - Step)
                    {
                        continue;
                    }

                    var (from, to) = target < parentLane ? (target + 1, parentLane - 1) : (parentLane + 1, target - 1);
                    if (from <= to && blocked[to - top + 1] - blocked[from - top] > 0)
                    {
                        continue;
                    }

                    // Mark the crossed lanes, so no later dot lands where this connector passes.
                    for (var lane = from; lane <= to; lane++)
                    {
                        if (lane != 0)
                        {
                            _end[lane] = Math.Max(End(lane), x0 - Step);
                        }
                    }

                    return (target, x0);
                }
            }

            throw new InvalidOperationException("Unreachable: every x is on the Step grid, so a lane always fits.");
        }

        /// <summary>Whether the lane has a dot strictly between the two x values.</summary>
        private bool HasDotBetween(int lane, double after, double before)
        {
            var dots = lane == 0 ? rootXs : _dots.GetValueOrDefault(lane);
            if (dots is null)
            {
                return false;
            }

            // First dot past `after`; dots are in ascending x.
            int lo = 0, hi = dots.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (dots[mid] <= after)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo < dots.Count && dots[lo] < before;
        }
    }
}
