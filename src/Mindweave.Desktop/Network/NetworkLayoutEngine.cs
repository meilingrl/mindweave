using System.Collections.Immutable;
using Mindweave.Application.Network;
using Mindweave.Core.Network;

namespace Mindweave.Desktop.Network;

public sealed class NetworkLayoutEngine
{
    public ImmutableDictionary<Guid, NodePosition> Arrange(NetworkGraphSnapshot graph,
        ImmutableDictionary<Guid, NodeExtent> extents, NetworkLayoutOptions? options = null)
    {
        options ??= new();
        var forest = Prepare(graph, extents, options);
        var positions = new Dictionary<Guid, NodePosition>();
        double nextLeft = 0;
        foreach (var group in forest.Order.GroupBy(id => forest.Nodes[id].RootConversationId).OrderBy(g => g.Key))
        {
            var ids = group.ToArray();
            var roots = ids.Where(id => !forest.Parents.ContainsKey(id)).ToArray();
            // A maximum side alone still overlaps diagonally. This circumscribed separation
            // keeps different rings clear for every angle, including long measured titles.
            var ringWidth = ids.Max(id => Extent(id, extents).Width) + options.CollisionGap;
            var ringHeight = ids.Max(id => Extent(id, extents).Height) + options.CollisionGap;
            var step = Math.Max(options.RadialGap, Math.Sqrt(ringWidth * ringWidth + ringHeight * ringHeight));
            var weights = new Dictionary<Guid, int>();
            foreach (var id in ids.Reverse()) weights[id] = 1 + forest.Children[id].Sum(child => weights[child]);

            // An unsummarized conversation root is a grouping origin, never an invented node.
            var stack = new Stack<(Guid Id, int Depth, double Start, double Sweep)>();
            if (roots.Length == 1 && forest.Nodes[roots[0]].Metadata.ConversationId == group.Key)
                stack.Push((roots[0], 0, -Math.PI, Math.Tau));
            else
                PushSectors(roots, 1, -Math.PI, Math.Tau);
            while (stack.TryPop(out var item))
            {
                var angle = item.Start + item.Sweep / 2;
                var radius = item.Depth * step;
                positions[item.Id] = new(radius * Math.Cos(angle), radius * Math.Sin(angle));
                PushSectors(forest.Children[item.Id], item.Depth + 1, item.Start, item.Sweep);
            }

            ResolveCollisions(ids, positions, extents, options);
            var bounds = Bounds(ids, positions, extents);
            var offsetX = nextLeft - bounds.Left;
            var offsetY = -(bounds.Top + (bounds.Bottom - bounds.Top) / 2);
            foreach (var id in ids)
            {
                var point = positions[id];
                positions[id] = new(point.X + offsetX, point.Y + offsetY);
            }
            nextLeft += bounds.Right - bounds.Left + options.GroupGap;

            void PushSectors(IReadOnlyList<Guid> children, int depth, double start, double sweep)
            {
                var total = children.Sum(id => weights[id]);
                // Calculate in forward stable order, push in reverse for stable iterative traversal.
                var sectors = new List<(Guid, int, double, double)>(children.Count);
                foreach (var child in children)
                {
                    var portion = sweep * weights[child] / total;
                    sectors.Add((child, depth, start, portion));
                    start += portion;
                }
                for (var i = sectors.Count - 1; i >= 0; i--) stack.Push(sectors[i]);
            }
        }

        var centerX = positions.Count == 0 ? 0 : (nextLeft - options.GroupGap) / 2;
        return positions.ToImmutableDictionary(p => p.Key, p => RequirePosition(new(p.Value.X - centerX, p.Value.Y)));
    }

