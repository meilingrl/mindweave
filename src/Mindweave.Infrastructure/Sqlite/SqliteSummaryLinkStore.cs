using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>
/// Summary links (migration v13). Each write checks inside its transaction; the schema checks again (pair order, one row
/// per pair, both nodes in the link's workspace, note length), so a bug here fails loudly instead of storing a bad link.
/// </summary>
internal sealed class SqliteSummaryLinkStore(SqliteDatabase database) : ISummaryLinkStore
{
    private const string Columns = "l.id, l.workspace_id, l.node_a, l.node_b, l.note, l.origin, l.created_at";

    /// <summary>The same columns for a RETURNING clause, which takes no table alias.</summary>
    private const string Returned = "id, workspace_id, node_a, node_b, note, origin, created_at";

    public Task<IReadOnlyList<SummaryLink>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<SummaryLink>>(
            connection =>
            {
                using var command = connection.Command(null, $"SELECT {Columns} FROM summary_link l WHERE l.workspace_id = $ws ORDER BY l.created_at, l.rowid;", ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var list = new List<SummaryLink>();
                while (reader.Read())
                {
                    list.Add(Read(reader));
                }

                return list;
            },
            cancellationToken);

    public Task<IReadOnlyList<SummaryLinkEntry>> ListForNodeAsync(SummaryNodeId node, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<SummaryLinkEntry>>(
            connection =>
            {
                using var command = connection.Command(null, $"""
                    SELECT {Columns}, n.id, n.display_name
                    FROM summary_link l
                    JOIN summary_node n ON n.id = CASE WHEN l.node_a = $node THEN l.node_b ELSE l.node_a END
                    WHERE l.node_a = $node OR l.node_b = $node
                    ORDER BY l.created_at, l.rowid;
                    """, ("$node", node.ToString()));
                using var reader = command.ExecuteReader();
                var list = new List<SummaryLinkEntry>();
                while (reader.Read())
                {
                    list.Add(new SummaryLinkEntry(Read(reader), SummaryNodeId.Parse(reader.GetString(7)), reader.GetString(8)));
                }

                return list;
            },
            cancellationToken);

    public Task<IReadOnlyList<SummaryLinkCandidate>> CandidatesAsync(SummaryNodeId node, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<SummaryLinkCandidate>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT n.id, n.display_name,
                           EXISTS (SELECT 1 FROM summary_link l WHERE (l.node_a = $node AND l.node_b = n.id) OR (l.node_b = $node AND l.node_a = n.id))
                    FROM summary_node n
                    WHERE n.workspace_id = (SELECT workspace_id FROM summary_node WHERE id = $node) AND n.id <> $node
                    ORDER BY n.updated_at DESC, n.rowid DESC;
                    """, ("$node", node.ToString()));
                using var reader = command.ExecuteReader();
                var list = new List<SummaryLinkCandidate>();
                while (reader.Read())
                {
                    list.Add(new SummaryLinkCandidate(SummaryNodeId.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2) != 0));
                }

                return list;
            },
            cancellationToken);

    public Task<SummaryLink> AddAsync(SummaryNodeId first, SummaryNodeId second, string? note, SummaryLinkOrigin origin, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                if (first == second)
                {
                    throw new SummaryLinkRefusedException(SummaryLinkRefusal.Self);
                }

                var workspaceA = WorkspaceOf(connection, transaction, first);
                var workspaceB = WorkspaceOf(connection, transaction, second);
                if (workspaceA is null || workspaceB is null)
                {
                    throw new SummaryLinkRefusedException(SummaryLinkRefusal.Missing);
                }

                if (workspaceA != workspaceB)
                {
                    throw new SummaryLinkRefusedException(SummaryLinkRefusal.OtherWorkspace);
                }

                var (a, b) = SummaryLinkPair.Normalize(first, second);
                var workspace = WorkspaceId.Parse(workspaceA);
                if (IsLinked(connection, transaction, workspace, a, b))
                {
                    throw new SummaryLinkRefusedException(SummaryLinkRefusal.Duplicate);
                }

                return Insert(connection, transaction, workspace, a, b, note, origin, at);
            },
            cancellationToken);

    /// <summary>Whether the pair (in the links' order) is linked in the workspace.</summary>
    internal static bool IsLinked(SqliteConnection connection, SqliteTransaction transaction, WorkspaceId workspace, SummaryNodeId a, SummaryNodeId b) =>
        connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM summary_link WHERE workspace_id = $ws AND node_a = $a AND node_b = $b);",
            ("$ws", workspace.ToString()), ("$a", a.ToString()), ("$b", b.ToString())) != 0;

    /// <summary>
    /// Stores a link of a checked pair (in the links' order, both in <paramref name="workspace"/>, not linked yet). A pending
    /// suggestion of the same pair goes in the same transaction: the pair is linked now, so there is nothing left to accept.
    /// </summary>
    internal static SummaryLink Insert(SqliteConnection connection, SqliteTransaction transaction, WorkspaceId workspace, SummaryNodeId a, SummaryNodeId b, string? note, SummaryLinkOrigin origin, DateTimeOffset at)
    {
        var pair = new (string, object?)[] { ("$ws", workspace.ToString()), ("$a", a.ToString()), ("$b", b.ToString()) };
        var link = new SummaryLink(SummaryLinkId.New(), workspace, a, b, note, origin, at);
        connection.Execute(transaction, """
            INSERT INTO summary_link (id, workspace_id, node_a, node_b, note, origin, created_at) VALUES ($id, $ws, $a, $b, $note, $origin, $at);
            """, [.. pair, ("$id", link.Id.ToString()), ("$note", note), ("$origin", OriginName(origin)), ("$at", at.Ms())]);
        connection.Execute(transaction, "DELETE FROM link_suggestion WHERE workspace_id = $ws AND node_a = $a AND node_b = $b AND state = 'pending';", pair);
        return link;
    }

    public Task<SummaryLink> SetNoteAsync(SummaryLinkId link, string? note, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.Command(transaction, $"UPDATE summary_link SET note = $note WHERE id = $id RETURNING {Returned};", ("$id", link.ToString()), ("$note", note));
                using var reader = command.ExecuteReader();
                return reader.Read() ? Read(reader) : throw new SummaryLinkRefusedException(SummaryLinkRefusal.Missing);
            },
            cancellationToken);

    public Task<SummaryLink?> DeleteAsync(SummaryLinkId link, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                SummaryLink? deleted;
                using (var command = connection.Command(transaction, $"DELETE FROM summary_link WHERE id = $id RETURNING {Returned};", ("$id", link.ToString())))
                using (var reader = command.ExecuteReader())
                {
                    deleted = reader.Read() ? Read(reader) : null;
                }

                if (deleted is not null)
                {
                    // The pair counts as ignored for suggestions from now on (requirements §10.3.4); a pending suggestion of
                    // the same pair becomes ignored too.
                    connection.Execute(transaction, """
                        INSERT INTO link_suggestion (id, workspace_id, node_a, node_b, state, created_at, updated_at)
                        VALUES ($id, $ws, $a, $b, 'ignored', $at, $at)
                        ON CONFLICT (workspace_id, node_a, node_b) DO UPDATE SET state = 'ignored', reason = NULL, updated_at = excluded.updated_at;
                        """,
                        ("$id", Guid.CreateVersion7().ToString("D")),
                        ("$ws", deleted.Workspace.ToString()),
                        ("$a", deleted.NodeA.ToString()),
                        ("$b", deleted.NodeB.ToString()),
                        ("$at", at.Ms()));
                }

                return deleted;
            },
            cancellationToken);

    public Task<int> CountTouchingAsync(IReadOnlyCollection<SummaryNodeId> nodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return database.ReadAsync(connection => CountTouching(connection, null, nodes), cancellationToken);
    }

    /// <summary>Links touching any of the nodes, each once (a link between two of them is one link).</summary>
    internal static int CountTouching(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyCollection<SummaryNodeId> nodes)
    {
        if (nodes.Count == 0)
        {
            return 0;
        }

        var ids = new HashSet<string>();
        foreach (var node in nodes.Distinct())
        {
            using var command = connection.Command(transaction, "SELECT id FROM summary_link WHERE node_a = $n OR node_b = $n;", ("$n", node.ToString()));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        return ids.Count;
    }

    private static string? WorkspaceOf(SqliteConnection connection, SqliteTransaction transaction, SummaryNodeId node) =>
        connection.Scalar<string>(transaction, "SELECT workspace_id FROM summary_node WHERE id = $id;", ("$id", node.ToString()));

    private static SummaryLink Read(SqliteDataReader reader) => new(
        SummaryLinkId.Parse(reader.GetString(0)),
        WorkspaceId.Parse(reader.GetString(1)),
        SummaryNodeId.Parse(reader.GetString(2)),
        SummaryNodeId.Parse(reader.GetString(3)),
        reader.NullableString(4),
        ParseOrigin(reader.GetString(5)),
        reader.Time(6));

    internal static string OriginName(SummaryLinkOrigin origin) => origin switch
    {
        SummaryLinkOrigin.User => "user",
        SummaryLinkOrigin.Confirmed => "confirmed",
        SummaryLinkOrigin.Recommended => "recommended",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
    };

    private static SummaryLinkOrigin ParseOrigin(string origin) => origin switch
    {
        "user" => SummaryLinkOrigin.User,
        "confirmed" => SummaryLinkOrigin.Confirmed,
        "recommended" => SummaryLinkOrigin.Recommended,
        _ => throw new InvalidDataException($"Unknown summary link origin '{origin}'."),
    };
}
