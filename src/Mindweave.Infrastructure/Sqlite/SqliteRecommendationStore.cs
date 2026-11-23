using Microsoft.Data.Sqlite;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>Recommendations (migration v11): rows per workspace and kind; cascades remove them with a workspace or summary.</summary>
internal sealed class SqliteRecommendationStore(SqliteDatabase database) : IRecommendationStore
{
    public Task<IReadOnlyList<Recommendation>> ListAsync(WorkspaceId workspace, RecommendationKind kind, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<Recommendation>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT r.id, r.question, r.reason, r.basis_node_id, n.display_name, r.generation_id, r.generated_at, r.seq, r.state, r.updated_at
                    FROM recommendation r LEFT JOIN summary_node n ON n.id = r.basis_node_id
                    WHERE r.workspace_id = $ws AND r.kind = $kind
                    ORDER BY r.state <> 'pending',
                             CASE WHEN r.state = 'pending' THEN r.generated_at ELSE -r.updated_at END,
                             r.seq, r.rowid;
                    """, ("$ws", workspace.ToString()), ("$kind", KindName(kind)));
                using var reader = command.ExecuteReader();
                var list = new List<Recommendation>();
                while (reader.Read())
                {
                    list.Add(new Recommendation(
                        RecommendationId.Parse(reader.GetString(0)),
                        workspace,
                        kind,
                        reader.GetString(1),
                        reader.NullableString(2),
                        reader.NullableString(3) is { } basis ? SummaryNodeId.Parse(basis) : null,
                        reader.NullableString(4),
                        Guid.ParseExact(reader.GetString(5), "D"),
                        reader.Time(6),
                        reader.GetInt32(7),
                        ParseState(reader.GetString(8)))
                    {
                        UpdatedAt = reader.Time(9),
                    });
                }

                return list;
            },
            cancellationToken);

    public Task<int?> AddBatchAsync(WorkspaceId workspace, RecommendationKind kind, RecommendationBatch batch, bool replacePending, CancellationToken cancellationToken)
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

                var stored = 0;
                for (var i = 0; i < batch.Items.Count; i++)
                {
                    var item = batch.Items[i];

                    // The basis must still be a summary of this workspace: one deleted meanwhile leaves the item out.
                    stored += connection.Execute(transaction, """
                        INSERT INTO recommendation (id, workspace_id, kind, question, reason, basis_node_id, generation_id, generated_at, seq, state, updated_at)
                        SELECT $id, $ws, $kind, $question, $reason, $basis, $generation, $at, $seq, 'pending', $at
                        WHERE $basis IS NULL OR EXISTS (SELECT 1 FROM summary_node WHERE id = $basis AND workspace_id = $ws);
                        """,
                        ("$id", RecommendationId.New().ToString()),
                        ws,
                        ("$kind", KindName(kind)),
                        ("$question", item.Question),
                        ("$reason", item.Reason),
                        ("$basis", item.Basis?.ToString()),
                        ("$generation", batch.Generation.ToString("D")),
                        ("$at", batch.At.Ms()),
                        ("$seq", i));
                }

                // The old pending ones go only when new ones replace them (review 2026-11-13): a batch whose every
                // item lost its basis meanwhile stores nothing and must leave the shown ones, as a failure does.
                if (replacePending && stored > 0)
                {
                    connection.Execute(transaction, """
                        DELETE FROM recommendation WHERE workspace_id = $ws AND kind = $kind AND state = 'pending' AND generation_id <> $generation;
                        """, ws, ("$kind", KindName(kind)), ("$generation", batch.Generation.ToString("D")));
                }

                return stored;
            },
            cancellationToken);
    }

    public Task<int> RemovePendingAsync(WorkspaceId workspace, RecommendationKind kind, IReadOnlyCollection<RecommendationId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return Task.FromResult(0);
        }

        return database.WriteAsync(
            (connection, transaction) => ids.Sum(id => connection.Execute(transaction, """
                DELETE FROM recommendation WHERE id = $id AND workspace_id = $ws AND kind = $kind AND state = 'pending';
                """, ("$id", id.ToString()), ("$ws", workspace.ToString()), ("$kind", KindName(kind)))),
            cancellationToken);
    }

    public Task<bool> SetStateAsync(RecommendationId id, RecommendationState state, string normalized, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        if (state == RecommendationState.Pending)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "A recommendation never becomes pending again.");
        }

        return database.WriteAsync(
            (connection, transaction) => state == RecommendationState.Ignored
                ? connection.Execute(transaction, """
                    UPDATE recommendation SET state = 'ignored', question = $normalized, reason = NULL, updated_at = $at
                    WHERE id = $id AND state = 'pending';
                    """, ("$id", id.ToString()), ("$normalized", normalized.Length > 0 ? normalized : "?"), ("$at", at.Ms())) == 1
                : connection.Execute(transaction, """
                    UPDATE recommendation SET state = 'used', updated_at = $at WHERE id = $id AND state = 'pending';
                    """, ("$id", id.ToString()), ("$at", at.Ms())) == 1,
            cancellationToken);
    }

    public Task<RecommendationBases> ListBasesAsync(WorkspaceId workspace, int limit, int bodyChars, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bodyChars);
        return database.ReadAsync(
            connection =>
            {
                var ws = ("$ws", (object?)workspace.ToString());
                var (count, newest, conversations) = Facts(connection, ws);
                using var command = connection.Command(null, """
                    SELECT n.id, n.display_name, v.title, substr(v.body, 1, $chars), n.created_at
                    FROM summary_node n JOIN summary_version v ON v.id = n.current_version_id
                    WHERE n.workspace_id = $ws
                    ORDER BY n.updated_at DESC, n.rowid DESC
                    LIMIT $limit;
                    """, ws, ("$chars", bodyChars), ("$limit", limit));
                using var reader = command.ExecuteReader();
                var list = new List<RecommendationBasis>();
                while (reader.Read())
                {
                    list.Add(new RecommendationBasis(
                        SummaryNodeId.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.Time(4)));
                }

                return new RecommendationBases(list, count, newest, conversations);
            },
            cancellationToken);
    }

    private static (int Count, DateTimeOffset? Newest, int Conversations) Facts(SqliteConnection connection, (string, object?) ws)
    {
        using var command = connection.Command(null, "SELECT count(*), max(created_at), (SELECT count(*) FROM conversation WHERE workspace_id = $ws) FROM summary_node WHERE workspace_id = $ws;", ws);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.NullableInt64(1) is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null, reader.GetInt32(2));
    }

    internal static string KindName(RecommendationKind kind) => kind switch
    {
        RecommendationKind.Question => "question",
        RecommendationKind.Node => "node",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static RecommendationState ParseState(string state) => state switch
    {
        "pending" => RecommendationState.Pending,
        "used" => RecommendationState.Used,
        "ignored" => RecommendationState.Ignored,
        _ => throw new InvalidDataException($"Unknown recommendation state '{state}'."),
    };
}