    public ImmutableDictionary<Guid, NodePosition> PlaceMissing(NetworkGraphSnapshot graph,
        ImmutableDictionary<Guid, NodePosition> saved, ImmutableDictionary<Guid, NodeExtent> extents,
        NetworkLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(saved);
        options ??= new();
        var forest = Prepare(graph, extents, options);
        foreach (var point in saved.Values) point.Validate(nameof(saved));
        var positions = saved.Where(p => forest.Nodes.ContainsKey(p.Key)).ToDictionary();
        if (positions.Count == forest.Order.Length) return positions.ToImmutableDictionary();
        var arranged = Arrange(graph, extents, options);
        if (positions.Count == 0) return arranged;

        foreach (var id in forest.Order)
        {
            if (positions.ContainsKey(id)) continue;
            var source = FindSavedSource(id);
            var origin = source is { } anchor ? saved[anchor] : arranged[id];
            var direction = source is { } reference
                ? Math.Atan2(arranged[id].Y - arranged[reference].Y, arranged[id].X - arranged[reference].X)
                : 0;
            var extent = Extent(id, extents);
            var sourceExtent = source is { } sourceId ? Extent(sourceId, extents) : extent;
            var step = Math.Max(options.RadialGap, Math.Max((extent.Width + sourceExtent.Width) / 2, (extent.Height + sourceExtent.Height) / 2) + options.CollisionGap);
            NodePosition? chosen = source is null && IsFree(origin, extent, positions, extents, options.CollisionGap) ? origin : null;
            // Bounded neighborhood search; no saved or previously placed coordinate is ever modified.
            for (var ring = 1; ring <= 24 && chosen is null; ring++)
                for (var sample = 0; sample < 32; sample++)
                {
                    var signedSample = (sample + 1) / 2 * (sample % 2 == 0 ? -1 : 1);
                    var angle = direction + signedSample * Math.Tau / 32;
                    var candidate = new NodePosition(origin.X + ring * step * Math.Cos(angle), origin.Y + ring * step * Math.Sin(angle));
                    if (!IsFree(candidate, extent, positions, extents, options.CollisionGap)) continue;
                    chosen = candidate;
                    break;
                }
            positions.Add(id, chosen ?? OutsideOccupied(origin, extent, positions, extents, options.CollisionGap));
        }
        return positions.ToImmutableDictionary();

        Guid? FindSavedSource(Guid id)
        {
            var ancestor = id;
            while (forest.Parents.TryGetValue(ancestor, out ancestor))
                if (saved.ContainsKey(ancestor)) return ancestor;

            // A root that was summarized later can also be placed near an existing saved descendant.
            var seen = new HashSet<Guid> { id };
            var queue = new Queue<Guid>(); queue.Enqueue(id);
            while (queue.TryDequeue(out var current))
            {
                if (saved.ContainsKey(current)) return current;
                if (forest.Parents.TryGetValue(current, out var parent) && seen.Add(parent)) queue.Enqueue(parent);
                foreach (var child in forest.Children[current])
                    if (seen.Add(child)) queue.Enqueue(child);
            }
            return null;
        }
    }

    private static void ResolveCollisions(Guid[] ids, Dictionary<Guid, NodePosition> positions,
        ImmutableDictionary<Guid, NodeExtent> extents, NetworkLayoutOptions options)
    {
        for (var pass = 0; pass < options.CollisionPasses; pass++)
        {
            var changed = false;
            for (var i = 0; i < ids.Length; i++)
                for (var j = i + 1; j < ids.Length; j++)
                {
                    var a = positions[ids[i]]; var b = positions[ids[j]];
                    var ae = Extent(ids[i], extents); var be = Extent(ids[j], extents);
                    var overlapX = (ae.Width + be.Width) / 2 + options.CollisionGap - Math.Abs(b.X - a.X);
                    var overlapY = (ae.Height + be.Height) / 2 + options.CollisionGap - Math.Abs(b.Y - a.Y);
                    if (overlapX <= 0 || overlapY <= 0) continue;
                    positions[ids[j]] = overlapX < overlapY
                        ? new(b.X + Math.CopySign(overlapX + 1e-6, b.X == a.X ? 1 : b.X - a.X), b.Y)
                        : new(b.X, b.Y + Math.CopySign(overlapY + 1e-6, b.Y == a.Y ? 1 : b.Y - a.Y));
                    changed = true;
                }
            if (!changed) break;
        }
        // A finite pass count alone cannot guarantee separation in a dense graph. Final stable
        // placement uses free space beyond the occupied bounds for any remaining collision.
        var placed = new Dictionary<Guid, NodePosition>();
        foreach (var id in ids)
        {
            var position = positions[id]; var extent = Extent(id, extents);
            if (!IsFree(position, extent, placed, extents, options.CollisionGap))
                position = OutsideOccupied(position, extent, placed, extents, options.CollisionGap);
            positions[id] = position; placed.Add(id, position);
        }
    }

