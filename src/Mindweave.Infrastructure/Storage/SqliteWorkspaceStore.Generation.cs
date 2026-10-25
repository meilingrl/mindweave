using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Core.Chat;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<GenerationWork> AcceptReplyAsync(Guid conversationId, string? userText, AiConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        if (userText is not null && string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("请输入问题。", nameof(userText));
        return WriteAsync((connection, transaction) =>
        {
            RequireConversation(connection, transaction, conversationId);
            var messages = ReadMessages(connection, transaction, conversationId);
            if (messages.Any(message => IsActive(message.Status))) throw new InvalidOperationException("此会话已有未结束回答。");
            var sequence = messages.Count == 0 ? 0 : messages[^1].Sequence;
            ChatMessage user;
            Guid? supersedes = null;
            if (userText is null)
            {
                if (messages.Count == 0 || messages[^1].Role != MessageRole.Assistant)
                    throw new InvalidOperationException("只能重试会话最后一条回答。");
                var previous = messages[^1];
                user = messages.Single(message => message.Id == previous.ReplyToUserId && message.Role == MessageRole.User);
                supersedes = previous.Id;
            }
            else
            {
                user = new(Guid.NewGuid(), conversationId, checked(++sequence), MessageRole.User, userText, GenerationStatus.Completed, 0, null, null);
                InsertMessage(connection, transaction, user);
                messages.Add(user);
            }
            // Each user contributes only its newest attempt. Retrying excludes the attempt being replaced.
            var input = LatestInput(messages).Where(message => message.Role == MessageRole.User || message.ReplyToUserId != user.Id).ToArray();
            var answer = new ChatMessage(Guid.NewGuid(), conversationId, checked(++sequence), MessageRole.Assistant, "", GenerationStatus.Queued, 0, user.Id, supersedes);
            InsertMessage(connection, transaction, answer);
            var task = NewGeneration(conversationId, GenerationKind.Reply, answer.Id, configuration, input, null);
            InsertGeneration(connection, transaction, task);
            if (userText is not null)
            {
                // Keep the clear as a revision tombstone: a delayed save must not resurrect submitted input.
                using var clear = ChatCommand(connection, transaction, """
                    UPDATE conversation_draft SET text='',
                        revision=CASE WHEN revision<9223372036854775807 THEN revision+1 ELSE revision END
                    WHERE conversation_id=$id AND text=$text
                    """, ("$id", conversationId), ("$text", userText));
                clear.ExecuteNonQuery();
            }
            return new GenerationWork(task, input);
        });
    }

    public Task<GenerationWork?> AcceptTitleAsync(Guid conversationId, AiConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        return WriteAsync<GenerationWork?>((connection, transaction) =>
        {
            var conversation = RequireConversation(connection, transaction, conversationId);
            if (conversation.TitleIsManual || conversation.TitleRevision != 0) return null;
            using var existing = ChatCommand(connection, transaction, "SELECT 1 FROM generation_task WHERE conversation_id=$id AND kind='Title' LIMIT 1", ("$id", conversationId));
            if (existing.ExecuteScalar() is not null) return null;
            var messages = ReadMessages(connection, transaction, conversationId);
            var answer = LatestInput(messages).FirstOrDefault(message =>
                message.Role == MessageRole.Assistant && message.Status == GenerationStatus.Completed);
            if (answer is null) return null;
            var user = messages.Single(message => message.Id == answer.ReplyToUserId && message.Role == MessageRole.User);
            ChatMessage[] input = [user, answer];
            var task = NewGeneration(conversationId, GenerationKind.Title, null, configuration, input, conversation.TitleRevision);
            InsertGeneration(connection, transaction, task);
            return new(task, input);
        });
    }

    public Task<bool> WriteGenerationAsync(Guid taskId, long revision, string body, GenerationStatus status, string? errorCode = null, string? actualModelId = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Enum.IsDefined(status)) throw new ArgumentException("未知生成状态。", nameof(status));
        ValidateGenerationMetadata(errorCode, actualModelId);
        return WriteAsync((connection, transaction) =>
        {
            var task = ReadGeneration(connection, transaction, taskId);
            if (task is null || !IsActive(task.Status) || revision <= task.Revision ||
                (task.Status == GenerationStatus.Running && status == GenerationStatus.Queued)) return false;
            // Titles have no checkpoint body. Successful title application must use the revision-checked path below.
            if (task.Kind == GenerationKind.Title && status == GenerationStatus.Completed) return false;
            if (task.AnswerMessageId is Guid answer)
            {
                using var update = ChatCommand(connection, transaction, """
                    UPDATE chat_message SET body=$body,status=$status,revision=$revision
                    WHERE id=$id AND conversation_id=$conversation AND status IN ('Queued','Running') AND revision<$revision
                    """, ("$body", body), ("$status", status.ToString()), ("$revision", revision), ("$id", answer), ("$conversation", task.ConversationId));
                if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("回答状态与任务不一致。");
            }
            UpdateGeneration(connection, transaction, taskId, revision, status, errorCode, actualModelId);
            return true;
        });
    }

    public Task<bool> CompleteTitleAsync(Guid taskId, string title)
    {
        var trimmed = ValidateTitle(title);
        return WriteAsync((connection, transaction) =>
        {
            var task = ReadGeneration(connection, transaction, taskId);
            if (task is null || task.Kind != GenerationKind.Title || !IsActive(task.Status)) return false;
            using var update = ChatCommand(connection, transaction, """
                UPDATE conversation SET title=$title,title_revision=title_revision+1
                WHERE id=$id AND title_is_manual=0 AND title_revision=$expected
                """, ("$title", trimmed), ("$id", task.ConversationId), ("$expected", task.ExpectedTitleRevision));
            var applied = update.ExecuteNonQuery() == 1;
            UpdateGeneration(connection, transaction, task.Id, checked(task.Revision + 1),
                applied ? GenerationStatus.Completed : GenerationStatus.Stopped, applied ? null : "title_obsolete", task.ActualModelId);
            return applied;
        });
    }

    public Task<GenerationRecord?> LoadGenerationAsync(Guid taskId) => ReadAsync(connection => ReadGeneration(connection, null, taskId));

    public Task<IReadOnlyList<GenerationRecord>> ListActiveGenerationsAsync() =>
        ReadAsync<IReadOnlyList<GenerationRecord>>(connection => ReadActiveGenerations(connection, null));

    public Task RecoverInterruptedAsync() => WriteAsync((connection, transaction) =>
    {
        foreach (var task in ReadActiveGenerations(connection, transaction))
        {
            var revision = checked(task.Revision + 1);
            if (task.AnswerMessageId is Guid answer)
            {
                using var update = ChatCommand(connection, transaction, """
                    UPDATE chat_message SET status='Interrupted',revision=$revision WHERE id=$id AND status IN ('Queued','Running')
                    """, ("$revision", revision), ("$id", answer));
                if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("回答状态与任务不一致。");
            }
            UpdateGeneration(connection, transaction, task.Id, revision, GenerationStatus.Interrupted, "interrupted", task.ActualModelId);
        }
        return true;
    });

    private static IEnumerable<ChatMessage> LatestInput(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var user in messages.Where(message => message.Role == MessageRole.User))
        {
            yield return user;
            var answer = messages.LastOrDefault(message => message.ReplyToUserId == user.Id);
            if (answer is not null) yield return answer;
        }
    }

    private static bool IsActive(GenerationStatus status) => status is GenerationStatus.Queued or GenerationStatus.Running;

    private static GenerationRecord NewGeneration(Guid conversationId, GenerationKind kind, Guid? answerId,
        AiConfiguration configuration, IReadOnlyList<ChatMessage> input, long? titleRevision) =>
        new(Guid.NewGuid(), conversationId, kind, answerId, GenerationStatus.Queued, 0, configuration,
            input.Select(message => message.Id).ToArray(), titleRevision, null, null);

    private static void InsertMessage(SqliteConnection connection, SqliteTransaction transaction, ChatMessage message)
    {
        using var command = ChatCommand(connection, transaction, """
            INSERT INTO chat_message(id,conversation_id,sequence,role,body,status,revision,reply_to_user_id,supersedes_message_id)
            VALUES($id,$conversation,$sequence,$role,$body,$status,$revision,$reply,$supersedes)
            """, ("$id", message.Id), ("$conversation", message.ConversationId), ("$sequence", message.Sequence),
            ("$role", message.Role.ToString()), ("$body", message.Body), ("$status", message.Status.ToString()),
            ("$revision", message.Revision), ("$reply", message.ReplyToUserId), ("$supersedes", message.SupersedesMessageId));
        command.ExecuteNonQuery();
    }

    private static void InsertGeneration(SqliteConnection connection, SqliteTransaction transaction, GenerationRecord task)
    {
        using var command = ChatCommand(connection, transaction, """
            INSERT INTO generation_task(id,conversation_id,kind,answer_message_id,status,revision,configuration_json,input_ids_json,expected_title_revision)
            VALUES($id,$conversation,$kind,$answer,$status,$revision,$configuration,$inputs,$expected)
            """, ("$id", task.Id), ("$conversation", task.ConversationId), ("$kind", task.Kind.ToString()),
            ("$answer", task.AnswerMessageId), ("$status", task.Status.ToString()), ("$revision", task.Revision),
            ("$configuration", JsonSerializer.Serialize(task.Configuration)), ("$inputs", JsonSerializer.Serialize(task.InputMessageIds)),
            ("$expected", task.ExpectedTitleRevision));
        command.ExecuteNonQuery();
    }

    private static void UpdateGeneration(SqliteConnection connection, SqliteTransaction transaction, Guid id, long revision,
        GenerationStatus status, string? errorCode, string? actualModelId)
    {
        using var command = ChatCommand(connection, transaction, """
            UPDATE generation_task SET revision=$revision,status=$status,error_code=$error,actual_model_id=COALESCE($model,actual_model_id)
            WHERE id=$id AND status IN ('Queued','Running') AND revision<$revision
            """, ("$id", id), ("$revision", revision), ("$status", status.ToString()), ("$error", errorCode), ("$model", actualModelId));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("生成任务状态冲突。");
    }

    private const string GenerationColumns = "id,conversation_id,kind,answer_message_id,status,revision,configuration_json,input_ids_json,expected_title_revision,error_code,actual_model_id";

    private static GenerationRecord? ReadGeneration(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = ChatCommand(connection, transaction, $"SELECT {GenerationColumns} FROM generation_task WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGeneration(reader) : null;
    }

    private static List<GenerationRecord> ReadActiveGenerations(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = ChatCommand(connection, transaction,
            $"SELECT {GenerationColumns} FROM generation_task WHERE status IN ('Queued','Running') ORDER BY rowid");
        using var reader = command.ExecuteReader();
        var result = new List<GenerationRecord>();
        while (reader.Read()) result.Add(ReadGeneration(reader));
        return result;
    }

    private static GenerationRecord ReadGeneration(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)), Enum.Parse<GenerationKind>(reader.GetString(2)), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
        Enum.Parse<GenerationStatus>(reader.GetString(4)), reader.GetInt64(5),
        JsonSerializer.Deserialize<AiConfiguration>(reader.GetString(6)) ?? throw new InvalidDataException("生成配置快照无效。"),
        JsonSerializer.Deserialize<Guid[]>(reader.GetString(7)) ?? throw new InvalidDataException("生成输入快照无效。"),
        reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10));

    private static void ValidateGenerationMetadata(string? errorCode, string? actualModelId)
    {
        if (errorCode is not null && (errorCode.Length is < 1 or > 64 || errorCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))))
            throw new ArgumentException("错误代码必须为简短标识符。", nameof(errorCode));
        if (actualModelId is not null && (string.IsNullOrWhiteSpace(actualModelId) || actualModelId.Length > 256 || actualModelId.Any(char.IsControl)))
            throw new ArgumentException("实际模型标识无效。", nameof(actualModelId));
    }
}
