using Microsoft.Data.Sqlite;
using Mindweave.Core.Chat;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<ChatContext> ReadContextAsync(Guid conversationId) => ReadAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadContext(connection, transaction, conversationId);
    });

    private static ChatContext ReadContext(SqliteConnection connection, SqliteTransaction transaction, Guid conversationId)
    {
        var input = new List<ChatMessage>();
        foreach (var range in ReadValidatedRanges(connection, transaction, conversationId))
        {
            var messages = ReadMessages(connection, transaction, range.ConversationId).ToDictionary(message => message.Id);
            // Never apply LatestInput to frozen identities: a referenced old attempt stays the old attempt.
            foreach (var id in range.MessageIds)
            {
                if (!messages.TryGetValue(id, out var message) || IsActive(message.Status)) throw new InvalidDataException("冻结消息不存在或不是终态。");
                input.Add(message);
            }
        }
        input.AddRange(ChatHistory.LatestInput(ReadMessages(connection, transaction, conversationId)));
        var source = ReadBranchSource(connection, transaction, conversationId);
        return new(input.ToArray(), source?.Quote, source?.MessageId);
    }
}
