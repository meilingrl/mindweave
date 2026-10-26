using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteContextSummaryStore(SqliteDatabase database) : IContextSummaryStore
{
    private const string Columns = "id, conversation_id, from_seq, to_seq, input_hash, prompt_version, model, text, created_at";

    public Task<IReadOnlyList<ContextSummary>> ListAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        return database.ReadAsync<IReadOnlyList<ContextSummary>>(
            connection =>
            {
                var list = new List<ContextSummary>();
                foreach (var conversation in conversations.Distinct())
                {
                    using var command = connection.Command(null, $"SELECT {Columns} FROM context_summary WHERE conversation_id = $conv;", ("$conv", conversation.ToString()));
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        list.Add(Read(reader));
                    }
                }

                return list;
            },
            cancellationToken);
    }

    public Task<ContextSummary> AddAsync(ContextSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                var key = summary.Key;
                connection.Execute(transaction, $"""
                    INSERT INTO context_summary ({Columns})
                    VALUES ($id, $conv, $from, $to, $hash, $version, $model, $text, $at)
                    ON CONFLICT (conversation_id, from_seq, to_seq, input_hash, prompt_version) DO NOTHING;
                    """,
                    ("$id", summary.Id.ToString("D")),
                    ("$conv", key.Conversation.ToString()),
                    ("$from", key.FromSeq),
                    ("$to", key.ToSeq),
                    ("$hash", key.InputHash),
                    ("$version", key.PromptVersion),
                    ("$model", summary.Model),
                    ("$text", summary.Text),
                    ("$at", summary.CreatedAt.Ms()));
                using var command = connection.Command(transaction, $"""
                    SELECT {Columns} FROM context_summary
                    WHERE conversation_id = $conv AND from_seq = $from AND to_seq = $to AND input_hash = $hash AND prompt_version = $version;
                    """, ("$conv", key.Conversation.ToString()), ("$from", key.FromSeq), ("$to", key.ToSeq), ("$hash", key.InputHash), ("$version", key.PromptVersion));
                using var reader = command.ExecuteReader();
                return reader.Read() ? Read(reader) : throw new InvalidOperationException("The summary was not stored.");
            },
            cancellationToken);
    }

    public Task RecordTaskInputAsync(GenerationTaskId task, IReadOnlyList<Guid> summaries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        return database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction,
                "UPDATE generation_task SET input_summary_ids = $ids WHERE id = $id;",
                ("$id", task.ToString()),
                ("$ids", JsonSerializer.Serialize(summaries.Select(s => s.ToString("D"))))),
            cancellationToken);
    }

    public Task<IReadOnlySet<MessageId>> CompressedAnswersAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlySet<MessageId>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT target_message_id FROM generation_task
                    WHERE conversation_id = $conv AND kind = 'answer' AND input_summary_ids <> '[]' AND target_message_id IS NOT NULL;
                    """, ("$conv", conversation.ToString()));
                using var reader = command.ExecuteReader();
                var set = new HashSet<MessageId>();
                while (reader.Read())
                {
                    set.Add(MessageId.Parse(reader.GetString(0)));
                }

                return set;
            },
            cancellationToken);

    private static ContextSummary Read(SqliteDataReader reader) =>
        new(
            Guid.ParseExact(reader.GetString(0), "D"),
            new SummaryKey(ConversationId.Parse(reader.GetString(1)), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.Time(8));
}
