using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mindweave.Application.Network;

public static class NetworkProjection
{
    public static NetworkGraphSnapshot Build(Guid workspaceId,
        ImmutableArray<NetworkConversation> conversations, ImmutableArray<NetworkNodeMetadata> nodes)
    {
        if (workspaceId == Guid.Empty || conversations.IsDefault || nodes.IsDefault)
            throw new ArgumentException("网络快照身份或集合无效。");
        var byConversation = new Dictionary<Guid, NetworkConversation>();
        foreach (var conversation in conversations)
            if (conversation is null || conversation.Id == Guid.Empty || conversation.Title is null || !byConversation.TryAdd(conversation.Id, conversation))
                throw new InvalidDataException("网络会话身份重复或无效。");
        var roots = new Dictionary<Guid, Guid>();
        foreach (var conversation in conversations)
        {
            var chain = new List<Guid>(); var seen = new HashSet<Guid>(); var cursor = conversation.Id;
            while (!roots.ContainsKey(cursor))
            {
                if (!byConversation.TryGetValue(cursor, out var current))
                    throw new InvalidDataException("网络父会话缺失或不属于当前工作区。");
                if (!seen.Add(cursor)) throw new InvalidDataException("网络会话关系存在循环。");
                chain.Add(cursor);
                if (current.ParentId is not { } parent) { roots[cursor] = cursor; break; }
                cursor = parent;
            }
            var root = roots[cursor];
            foreach (var id in chain) roots[id] = root;
        }
        var summaries = new Dictionary<Guid, NetworkNodeMetadata>(); var nodeIds = new HashSet<Guid>();
        foreach (var node in nodes)
            if (node is null || node.Id == Guid.Empty || !nodeIds.Add(node.Id) || !byConversation.ContainsKey(node.ConversationId) ||
                node.DisplayName is null || node.BaseTitle is null || !summaries.TryAdd(node.ConversationId, node))
                throw new InvalidDataException("网络总结身份重复或不属于当前工作区。");
        var orderedMetadata = nodes.OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToImmutableArray();
        var projected = orderedMetadata.Select(n => new NetworkNode(n, roots[n.ConversationId])).ToImmutableArray();
        var edges = ImmutableArray.CreateBuilder<NetworkEdge>();
        foreach (var node in orderedMetadata)
        {
            var current = byConversation[node.ConversationId];
            var path = new List<NetworkPathItem> { new(current.Id, current.Title) };
            while (current.ParentId is { } parent)
            {
                current = byConversation[parent]; path.Add(new(current.Id, current.Title));
                if (!summaries.TryGetValue(current.Id, out var ancestor)) continue;
                path.Reverse(); edges.Add(new(ancestor.Id, node.Id, path.ToImmutableArray())); break;
            }
        }
        var relationships = edges.ToImmutable();
        // Canonical ordered content, never enumeration order or a process-specific hash code.
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            WorkspaceId = workspaceId,
            Conversations = conversations.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToArray(),
            Nodes = projected,
            Edges = relationships
        });
        return new(workspaceId, Convert.ToHexString(SHA256.HashData(content)), projected, relationships);
    }
}
