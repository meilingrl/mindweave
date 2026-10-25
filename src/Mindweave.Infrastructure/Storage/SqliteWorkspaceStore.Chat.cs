using System.Globalization;
using Microsoft.Data.Sqlite;
using Mindweave.Core.Chat;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<IReadOnlyList<Conversation>> ListConversationsAsync(Guid workspaceId) => ReadAsync<IReadOnlyList<Conversation>>(connection =>
    {
        using var command = ChatCommand(connection, null,
            "SELECT id,workspace_id,title,title_is_manual,title_revision,created_at FROM conversation WHERE workspace_id=$id ORDER BY created_at,id", ("$id", workspaceId));
        using var reader = command.ExecuteReader();
        var result = new List<Conversation>();
        while (reader.Read()) result.Add(ReadConversation(reader));
        return result;
    });

    public Task<Conversation> CreateConversationAsync(Guid workspaceId) => WriteAsync((connection, transaction) =>
    {
        var conversation = new Conversation(Guid.NewGuid(), workspaceId, "新会话", false, 0, DateTimeOffset.UtcNow);
        using var command = ChatCommand(connection, transaction,
            "INSERT INTO conversation(id,workspace_id,title,created_at) VALUES($id,$workspace,$title,$created)",
            ("$id", conversation.Id), ("$workspace", workspaceId), ("$title", conversation.Title),
            ("$created", conversation.CreatedAt.ToString("O", CultureInfo.InvariantCulture)));
        command.ExecuteNonQuery();
        return conversation;
    });

    public Task<Conversation?> LoadConversationAsync(Guid conversationId) => ReadAsync(connection => ReadConversation(connection, null, conversationId));

    public Task RenameConversationAsync(Guid conversationId, string title)
    {
        var trimmed = ValidateTitle(title);
        return WriteAsync((connection, transaction) =>
        {
            using var command = ChatCommand(connection, transaction,
                "UPDATE conversation SET title=$title,title_is_manual=1,title_revision=title_revision+1 WHERE id=$id",
                ("$title", trimmed), ("$id", conversationId));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("会话不存在。");
            return true;
        });
    }

    public Task<IReadOnlyList<ChatMessage>> ReadMessagesAsync(Guid conversationId) =>
        ReadAsync<IReadOnlyList<ChatMessage>>(connection => ReadMessages(connection, null, conversationId));

    public Task<ConversationDraft> LoadDraftAsync(Guid conversationId) => ReadAsync(connection =>
    {
        RequireConversation(connection, null, conversationId);
        using var command = ChatCommand(connection, null, "SELECT text,revision FROM conversation_draft WHERE conversation_id=$id", ("$id", conversationId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ConversationDraft(reader.GetString(0), reader.GetInt64(1)) : new("", 0);
    });

    public Task SaveDraftAsync(Guid conversationId, string text, long revision)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (revision < 1) throw new ArgumentException("草稿修订号必须为正数。", nameof(revision));
        return WriteAsync((connection, transaction) =>
        {
            RequireConversation(connection, transaction, conversationId);
            using var command = ChatCommand(connection, transaction, """
                INSERT INTO conversation_draft(conversation_id,text,revision) VALUES($id,$text,$revision)
                ON CONFLICT(conversation_id) DO UPDATE SET text=$text,revision=$revision
                WHERE conversation_draft.revision<$revision
                """, ("$id", conversationId), ("$text", text), ("$revision", revision));
            command.ExecuteNonQuery();
            return true;
        });
    }

    private static Conversation RequireConversation(SqliteConnection connection, SqliteTransaction? transaction, Guid id) =>
        ReadConversation(connection, transaction, id) ?? throw new InvalidOperationException("会话不存在。");

    private static Conversation? ReadConversation(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = ChatCommand(connection, transaction,
            "SELECT id,workspace_id,title,title_is_manual,title_revision,created_at FROM conversation WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    private static Conversation ReadConversation(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(4),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture));

    private static List<ChatMessage> ReadMessages(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = ChatCommand(connection, transaction, """
            SELECT id,conversation_id,sequence,role,body,status,revision,reply_to_user_id,supersedes_message_id
            FROM chat_message WHERE conversation_id=$id ORDER BY sequence
            """, ("$id", id));
        using var reader = command.ExecuteReader();
        var result = new List<ChatMessage>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
            Enum.Parse<MessageRole>(reader.GetString(3)), reader.GetString(4), Enum.Parse<GenerationStatus>(reader.GetString(5)), reader.GetInt64(6),
            reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8))));
        return result;
    }

    private static string ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 160)
            throw new ArgumentException("会话标题需为 1–160 个字符。", nameof(title));
        return title.Trim();
    }

    private static SqliteCommand ChatCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
        return command;
    }
}
