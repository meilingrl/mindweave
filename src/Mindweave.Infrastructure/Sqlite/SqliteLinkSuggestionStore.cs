using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>
/// Link suggestions (migration v13's link_suggestion, made for this in 8a; no migration of their own). Each write checks
/// inside its transaction; the schema checks again (pair order, one row per pair, both nodes in the workspace, note length,
/// an ignored row without its reason), so a bug here fails loudly instead of storing a bad suggestion.
/// </summary>
internal sealed class SqliteLinkSuggestionStore(SqliteDatabase database) : ILinkSuggestionStore
{
    public Task<IReadOnlyList<LinkSuggestion>> ListPendingAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<LinkSuggestion>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT s.id, s.node_a, s.node_b, a.display_name, b.display_name, s.note, s.reason, s.generation_id, s.created_at
                    FROM link_suggestion s
                    JOIN summary_node a ON a.id = s.node_a
                    JOIN summary_node b ON b.id = s.node_b
                    WHERE s.workspace_id = $ws AND s.state = 'pending'
                    ORDER BY s.created_at, s.rowid;
                    """, ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var list = new List<LinkSuggestion>();
                while (reader.Read())
                {
                    list.Add(new LinkSuggestion(
                        LinkSuggestionId.Parse(reader.GetString(0)),
                        workspace,
                        SummaryNodeId.Parse(reader.GetString(1)),
                        SummaryNodeId.Parse(reader.GetString(2)),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.NullableString(5),
                        reader.NullableString(6),
                        Guid.ParseExact(reader.GetString(7), "D"),
                        reader.Time(8)));
                }

                return list;
            },
            cancellationToken);

    public Task<IReadOnlyList<(SummaryNodeId A, SummaryNodeId B)>> ListAvoidedAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<(SummaryNodeId, SummaryNodeId)>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT node_a, node_b FROM summary_link WHERE workspace_id = $ws
                    UNION
                    SELECT node_a, node_b FROM link_suggestion WHERE workspace_id = $ws AND state = 'ignored';
                    """, ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var list = new List<(SummaryNodeId, SummaryNodeId)>();
                while (reader.Read())
                {
                    list.Add((SummaryNodeId.Parse(reader.GetString(0)), SummaryNodeId.Parse(reader.GetString(1))));
                }

                return list;
            },
            cancellationToken);

    public Task<int?> AddBatchAsync(WorkspaceId workspace, LinkSuggestionBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return database.WriteAsync<int?>(
            (connection, transaction) =>
            {
                var ws = ("$ws", (object?)workspace.ToString());
                if (connection.Scalar<long>(transaction, "SELECT count(*) FROM workspace WHERE id = $ws;", ws) == 0)
                {
                    return null;
                }

                var generation = ("$generation", (object?)batch.Generation.ToString("D"));
                var stored = 0;
                for (var i = 0; i < batch.Items.Count; i++)
                {
                    var item = batch.Items[i];
                    var (a, b) = SummaryLinkPair.Normalize(item.NodeA, item.NodeB);
                    var pair = new (string, object?)[] { ws, ("$a", a.ToString()), ("$b", b.ToString()) };

                    // Both summaries still in this workspace, the pair neither linked nor ignored meanwhile, and not stored
                    // by this batch already (the service drops repeats; this keeps a repeat from counting twice).
                    if (a == b
                        || connection.Scalar<long>(transaction, "SELECT count(*) FROM summary_node WHERE workspace_id = $ws AND id IN ($a, $b);", pair) != 2
                        || connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM summary_link WHERE workspace_id = $ws AND node_a = $a AND node_b = $b);", pair) != 0
                        || connection.Scalar<long>(transaction, """
                            SELECT EXISTS (SELECT 1 FROM link_suggestion WHERE workspace_id = $ws AND node_a = $a AND node_b = $b
                                           AND (state = 'ignored' OR generation_id = $generation));
                            """, [.. pair, generation]) != 0)
                    {
                        continue;
                    }

                    // A pair still pending from an earlier request is suggested again: it takes this request's note and reason.
                    stored += connection.Execute(transaction, """
                        INSERT INTO link_suggestion (id, workspace_id, node_a, node_b, note, reason, state, generation_id, created_at, updated_at)
                        VALUES ($id, $ws, $a, $b, $note, $reason, 'pending', $generation, $at, $at)
                        ON CONFLICT (workspace_id, node_a, node_b) DO UPDATE
                            SET note = excluded.note, reason = excluded.reason, generation_id = excluded.generation_id,
                                created_at = excluded.created_at, updated_at = excluded.updated_at
                            WHERE state = 'pending';
                        """,
                        [.. pair,
                        generation,
                        ("$id", LinkSuggestionId.New().ToString()),
                        ("$note", item.Note),
                        ("$reason", item.Reason),
                        ("$at", batch.At.Ms() + i)]);
                }

                // The old pending ones go only when new ones replace them: a batch that stored nothing (every pair gone or
                // decided meanwhile) leaves the shown ones, as a failure does (same rule as recommendations).
                if (stored > 0)
                {
                    connection.Execute(transaction, """
                        DELETE FROM link_suggestion WHERE workspace_id = $ws AND state = 'pending' AND generation_id <> $generation;
                        """, ws, generation);
                }

                return stored;
            },
            cancellationToken);
    }

    public Task<LinkSuggestionAcceptance> AcceptAsync(LinkSuggestionId suggestion, string? note, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var id = ("$id", (object?)suggestion.ToString());
                string workspace, a, b;
                using (var command = connection.Command(transaction, "SELECT workspace_id, node_a, node_b FROM link_suggestion WHERE id = $id AND state = 'pending';", id))
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        return new LinkSuggestionAcceptance(null, SummaryLinkRefusal.Missing, null);
                    }

                    (workspace, a, b) = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
                }

                var ws = WorkspaceId.Parse(workspace);
                var (nodeA, nodeB) = (SummaryNodeId.Parse(a), SummaryNodeId.Parse(b));
                if (SqliteSummaryLinkStore.IsLinked(connection, transaction, ws, nodeA, nodeB))
                {
                    connection.Execute(transaction, "DELETE FROM link_suggestion WHERE id = $id;", id);
                    return new LinkSuggestionAcceptance(null, SummaryLinkRefusal.Duplicate, ws);
                }

                // Inserting the link removes the pending row of the pair (SqliteSummaryLinkStore.Insert).
                var link = SqliteSummaryLinkStore.Insert(connection, transaction, ws, nodeA, nodeB, note, SummaryLinkOrigin.Confirmed, at);
                return new LinkSuggestionAcceptance(link, null, ws);
            },
            cancellationToken);

    public Task<bool> IgnoreAsync(LinkSuggestionId suggestion, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => Ignore(connection, transaction, "id = $key", ("$key", suggestion.ToString()), at) == 1,
            cancellationToken);

    public Task<int> IgnoreAllAsync(WorkspaceId workspace, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => Ignore(connection, transaction, "workspace_id = $key", ("$key", workspace.ToString()), at),
            cancellationToken);

    /// <summary>Pending rows matching <paramref name="where"/> become ignored; the reason goes (the note and generation stay).</summary>
    private static int Ignore(SqliteConnection connection, SqliteTransaction transaction, string where, (string, object?) key, DateTimeOffset at) =>
        connection.Execute(transaction, $"UPDATE link_suggestion SET state = 'ignored', reason = NULL, updated_at = $at WHERE {where} AND state = 'pending';", key, ("$at", at.Ms()));
}
