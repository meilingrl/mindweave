using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteSummaryNetworkStore(SqliteDatabase database) : ISummaryNetworkStore
{
    public Task<IReadOnlyDictionary<ConversationId, ExplorationPlace>> GetExplorationPlacesAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyDictionary<ConversationId, ExplorationPlace>>(
            connection =>
            {
                using var command = connection.Command(null, "SELECT root_conversation_id, x, y, angle, size FROM exploration_place WHERE workspace_id = $ws;", ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var places = new Dictionary<ConversationId, ExplorationPlace>();
                while (reader.Read())
                {
                    places[ConversationId.Parse(reader.GetString(0))] = new ExplorationPlace(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetInt32(4));
                }

                return places;
            },
            cancellationToken);

    public Task<int> SaveExplorationPlacesAsync(WorkspaceId workspace, IReadOnlyDictionary<ConversationId, ExplorationPlace> places, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(places);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                var written = 0;
                foreach (var (root, place) in places)
                {
                    written += connection.Execute(transaction, """
                        INSERT INTO exploration_place (root_conversation_id, workspace_id, x, y, angle, size, updated_at)
                        SELECT $root, $ws, $x, $y, $angle, $size, $at WHERE EXISTS (SELECT 1 FROM conversation WHERE id = $root AND workspace_id = $ws)
                        ON CONFLICT (root_conversation_id) DO UPDATE SET x = excluded.x, y = excluded.y, angle = excluded.angle, size = excluded.size, updated_at = excluded.updated_at;
                        """, ("$root", root.ToString()), ("$ws", workspace.ToString()), ("$x", place.X), ("$y", place.Y), ("$angle", place.Angle), ("$size", Math.Max(1, place.Size)), ("$at", at.Ms()));
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

    public Task<IReadOnlyDictionary<ConversationId, int>> QuestionCountsAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyDictionary<ConversationId, int>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT m.conversation_id, COUNT(*) FROM message m JOIN conversation c ON c.id = m.conversation_id
                    WHERE c.workspace_id = $ws AND m.role = 'user' GROUP BY m.conversation_id;
                    """, ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var counts = new Dictionary<ConversationId, int>();
                while (reader.Read())
                {
                    counts[ConversationId.Parse(reader.GetString(0))] = reader.GetInt32(1);
                }

                return counts;
            },
            cancellationToken);
}