    private static NodePosition OutsideOccupied(NodePosition origin, NodeExtent extent,
        Dictionary<Guid, NodePosition> occupied, ImmutableDictionary<Guid, NodeExtent> extents, double gap)
    {
        var bounds = Bounds(occupied.Keys, occupied, extents);
        var candidates = new NodePosition[]
        {
            new(bounds.Right + extent.Width / 2 + gap + 1e-6, origin.Y),
            new(bounds.Left - extent.Width / 2 - gap - 1e-6, origin.Y),
            new(origin.X, bounds.Bottom + extent.Height / 2 + gap + 1e-6),
            new(origin.X, bounds.Top - extent.Height / 2 - gap - 1e-6)
        };
        foreach (var candidate in candidates)
            if (IsFree(candidate, extent, occupied, extents, gap)) return candidate;
        throw new InvalidOperationException("合法画布坐标范围内没有找到可用的节点位置。");
    }

    private static bool IsFree(NodePosition candidate, NodeExtent extent, Dictionary<Guid, NodePosition> occupied,
        ImmutableDictionary<Guid, NodeExtent> extents, double gap)
    {
        if (!candidate.IsValid) return false;
        foreach (var (id, position) in occupied)
        {
            var other = Extent(id, extents);
            if (Math.Abs(candidate.X - position.X) < (extent.Width + other.Width) / 2 + gap &&
                Math.Abs(candidate.Y - position.Y) < (extent.Height + other.Height) / 2 + gap) return false;
        }
        return true;
    }

    private static NodeExtent Extent(Guid id, ImmutableDictionary<Guid, NodeExtent> extents) => extents.GetValueOrDefault(id, NodeExtent.Default);
    private static NodePosition RequirePosition(NodePosition point)
    {
        if (!point.IsValid) throw new InvalidOperationException("网络布局超出合法画布坐标范围。");
        return point;
    }

    private static (double Left, double Top, double Right, double Bottom) Bounds(IEnumerable<Guid> ids,
        Dictionary<Guid, NodePosition> positions, ImmutableDictionary<Guid, NodeExtent> extents)
    {
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var id in ids)
        {
            var position = positions[id]; var extent = Extent(id, extents);
            left = Math.Min(left, position.X - extent.Width / 2); top = Math.Min(top, position.Y - extent.Height / 2);
            right = Math.Max(right, position.X + extent.Width / 2); bottom = Math.Max(bottom, position.Y + extent.Height / 2);
        }
        return (left, top, right, bottom);
    }

    private sealed record Forest(Dictionary<Guid, NetworkNode> Nodes, Dictionary<Guid, Guid> Parents,
        Dictionary<Guid, Guid[]> Children, Guid[] Order);

    private static Forest Prepare(NetworkGraphSnapshot graph, ImmutableDictionary<Guid, NodeExtent> extents, NetworkLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(extents);
        options.Validate();
        foreach (var extent in extents.Values) extent.Validate(nameof(extents));
        if (graph.Nodes.IsDefault || graph.Edges.IsDefault) throw new ArgumentException("网络集合必须已初始化。", nameof(graph));
        var nodes = new Dictionary<Guid, NetworkNode>();
        foreach (var node in graph.Nodes)
            if (!nodes.TryAdd(node.Metadata.Id, node)) throw new ArgumentException("网络节点 ID 重复。", nameof(graph));
        var ordered = nodes.Keys.OrderBy(id => nodes[id].Metadata.CreatedAt).ThenBy(id => id).ToArray();
        var parents = new Dictionary<Guid, Guid>();
        var childLists = ordered.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var edge in graph.Edges)
        {
            if (!nodes.TryGetValue(edge.ParentNodeId, out var parent) || !nodes.TryGetValue(edge.ChildNodeId, out var child) ||
                parent.RootConversationId != child.RootConversationId || !parents.TryAdd(edge.ChildNodeId, edge.ParentNodeId))
                throw new ArgumentException("网络边必须连接同组节点且每个节点至多一个父节点。", nameof(graph));
            childLists[edge.ParentNodeId].Add(edge.ChildNodeId);
        }
        var children = childLists.ToDictionary(p => p.Key, p => p.Value.OrderBy(id => nodes[id].Metadata.CreatedAt).ThenBy(id => id).ToArray());
        var order = new List<Guid>(nodes.Count);
        var stack = new Stack<Guid>(ordered.Where(id => !parents.ContainsKey(id)).Reverse());
        while (stack.TryPop(out var id))
        {
            order.Add(id);
            for (var i = children[id].Length - 1; i >= 0; i--) stack.Push(children[id][i]);
        }
        if (order.Count != nodes.Count) throw new ArgumentException("网络关系包含循环。", nameof(graph));
        return new(nodes, parents, children, order.ToArray());
    }
}
