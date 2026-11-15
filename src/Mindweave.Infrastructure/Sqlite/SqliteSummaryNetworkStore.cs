using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteSummaryNetworkStore(SqliteDatabase database) : ISummaryNetworkStore
{
    public Task<IReadOnlyDictionary<SummaryNodeId, NodePosition>> GetLayoutAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyDictionary<SummaryNodeId, NodePosition>>(
            connection =>
            {
                using var command = connection.Command(null, "SELECT node_id, x, y FROM node_layout WHERE workspace_id = $ws;", ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var layout = new Dictionary<SummaryNodeId, NodePosition>();
                while (reader.Read())
                {
                    layout[SummaryNodeId.Parse(reader.GetString(0))] = new NodePosition(reader.GetDouble(1), reader.GetDouble(2));
                }

                return layout;
            },
            cancellationToken);

    public Task<bool> PlaceAsync(WorkspaceId workspace, SummaryNodeId node, NodePosition position, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction, """
                INSERT INTO node_layout (node_id, workspace_id, x, y, updated_at)
                SELECT $node, $ws, $x, $y, $at WHERE EXISTS (SELECT 1 FROM summary_node WHERE id = $node AND workspace_id = $ws)
                ON CONFLICT (node_id) DO NOTHING;
                """, ("$node", node.ToString()), ("$ws", workspace.ToString()), ("$x", position.X), ("$y", position.Y), ("$at", at.Ms())) == 1,
            cancellationToken);

    public Task<int> SaveLayoutAsync(WorkspaceId workspace, IReadOnlyDictionary<SummaryNodeId, NodePosition> positions, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(positions);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                var written = 0;
                foreach (var (node, position) in positions)
                {
                    written += connection.Execute(transaction, """
                        INSERT INTO node_layout (node_id, workspace_id, x, y, updated_at)
                        SELECT $node, $ws, $x, $y, $at WHERE EXISTS (SELECT 1 FROM summary_node WHERE id = $node AND workspace_id = $ws)
                        ON CONFLICT (node_id) DO UPDATE SET x = excluded.x, y = excluded.y, updated_at = excluded.updated_at;
                        """, ("$node", node.ToString()), ("$ws", workspace.ToString()), ("$x", position.X), ("$y", position.Y), ("$at", at.Ms()));
                }

                return written;
            },
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<ConversationId, IReadOnlyList<ConversationId>>> AncestorChainsAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyDictionary<ConversationId, IReadOnlyList<ConversationId>>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT r.child_id, r.ancestor_id FROM branch_context_range r JOIN conversation c ON c.id = r.child_id
                    WHERE c.workspace_id = $ws ORDER BY r.child_id, r.depth;
                    """, ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var chains = new Dictionary<ConversationId, List<ConversationId>>();
                while (reader.Read())
                {
                    var child = ConversationId.Parse(reader.GetString(0));
                    if (!chains.TryGetValue(child, out var chain))
                    {
                        chains[child] = chain = [];
                    }

                    chain.Add(ConversationId.Parse(reader.GetString(1)));
                }

                return chains.ToDictionary(c => c.Key, c => (IReadOnlyList<ConversationId>)c.Value);
            },
            cancellationToken);
}
