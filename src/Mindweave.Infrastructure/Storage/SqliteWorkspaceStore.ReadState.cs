using Microsoft.Data.Sqlite;
using Mindweave.Core.Branches;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<ConversationReadState?> LoadReadStateAsync(Guid conversationId) => ReadAsync(connection =>
    {
        using var command = ChatCommand(connection, null, "SELECT message_id,source_start,offset_within_block,format_version,renderer_profile,revision FROM conversation_read_state WHERE conversation_id=$id", ("$id", conversationId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ConversationReadState(conversationId, reader.IsDBNull(0) ? null : new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetDouble(2), reader.GetInt32(3), reader.GetString(4)), reader.GetInt64(5)) : null;
    });

    public Task SaveReadStateAsync(ConversationReadState state) => WriteAsync((connection, transaction) =>
    {
        SaveReadState(connection, transaction, state); return true;
    });

    private static void SaveReadState(SqliteConnection connection, SqliteTransaction transaction, ConversationReadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Revision < 1) throw new ArgumentException("阅读修订号必须为正数。", nameof(state));
        RequireConversation(connection, transaction, state.ConversationId);
        var location = state.Location;
        if (location is not null)
        {
            if (location.SourceStart < 0 || !double.IsFinite(location.OffsetWithinBlock) || location.OffsetWithinBlock < 0 ||
                location.FormatVersion != 1 || location.RendererProfile != SupportedRendererProfile)
                throw new ArgumentException("阅读位置无效。", nameof(state));
            using var owner = ChatCommand(connection, transaction, "SELECT 1 FROM chat_message WHERE id=$message AND conversation_id=$conversation", ("$message", location.MessageId), ("$conversation", state.ConversationId));
            if (owner.ExecuteScalar() is null) throw new ArgumentException("阅读消息必须属于当前会话。", nameof(state));
        }
        using var command = ChatCommand(connection, transaction, """
            INSERT INTO conversation_read_state VALUES($id,$message,$start,$offset,$version,$profile,$revision)
            ON CONFLICT(conversation_id) DO UPDATE SET message_id=$message,source_start=$start,offset_within_block=$offset,
                format_version=$version,renderer_profile=$profile,revision=$revision WHERE conversation_read_state.revision<$revision
            """, ("$id", state.ConversationId), ("$message", location?.MessageId), ("$start", location?.SourceStart),
            ("$offset", location?.OffsetWithinBlock), ("$version", location?.FormatVersion), ("$profile", location?.RendererProfile), ("$revision", state.Revision));
        command.ExecuteNonQuery();
    }
}
