using System.Collections.Immutable;
using Mindweave.Application.Network;
using Mindweave.Core.Network;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore : INetworkStore
{
    public Task<NetworkWorkspaceSnapshot> LoadNetworkAsync(Guid workspaceId) => ReadAsync(c =>
    {
        using var t = c.BeginTransaction(deferred: true);
        var conversations = ImmutableArray.CreateBuilder<NetworkConversation>();
        using (var q = ChatCommand(c, t, """
            SELECT c.id,b.parent_conversation_id,c.title,c.created_at
            FROM conversation c LEFT JOIN branch_source b ON b.child_conversation_id=c.id
            WHERE c.workspace_id=$workspace ORDER BY c.created_at,c.id
            """, ("$workspace", workspaceId)))
        using (var r = q.ExecuteReader())
            while (r.Read()) conversations.Add(new(Guid.Parse(r.GetString(0)), SummaryGuid(r, 1), r.GetString(2), ContextTime(r.GetString(3))));
        var nodes = ImmutableArray.CreateBuilder<NetworkNodeMetadata>();
        // Only metadata is selected: neither summary markdown nor chat message body is materialized.
        using (var q = ChatCommand(c, t, """
            SELECT n.id,n.conversation_id,n.display_name,n.base_title,n.created_at,r.is_read,n.candidate_version_id,
                json_extract(s.coverage_json,'$.OwnHighWaterSequence'),
                EXISTS(SELECT 1 FROM chat_message m WHERE m.conversation_id=n.conversation_id
                    AND m.sequence>json_extract(s.coverage_json,'$.OwnHighWaterSequence')
                    AND m.status NOT IN ('Queued','Running'))
            FROM summary_node n
            LEFT JOIN summary_read_state r ON r.version_id=n.current_version_id
            LEFT JOIN summary_version_source s ON s.version_id=n.current_version_id
            WHERE n.workspace_id=$workspace ORDER BY n.created_at,n.id
            """, ("$workspace", workspaceId)))
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                if (r.IsDBNull(5) || r.IsDBNull(7)) throw new InvalidDataException("网络总结当前版本元数据缺失。");
                nodes.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), ContextTime(r.GetString(4)),
                    !r.GetBoolean(5), !r.IsDBNull(6), r.GetBoolean(8)));
            }
        var graph = NetworkProjection.Build(workspaceId, conversations.ToImmutable(), nodes.ToImmutable());
        var positions = ImmutableDictionary.CreateBuilder<Guid, NodePosition>();
        using (var q = ChatCommand(c, t, "SELECT summary_node_id,x,y FROM node_layout WHERE workspace_id=$workspace ORDER BY summary_node_id", ("$workspace", workspaceId)))
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var position = new NodePosition(r.GetDouble(1), r.GetDouble(2));
                if (!position.IsValid) throw new InvalidDataException("已保存网络坐标无效。");
                positions.Add(Guid.Parse(r.GetString(0)), position);
            }
        return new NetworkWorkspaceSnapshot(graph, positions.ToImmutable());
    });

    public Task<LayoutWriteResult> TrySaveInitialNodePositionAsync(Guid workspaceId, Guid nodeId, NodePosition position) => WriteAsync((c, t) =>
    {
        position.Validate(nameof(position));
        using (var member = ChatCommand(c, t, "SELECT 1 FROM summary_node WHERE workspace_id=$workspace AND id=$node", ("$workspace", workspaceId), ("$node", nodeId)))
            if (member.ExecuteScalar() is null) return new LayoutWriteResult(LayoutWriteStatus.NodeMissing);
        using (var existing = ChatCommand(c, t, "SELECT x,y FROM node_layout WHERE workspace_id=$workspace AND summary_node_id=$node", ("$workspace", workspaceId), ("$node", nodeId)))
        using (var r = existing.ExecuteReader())
            if (r.Read()) return new LayoutWriteResult(LayoutWriteStatus.AlreadyExists, new(r.GetDouble(0), r.GetDouble(1)));
        using var insert = ChatCommand(c, t, "INSERT INTO node_layout(workspace_id,summary_node_id,x,y) VALUES($workspace,$node,$x,$y)",
            ("$workspace", workspaceId), ("$node", nodeId), ("$x", position.X), ("$y", position.Y));
        insert.ExecuteNonQuery(); return new LayoutWriteResult(LayoutWriteStatus.Applied);
    });

    public Task<LayoutWriteResult> SaveLayoutAsync(Guid workspaceId,
        ImmutableDictionary<Guid, NodePosition> positions, ImmutableArray<Guid> expectedNodeIds) => WriteAsync((c, t) =>
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (expectedNodeIds.IsDefault || expectedNodeIds.Distinct().Count() != expectedNodeIds.Length ||
            !positions.Keys.ToHashSet().SetEquals(expectedNodeIds))
            throw new ArgumentException("布局坐标与预期节点必须一一对应且不能重复。", nameof(expectedNodeIds));
        foreach (var position in positions.Values) position.Validate(nameof(positions));
        var actual = new HashSet<Guid>();
        using (var q = ChatCommand(c, t, "SELECT id FROM summary_node WHERE workspace_id=$workspace", ("$workspace", workspaceId)))
        using (var r = q.ExecuteReader()) while (r.Read()) actual.Add(Guid.Parse(r.GetString(0)));
        if (!actual.SetEquals(expectedNodeIds)) return new LayoutWriteResult(LayoutWriteStatus.MembershipChanged);
        // Membership and writes share this immediate writer transaction; no late insert can resurrect a deleted node.
        foreach (var (id, position) in positions.OrderBy(p => p.Key))
        {
            using var q = ChatCommand(c, t, """
                INSERT INTO node_layout(workspace_id,summary_node_id,x,y) VALUES($workspace,$node,$x,$y)
                ON CONFLICT(workspace_id,summary_node_id) DO UPDATE SET x=excluded.x,y=excluded.y
                """, ("$workspace", workspaceId), ("$node", id), ("$x", position.X), ("$y", position.Y));
            q.ExecuteNonQuery();
        }
        return new LayoutWriteResult(LayoutWriteStatus.Applied);
    });
}
