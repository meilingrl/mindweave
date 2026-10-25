using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteConversationStore(SqliteDatabase database) : IConversationStore
{
    private const string MessageColumns =
        "id, conversation_id, seq, role, body, state, revision, supersedes_id, error_kind, error_detail, created_at, completed_at";

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<ConversationSummary>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT c.id, c.workspace_id, c.title, c.title_source, c.created_at, c.updated_at,
                           (SELECT body FROM message WHERE conversation_id = c.id AND role = 'user' ORDER BY seq LIMIT 1),
                           (SELECT state FROM message WHERE conversation_id = c.id AND role = 'assistant' ORDER BY seq DESC LIMIT 1)
                    FROM conversation c
                    WHERE c.workspace_id = $ws
                    ORDER BY c.updated_at DESC, c.rowid DESC;
                    """, ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var list = new List<ConversationSummary>();
                while (reader.Read())
                {
                    var last = reader.NullableString(7);
                    list.Add(new ConversationSummary(ReadConversation(reader), reader.NullableString(6), last is null ? null : SqlNames.ParseMessageState(last)));
                }

                return list;
            },
            cancellationToken);

    public Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken) =>
        database.ReadAsync(connection => GetConversation(connection, null, id), cancellationToken);

    public Task<IReadOnlyList<Message>> GetMessagesAsync(ConversationId id, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<Message>>(connection => GetMessages(connection, null, id), cancellationToken);

    public Task CreateAsync(Conversation conversation, Message question, Message answer, GenerationTaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                connection.Execute(transaction, """
                    INSERT INTO conversation (id, workspace_id, title, title_source, created_at, updated_at)
                    VALUES ($id, $ws, $title, $source, $created, $updated);
                    """,
                    ("$id", conversation.Id.ToString()),
                    ("$ws", conversation.WorkspaceId.ToString()),
                    ("$title", conversation.Title),
                    ("$source", SqlNames.Of(conversation.TitleSource)),
                    ("$created", conversation.CreatedAt.Ms()),
                    ("$updated", conversation.UpdatedAt.Ms()));
                InsertMessage(connection, transaction, question with { Seq = 0 });
                InsertMessage(connection, transaction, answer with { Seq = 1 });
                InsertTask(connection, transaction, task);
                return 0;
            },
            cancellationToken);
    }

    public Task<(Message Question, Message Answer)> AppendQuestionAsync(Message question, Message answer, GenerationTaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                ThrowIfUnfinished(connection, transaction, question.ConversationId);
                var next = NextSeq(connection, transaction, question.ConversationId);
                var storedQuestion = question with { Seq = next };
                var storedAnswer = answer with { Seq = next + 1 };
                InsertMessage(connection, transaction, storedQuestion);
                InsertMessage(connection, transaction, storedAnswer);
                InsertTask(connection, transaction, task);
                Touch(connection, transaction, question.ConversationId, question.CreatedAt);
                return (storedQuestion, storedAnswer);
            },
            cancellationToken);
    }

    public Task<Message> AddRetryAsync(Message answer, GenerationTaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                // Re-checked inside the transaction: only the last, finished answer may be superseded.
                var messages = GetMessages(connection, transaction, answer.ConversationId);
                if (answer.Supersedes is not { } replaced || !ConversationRules.CanRetry(messages, replaced))
                {
                    throw new AnswerInProgressException("Only the last finished answer can be retried.");
                }

                var stored = answer with { Seq = messages[^1].Seq + 1 };
                InsertMessage(connection, transaction, stored);
                InsertTask(connection, transaction, task);
                Touch(connection, transaction, answer.ConversationId, answer.CreatedAt);
                return stored;
            },
            cancellationToken);
    }

    public Task AddTaskAsync(GenerationTaskRecord task, CancellationToken cancellationToken) =>
        database.WriteAsync((connection, transaction) => InsertTask(connection, transaction, task), cancellationToken);

    public Task<bool> MarkRunningAsync(GenerationTaskId task, MessageId? answer, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var started = connection.Execute(transaction,
                    "UPDATE generation_task SET state = 'running', started_at = $at WHERE id = $id AND state = 'queued';",
                    ("$id", task.ToString()), ("$at", at.Ms()));
                if (started == 1 && answer is { } message)
                {
                    connection.Execute(transaction,
                        "UPDATE message SET state = 'streaming' WHERE id = $id AND state = 'pending';",
                        ("$id", message.ToString()));
                }

                return started == 1;
            },
            cancellationToken);

    public Task<bool> CheckpointAsync(MessageId answer, string body, long revision, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction, """
                UPDATE message SET body = $body, revision = $rev
                WHERE id = $id AND state = 'streaming' AND revision < $rev;
                """, ("$id", answer.ToString()), ("$body", body), ("$rev", revision)) == 1,
            cancellationToken);

    public Task<bool> FinishAnswerAsync(
        GenerationTaskId task,
        MessageId answer,
        string body,
        long revision,
        MessageState state,
        GenerationErrorKind? errorKind,
        string? errorDetail,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var applied = connection.Execute(transaction, """
                    UPDATE message
                    SET body = $body, state = $state, revision = max(revision + 1, $rev),
                        error_kind = $kind, error_detail = $detail, completed_at = $at
                    WHERE id = $id AND state IN ('pending', 'streaming');
                    """,
                    ("$id", answer.ToString()),
                    ("$body", body),
                    ("$state", SqlNames.Of(state)),
                    ("$rev", revision),
                    ("$kind", errorKind is { } k ? SqlNames.Of(k) : null),
                    ("$detail", Truncate(errorDetail)),
                    ("$at", at.Ms())) == 1;
                FinishTask(connection, transaction, task, SqlNames.TaskStateFor(state), errorKind, errorDetail, at);
                return applied;
            },
            cancellationToken);

    public Task FinishTaskAsync(GenerationTaskId task, GenerationTaskState state, GenerationErrorKind? errorKind, string? errorDetail, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync((connection, transaction) => FinishTask(connection, transaction, task, state, errorKind, errorDetail, at), cancellationToken);

    public Task<bool> SetAutoTitleAsync(ConversationId id, string title, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction,
                "UPDATE conversation SET title = $title, title_source = 'auto' WHERE id = $id AND title_source <> 'manual';",
                ("$id", id.ToString()), ("$title", title)) == 1,
            cancellationToken);

    public Task SetManualTitleAsync(ConversationId id, string title, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction,
                "UPDATE conversation SET title = $title, title_source = 'manual', updated_at = $at WHERE id = $id;",
                ("$id", id.ToString()), ("$title", title), ("$at", at.Ms())),
            cancellationToken);

    public Task<RecoveryReport> RecoverUnfinishedAsync(DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var answers = connection.Execute(transaction,
                    "UPDATE message SET state = 'interrupted', completed_at = $at, revision = revision + 1 WHERE state IN ('pending', 'streaming');",
                    ("$at", at.Ms()));
                var tasks = connection.Execute(transaction,
                    "UPDATE generation_task SET state = 'interrupted', finished_at = $at WHERE state IN ('queued', 'running');",
                    ("$at", at.Ms()));
                return new RecoveryReport(answers, tasks);
            },
            cancellationToken);

    public Task<IReadOnlySet<string>> CredentialRefsInUseAsync(CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlySet<string>>(
            connection =>
            {
                using var command = connection.Command(null, "SELECT DISTINCT credential_ref FROM generation_task WHERE state IN ('queued', 'running');");
                using var reader = command.ExecuteReader();
                var set = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    set.Add(reader.GetString(0));
                }

                return set;
            },
            cancellationToken);

    private static int FinishTask(SqliteConnection connection, SqliteTransaction transaction, GenerationTaskId task, GenerationTaskState state, GenerationErrorKind? errorKind, string? errorDetail, DateTimeOffset at) =>
        connection.Execute(transaction, """
            UPDATE generation_task SET state = $state, error_kind = $kind, error_detail = $detail, finished_at = $at
            WHERE id = $id AND state IN ('queued', 'running');
            """,
            ("$id", task.ToString()),
            ("$state", SqlNames.Of(state)),
            ("$kind", errorKind is { } k ? SqlNames.Of(k) : null),
            ("$detail", Truncate(errorDetail)),
            ("$at", at.Ms()));

    private static void ThrowIfUnfinished(SqliteConnection connection, SqliteTransaction transaction, ConversationId id)
    {
        var unfinished = connection.Scalar<long>(transaction,
            "SELECT count(*) FROM message WHERE conversation_id = $id AND state IN ('pending', 'streaming');",
            ("$id", id.ToString()));
        if (unfinished > 0)
        {
            throw new AnswerInProgressException();
        }
    }

    private static int NextSeq(SqliteConnection connection, SqliteTransaction transaction, ConversationId id) =>
        (int)(connection.Scalar<long?>(transaction, "SELECT max(seq) FROM message WHERE conversation_id = $id;", ("$id", id.ToString())) ?? -1) + 1;

    private static void Touch(SqliteConnection connection, SqliteTransaction transaction, ConversationId id, DateTimeOffset at) =>
        connection.Execute(transaction, "UPDATE conversation SET updated_at = max(updated_at, $at) WHERE id = $id;", ("$id", id.ToString()), ("$at", at.Ms()));

    private static void InsertMessage(SqliteConnection connection, SqliteTransaction transaction, Message m) =>
        connection.Execute(transaction, $"""
            INSERT INTO message ({MessageColumns})
            VALUES ($id, $conv, $seq, $role, $body, $state, $rev, $sup, $kind, $detail, $created, $completed);
            """,
            ("$id", m.Id.ToString()),
            ("$conv", m.ConversationId.ToString()),
            ("$seq", m.Seq),
            ("$role", SqlNames.Of(m.Role)),
            ("$body", m.Body),
            ("$state", SqlNames.Of(m.State)),
            ("$rev", m.Revision),
            ("$sup", m.Supersedes?.ToString()),
            ("$kind", m.ErrorKind is { } k ? SqlNames.Of(k) : null),
            ("$detail", m.ErrorDetail),
            ("$created", m.CreatedAt.Ms()),
            ("$completed", m.CompletedAt?.Ms()));

    private static int InsertTask(SqliteConnection connection, SqliteTransaction transaction, GenerationTaskRecord t) =>
        connection.Execute(transaction, """
            INSERT INTO generation_task (id, kind, conversation_id, target_message_id, state, model_config_id, base_url, protocol,
                                         model, credential_ref, max_output_tokens, input_message_ids, created_at)
            VALUES ($id, $kind, $conv, $target, 'queued', $config, $url, $protocol, $model, $cred, $max, $input, $created);
            """,
            ("$id", t.Id.ToString()),
            ("$kind", SqlNames.Of(t.Kind)),
            ("$conv", t.ConversationId.ToString()),
            ("$target", t.TargetMessageId?.ToString()),
            ("$config", t.Model.ModelConfigId),
            ("$url", t.Model.BaseUrl),
            ("$protocol", SqlNames.Of(t.Model.Protocol)),
            ("$model", t.Model.Model),
            ("$cred", t.Model.CredentialRef),
            ("$max", t.Model.MaxOutputTokens),
            ("$input", JsonSerializer.Serialize(t.InputMessageIds.Select(i => i.ToString()))),
            ("$created", t.CreatedAt.Ms()));

    private static Conversation? GetConversation(SqliteConnection connection, SqliteTransaction? transaction, ConversationId id)
    {
        using var command = connection.Command(transaction,
            "SELECT id, workspace_id, title, title_source, created_at, updated_at FROM conversation WHERE id = $id;",
            ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    private static List<Message> GetMessages(SqliteConnection connection, SqliteTransaction? transaction, ConversationId id)
    {
        using var command = connection.Command(transaction, $"SELECT {MessageColumns} FROM message WHERE conversation_id = $id ORDER BY seq;", ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        var list = new List<Message>();
        while (reader.Read())
        {
            var kind = reader.NullableString(8);
            var supersedes = reader.NullableString(7);
            var completed = reader.NullableInt64(11);
            list.Add(new Message(
                MessageId.Parse(reader.GetString(0)),
                ConversationId.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                SqlNames.ParseRole(reader.GetString(3)),
                reader.GetString(4),
                SqlNames.ParseMessageState(reader.GetString(5)),
                reader.GetInt64(6),
                supersedes is null ? null : MessageId.Parse(supersedes),
                kind is null ? null : SqlNames.ParseErrorKind(kind),
                reader.NullableString(9),
                reader.Time(10),
                completed is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null));
        }

        return list;
    }

    private static Conversation ReadConversation(SqliteDataReader reader) =>
        new(
            ConversationId.Parse(reader.GetString(0)),
            WorkspaceId.Parse(reader.GetString(1)),
            reader.NullableString(2),
            SqlNames.ParseTitleSource(reader.GetString(3)),
            reader.Time(4),
            reader.Time(5));

    private static string? Truncate(string? detail) => detail is { Length: > 1000 } ? detail[..1000] : detail;
}
