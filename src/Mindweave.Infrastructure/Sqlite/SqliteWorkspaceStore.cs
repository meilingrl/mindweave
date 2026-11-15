using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteWorkspaceStore(SqliteDatabase database) : IWorkspaceStore
{
    public Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<Workspace>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, name, created_at FROM workspace ORDER BY created_at, rowid;";
                using var reader = command.ExecuteReader();
                var list = new List<Workspace>();
                while (reader.Read())
                {
                    list.Add(new Workspace(
                        WorkspaceId.Parse(reader.GetString(0)),
                        WorkspaceName.FromStored(reader.GetString(1)),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
                }

                return list;
            },
            cancellationToken);

    public Task AddAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO workspace (id, name, created_at) VALUES ($id, $name, $created);";
                command.Parameters.AddWithValue("$id", workspace.Id.ToString());
                command.Parameters.AddWithValue("$name", workspace.Name.Value);
                command.Parameters.AddWithValue("$created", workspace.CreatedAt.ToUnixTimeMilliseconds());
                return command.ExecuteNonQuery();
            },
            cancellationToken);
    }

    public Task<Workspace?> RenameAsync(WorkspaceId workspace, WorkspaceName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.Command(
                    transaction,
                    "UPDATE workspace SET name = $name WHERE id = $id RETURNING created_at;",
                    ("$name", name.Value),
                    ("$id", workspace.ToString()));
                return command.ExecuteScalar() is long created
                    ? new Workspace(workspace, name, DateTimeOffset.FromUnixTimeMilliseconds(created))
                    : null;
            },
            cancellationToken);
    }

    public Task<WorkspaceContents> InspectAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync(
            connection =>
            {
                var ws = ("$ws", (object?)workspace.ToString());
                if (connection.Scalar<long>(null, "SELECT EXISTS (SELECT 1 FROM workspace WHERE id = $ws);", ws) == 0)
                {
                    return new WorkspaceContents(false, 0, 0, []);
                }

                var conversations = Conversations(connection, null, workspace);
                return new WorkspaceContents(
                    true,
                    conversations.Count,
                    (int)connection.Scalar<long>(null, "SELECT count(*) FROM summary_node WHERE workspace_id = $ws;", ws),
                    Busy(connection, null, conversations))
                {
                    Archived = conversations.Count(id => SqliteArchive.IsHidden(connection, null, id)),
                    Links = (int)connection.Scalar<long>(null, "SELECT count(*) FROM summary_link WHERE workspace_id = $ws;", ws),
                };
            },
            cancellationToken);

    public Task<WorkspaceDeleteResult> DeleteAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var ws = ("$ws", (object?)workspace.ToString());
                if (connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM workspace WHERE id = $ws);", ws) == 0)
                {
                    return new WorkspaceDeleteResult(true, [], [], null);
                }

                var conversations = Conversations(connection, transaction, workspace);
                var busy = Busy(connection, transaction, conversations);
                if (busy.Count > 0)
                {
                    return new WorkspaceDeleteResult(false, busy, [], null);
                }

                // In dependency order, every RESTRICT reference cleared before its target. Summary nodes and versions
                // reference each other with deferred keys, checked at COMMIT once both are gone; read states cascade
                // with their versions. Task rows go before the messages they target (request traces cascade); retry
                // links are cut before the messages go. Message blocks, reading positions, drafts, context summaries
                // and archive rows cascade with their messages or conversations; ranges are removed by hand because a
                // conversation may still be another one's ancestor until both are gone. Summary links and link suggestions
                // (v13) cascade with their summary nodes, recommendations (v11) with their basis or the workspace.
                const string InWorkspace = "(SELECT id FROM conversation WHERE workspace_id = $ws)";
                const string NodesInWorkspace = "(SELECT id FROM summary_node WHERE workspace_id = $ws)";
                connection.Execute(transaction, "DELETE FROM node_layout WHERE workspace_id = $ws;", ws);
                connection.Execute(transaction, $"DELETE FROM summary_edit_draft WHERE node_id IN {NodesInWorkspace};", ws);
                connection.Execute(transaction, $"DELETE FROM summary_version WHERE node_id IN {NodesInWorkspace};", ws);
                connection.Execute(transaction, "DELETE FROM summary_node WHERE workspace_id = $ws;", ws);
                connection.Execute(transaction, $"DELETE FROM generation_task WHERE conversation_id IN {InWorkspace};", ws);
                connection.Execute(transaction, $"DELETE FROM branch_source WHERE child_id IN {InWorkspace};", ws);
                connection.Execute(transaction, $"DELETE FROM branch_context_range WHERE child_id IN {InWorkspace};", ws);
                connection.Execute(transaction, $"UPDATE message SET supersedes_id = NULL WHERE conversation_id IN {InWorkspace} AND supersedes_id IS NOT NULL;", ws);
                connection.Execute(transaction, $"DELETE FROM message WHERE conversation_id IN {InWorkspace};", ws);
                connection.Execute(transaction, "DELETE FROM conversation WHERE workspace_id = $ws;", ws);
                connection.Execute(transaction, "DELETE FROM workspace_location WHERE workspace_id = $ws;", ws);
                connection.Execute(transaction, "DELETE FROM app_state WHERE key = $key AND value = $ws;", ws, ("$key", LastWorkspaceKey));
                connection.Execute(transaction, "DELETE FROM workspace WHERE id = $ws;", ws);

                // The workspace last left elsewhere comes next; never-visited ones by age, newest first.
                var next = connection.Scalar<string>(transaction, """
                    SELECT w.id FROM workspace w LEFT JOIN workspace_location l ON l.workspace_id = w.id
                    ORDER BY l.updated_at IS NULL, l.updated_at DESC, w.created_at DESC, w.rowid DESC LIMIT 1;
                    """);
                return new WorkspaceDeleteResult(false, [], conversations, next is null ? null : WorkspaceId.Parse(next));
            },
            cancellationToken);

    /// <summary>The key <see cref="SqliteLocationStore"/> keeps the last active workspace under.</summary>
    internal const string LastWorkspaceKey = "last_workspace_id";

    private static List<ConversationId> Conversations(SqliteConnection connection, SqliteTransaction? transaction, WorkspaceId workspace)
    {
        using var command = connection.Command(transaction, "SELECT id FROM conversation WHERE workspace_id = $ws ORDER BY created_at, id;", ("$ws", workspace.ToString()));
        using var reader = command.ExecuteReader();
        var list = new List<ConversationId>();
        while (reader.Read())
        {
            list.Add(ConversationId.Parse(reader.GetString(0)));
        }

        return list;
    }

    private static List<BusyConversation> Busy(SqliteConnection connection, SqliteTransaction? transaction, List<ConversationId> conversations)
    {
        var busy = new List<BusyConversation>();
        foreach (var conversation in conversations)
        {
            var (answer, summary) = SqliteArchive.UnfinishedUserWork(connection, transaction, conversation);
            if (answer || summary)
            {
                busy.Add(new BusyConversation(conversation, answer, summary));
            }
        }

        return busy;
    }
}
