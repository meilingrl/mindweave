using Mindweave.Application.ManualBackup;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>What a manual backup reads beyond the other stores (docs/design/sharing-backup.md §5). One snapshot per call.</summary>
internal sealed class SqliteManualBackupStore(SqliteDatabase database) : IManualBackupStore
{
    /// <summary>
    /// Per workspace: its conversations; the size of the originals its conversations refer to, each content once; and the
    /// text it holds (messages, summary versions, extracted text), which the package carries in its document and, mostly
    /// again, in its readable page, compressed.
    /// </summary>
    public Task<IReadOnlyDictionary<WorkspaceId, (int Conversations, long Bytes)>> SizesAsync(CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyDictionary<WorkspaceId, (int Conversations, long Bytes)>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT w.id,
                        (SELECT count(*) FROM conversation c WHERE c.workspace_id = w.id),
                        (SELECT coalesce(sum(byte_size), 0) FROM (
                            SELECT DISTINCT r.sha256, r.byte_size FROM resource r WHERE r.workspace_id = w.id AND r.conversation_id IS NOT NULL)),
                        (SELECT coalesce(sum(length(CAST(b.text AS BLOB)) + coalesce(length(CAST(b.data AS BLOB)), 0)), 0)
                            FROM message_block b JOIN message m ON m.id = b.message_id JOIN conversation c ON c.id = m.conversation_id
                            WHERE c.workspace_id = w.id),
                        (SELECT coalesce(sum(length(CAST(v.body AS BLOB))), 0)
                            FROM summary_version v JOIN summary_node n ON n.id = v.node_id WHERE n.workspace_id = w.id),
                        (SELECT coalesce(sum(length(CAST(d.text AS BLOB))), 0)
                            FROM resource_derivative d JOIN resource r ON r.id = d.resource_id
                            WHERE r.workspace_id = w.id AND r.conversation_id IS NOT NULL AND d.kind = 'text')
                    FROM workspace w;
                    """);
                using var reader = command.ExecuteReader();
                var sizes = new Dictionary<WorkspaceId, (int, long)>();
                while (reader.Read())
                {
                    sizes[WorkspaceId.Parse(reader.GetString(0))] = (reader.GetInt32(1), reader.GetInt64(2) + reader.GetInt64(3) + reader.GetInt64(4) + reader.GetInt64(5));
                }

                return sizes;
            },
            cancellationToken);

    /// <summary>Only the task's model column: its connection, address and credential stay where they are.</summary>
    public Task<IReadOnlyDictionary<MessageId, string>> AnswerModelsAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyDictionary<MessageId, string>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT target_message_id, model FROM generation_task
                    WHERE conversation_id = $c AND kind = 'answer' AND target_message_id IS NOT NULL
                    ORDER BY created_at, id;
                    """, ("$c", conversation.ToString()));
                using var reader = command.ExecuteReader();
                var models = new Dictionary<MessageId, string>();
                while (reader.Read())
                {
                    models[MessageId.Parse(reader.GetString(0))] = reader.GetString(1);
                }

                return models;
            },
            cancellationToken);
}
