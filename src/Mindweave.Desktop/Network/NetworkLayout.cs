using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.Network;

/// <summary>
/// One node as the layout sees it: its summarized parent (if any), its exploration group, its creation order and
/// the width of its label. The label width is an estimate from the text, never a measured font size, so the same
/// input lays out the same on every machine (MW-C12).
/// </summary>
public sealed record LayoutNode(SummaryNodeId Id, SummaryNodeId? Parent, Guid Group, int Order, double LabelWidth);

/// <summary>
/// Stable automatic layout (architecture §11.2): a radial tree per exploration group, groups side by side, and
/// placement of new nodes beside their source without moving anything already placed. Pure and deterministic.
/// </summary>
public static class NetworkLayout
{
    public const double LevelSpacing = 170;
    public const double GroupGap = 160;
    public const double NodeHeight = 44;
    public const double LabelPadding = 24;
    public const int CollisionPasses = 40;

    /// <summary>Estimated label width: CJK and other wide characters about one em, ASCII about half.</summary>
    public static double LabelWidth(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return label.EnumerateRunes().Sum(r => r.IsAscii ? 8.0 : 14.0);
    }

    /// <summary>Lays out every node from scratch (first layout, or an explicit "arrange").</summary>
    public static IReadOnlyDictionary<SummaryNodeId, NodePosition> Arrange(IReadOnlyList<LayoutNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var result = new Dictionary<SummaryNodeId, NodePosition>();
        var cursor = 0.0;
        foreach (var group in Groups(nodes))
        {
            var local = ArrangeGroup(group);
            var minX = local.Values.Min(p => p.X) - (group.Max(n => n.LabelWidth) / 2) - LabelPadding;
            var maxX = local.Values.Max(p => p.X) + (group.Max(n => n.LabelWidth) / 2) + LabelPadding;
            var shift = cursor - minX;
            foreach (var (id, p) in local)
            {
                result[id] = new NodePosition(p.X + shift, p.Y);
            }

            cursor += (maxX - minX) + GroupGap;
        }

        Separate(nodes, result, fixedNodes: null);
        return result;
    }

    /// <summary>
    /// Places nodes that have no position yet, keeping every existing position. Each goes beside its parent (outwards,
    /// trying a fixed sequence of angles and rings), else at the edge of its group, else to the right of everything.
    /// </summary>
    public static IReadOnlyDictionary<SummaryNodeId, NodePosition> PlaceNew(IReadOnlyList<LayoutNode> nodes, IReadOnlyDictionary<SummaryNodeId, NodePosition> existing)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(existing);
        var known = nodes.ToDictionary(n => n.Id);
        var placed = existing.Where(p => known.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (placed.Count == 0)
        {
            return Arrange(nodes);
        }

        var added = new Dictionary<SummaryNodeId, NodePosition>();
        foreach (var node in nodes.OrderBy(n => n.Order).ThenBy(n => n.Id.Value).Where(n => !placed.ContainsKey(n.Id)))
        {
            var position = node.Parent is { } parent && placed.TryGetValue(parent, out var p)
                ? BesideParent(node, p, Outward(known, placed, parent, p), known, placed)
                : placed.Where(x => known[x.Key].Group == node.Group).Select(x => x.Value).ToList() is { Count: > 0 } mates
                    ? BesideGroup(node, mates, known, placed)
                    : RightOfAll(node, known, placed);
            placed[node.Id] = position;
            added[node.Id] = position;
        }

        return added;
    }

    private static IEnumerable<List<LayoutNode>> Groups(IReadOnlyList<LayoutNode> nodes) =>
        nodes.GroupBy(n => n.Group)
            .Select(g => g.OrderBy(n => n.Order).ThenBy(n => n.Id.Value).ToList())
            .OrderBy(g => g[0].Order).ThenBy(g => g[0].Id.Value);

