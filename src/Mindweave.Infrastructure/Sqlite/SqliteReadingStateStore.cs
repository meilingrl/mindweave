using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteReadingStateStore(SqliteDatabase database) : IReadingStateStore
{
    public Task<ReadingPosition?> GetPositionAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken) =>
        database.ReadAsync(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT message_id, block_ordinal, locator, locator_version FROM reading_position
                    WHERE conversation_id = $conv AND slot_key = $slot;
                    """, ("$conv", conversation.ToString()), ("$slot", slot.Value));
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? new ReadingPosition(MessageId.Parse(reader.GetString(0)), reader.GetInt32(1), reader.NullableString(2), reader.GetInt32(3))
                    : null;
            },
            cancellationToken);

    public Task SavePositionAsync(ConversationId conversation, ViewerSlotKey slot, ReadingPosition position, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);
        return database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction, """
                INSERT INTO reading_position (conversation_id, slot_key, message_id, block_ordinal, locator, locator_version, updated_at)
                SELECT $conv, $slot, $message, $block, $locator, $version, $at
                WHERE EXISTS (SELECT 1 FROM message WHERE id = $message AND conversation_id = $conv)
                ON CONFLICT (conversation_id, slot_key) DO UPDATE SET
                    message_id = excluded.message_id, block_ordinal = excluded.block_ordinal, locator = excluded.locator,
                    locator_version = excluded.locator_version, updated_at = excluded.updated_at;
                """,
                ("$conv", conversation.ToString()),
                ("$slot", slot.Value),
                ("$message", position.Message.ToString()),
                ("$block", position.Block),
                ("$locator", position.Locator),
                ("$version", position.LocatorVersion),
                ("$at", at.Ms())),
            cancellationToken);
    }

    public Task<string?> GetDraftAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken) =>
        database.ReadAsync(
            connection => connection.Scalar<string>(null,
                "SELECT text FROM draft WHERE conversation_id = $conv AND slot_key = $slot;",
                ("$conv", conversation.ToString()), ("$slot", slot.Value)),
            cancellationToken);

    public Task SaveDraftAsync(ConversationId conversation, ViewerSlotKey slot, string text, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return database.WriteAsync(
            (connection, transaction) => text.Length == 0
                ? connection.Execute(transaction, "DELETE FROM draft WHERE conversation_id = $conv AND slot_key = $slot;",
                    ("$conv", conversation.ToString()), ("$slot", slot.Value))
                : connection.Execute(transaction, """
                    INSERT INTO draft (conversation_id, slot_key, text, updated_at)
                    SELECT $conv, $slot, $text, $at WHERE EXISTS (SELECT 1 FROM conversation WHERE id = $conv)
                    ON CONFLICT (conversation_id, slot_key) DO UPDATE SET text = excluded.text, updated_at = excluded.updated_at;
                    """, ("$conv", conversation.ToString()), ("$slot", slot.Value), ("$text", text), ("$at", at.Ms())),
            cancellationToken);
    }
}