    private static Dictionary<SummaryNodeId, NodePosition> ArrangeGroup(List<LayoutNode> group)
    {
        var ids = group.Select(n => n.Id).ToHashSet();
        var children = group.ToLookup(n => n.Parent is { } p && ids.Contains(p) ? p : (SummaryNodeId?)null);
        var tops = children[null].ToList();
        var leaves = new Dictionary<SummaryNodeId, int>();
        foreach (var top in tops)
        {
            CountLeaves(top.Id, children, leaves);
        }

        var result = new Dictionary<SummaryNodeId, NodePosition>();
        if (tops.Count == 1)
        {
            // The exploration's own summary sits in the middle.
            result[tops[0].Id] = new NodePosition(0, 0);
            Spread(tops[0].Id, 1, 0, 2 * Math.PI, children, leaves, result);
        }
        else
        {
            // The root conversation has no summary: its summarized descendants share the first ring around an empty centre.
            Assign(tops, 1, 0, 2 * Math.PI, children, leaves, result);
        }

        return result;
    }

    private static int CountLeaves(SummaryNodeId id, ILookup<SummaryNodeId?, LayoutNode> children, Dictionary<SummaryNodeId, int> leaves)
    {
        var kids = children[id].ToList();
        var count = kids.Count == 0 ? 1 : kids.Sum(k => CountLeaves(k.Id, children, leaves));
        leaves[id] = count;
        return count;
    }

    private static void Spread(SummaryNodeId parent, int depth, double from, double to, ILookup<SummaryNodeId?, LayoutNode> children, Dictionary<SummaryNodeId, int> leaves, Dictionary<SummaryNodeId, NodePosition> result) =>
        Assign([.. children[parent]], depth, from, to, children, leaves, result);

    private static void Assign(List<LayoutNode> siblings, int depth, double from, double to, ILookup<SummaryNodeId?, LayoutNode> children, Dictionary<SummaryNodeId, int> leaves, Dictionary<SummaryNodeId, NodePosition> result)
    {
        var total = siblings.Sum(s => leaves[s.Id]);
        var angle = from;
        foreach (var sibling in siblings)
        {
            var span = (to - from) * leaves[sibling.Id] / total;
            var middle = angle + (span / 2);
            var radius = depth * LevelSpacing;
            result[sibling.Id] = new NodePosition(Math.Round(radius * Math.Cos(middle), 2), Math.Round(radius * Math.Sin(middle), 2));
            Spread(sibling.Id, depth + 1, angle, angle + span, children, leaves, result);
            angle += span;
        }
    }

    /// <summary>The direction from the parent's own source (or group centre) to the parent, so new nodes continue outwards.</summary>
    private static double Outward(Dictionary<SummaryNodeId, LayoutNode> known, Dictionary<SummaryNodeId, NodePosition> placed, SummaryNodeId parent, NodePosition p)
    {
        if (known[parent].Parent is { } grand && placed.TryGetValue(grand, out var g) && (g.X != p.X || g.Y != p.Y))
        {
            return Math.Atan2(p.Y - g.Y, p.X - g.X);
        }

        var mates = placed.Where(x => known[x.Key].Group == known[parent].Group).Select(x => x.Value).ToList();
        var cx = mates.Average(m => m.X);
        var cy = mates.Average(m => m.Y);
        return Math.Abs(p.X - cx) < 1 && Math.Abs(p.Y - cy) < 1 ? 0 : Math.Atan2(p.Y - cy, p.X - cx);
    }

    private static NodePosition BesideParent(LayoutNode node, NodePosition parent, double outward, Dictionary<SummaryNodeId, LayoutNode> known, Dictionary<SummaryNodeId, NodePosition> placed)
    {
        for (var ring = 1; ring <= 6; ring++)
        {
            var radius = LevelSpacing * (0.8 + (0.4 * ring));
            for (var step = 0; step < 12; step++)
            {
                // 0, +30°, −30°, +60°, −60°, … around the outward direction.
                var offset = (step + 1) / 2 * (Math.PI / 6) * (step % 2 == 0 ? -1 : 1);
                var candidate = Round(parent.X + (radius * Math.Cos(outward + offset)), parent.Y + (radius * Math.Sin(outward + offset)));
                if (!Collides(node, candidate, known, placed))
                {
                    return candidate;
                }
            }
        }

        return Round(parent.X + (LevelSpacing * 3.5 * Math.Cos(outward)), parent.Y + (LevelSpacing * 3.5 * Math.Sin(outward)));
    }

    private static NodePosition BesideGroup(LayoutNode node, List<NodePosition> mates, Dictionary<SummaryNodeId, LayoutNode> known, Dictionary<SummaryNodeId, NodePosition> placed)
    {
        var cx = mates.Average(m => m.X);
        var cy = mates.Average(m => m.Y);
        var reach = mates.Max(m => Math.Sqrt(((m.X - cx) * (m.X - cx)) + ((m.Y - cy) * (m.Y - cy))));
        return BesideParent(node, new NodePosition(cx, cy), Math.PI / 2, known, placed) is var near && reach < LevelSpacing
            ? near
            : BesideParent(node, Round(cx, cy + reach), Math.PI / 2, known, placed);
    }

    private static NodePosition RightOfAll(LayoutNode node, Dictionary<SummaryNodeId, LayoutNode> known, Dictionary<SummaryNodeId, NodePosition> placed)
    {
        var right = placed.Max(p => p.Value.X + (known[p.Key].LabelWidth / 2));
        return Round(right + GroupGap + (node.LabelWidth / 2) + LabelPadding, placed.Values.Average(p => p.Y));
    }

    private static bool Collides(LayoutNode node, NodePosition at, Dictionary<SummaryNodeId, LayoutNode> known, Dictionary<SummaryNodeId, NodePosition> placed) =>
        placed.Any(p => Overlap(node.LabelWidth, at, known[p.Key].LabelWidth, p.Value, out _, out _));

    /// <summary>Limited collision repair on label boxes; nodes in <paramref name="fixedNodes"/> do not move.</summary>
    private static void Separate(IReadOnlyList<LayoutNode> nodes, Dictionary<SummaryNodeId, NodePosition> positions, IReadOnlySet<SummaryNodeId>? fixedNodes)
    {
        var ordered = nodes.OrderBy(n => n.Order).ThenBy(n => n.Id.Value).ToList();
        for (var pass = 0; pass < CollisionPasses; pass++)
        {
            var moved = false;
            for (var i = 0; i < ordered.Count; i++)
            {
                for (var j = i + 1; j < ordered.Count; j++)
                {
                    var a = ordered[i];
                    var b = ordered[j];
                    if (!Overlap(a.LabelWidth, positions[a.Id], b.LabelWidth, positions[b.Id], out var dx, out var dy))
                    {
                        continue;
                    }

                    // Push the later node away along the shorter overlap axis; deterministic, no randomness.
                    var pb = positions[b.Id];
                    var pa = positions[a.Id];
                    var target = fixedNodes?.Contains(b.Id) == true ? a.Id : b.Id;
                    var sign = target == b.Id ? 1 : -1;
                    var p = positions[target];
                    positions[target] = dx < dy
                        ? Round(p.X + (sign * (pb.X >= pa.X ? dx : -dx)), p.Y)
                        : Round(p.X, p.Y + (sign * (pb.Y >= pa.Y ? dy : -dy)));
                    moved = true;
                }
            }

            if (!moved)
            {
                return;
            }
        }
    }

    private static bool Overlap(double widthA, NodePosition a, double widthB, NodePosition b, out double dx, out double dy)
    {
        var halfW = ((widthA + widthB) / 2) + LabelPadding;
        dx = halfW - Math.Abs(a.X - b.X);
        dy = NodeHeight - Math.Abs(a.Y - b.Y);
        if (dx > 0 && dy > 0)
        {
            dx += 1;
            dy += 1;
            return true;
        }

        return false;
    }

    private static NodePosition Round(double x, double y) => new(Math.Round(x, 2), Math.Round(y, 2));
}
