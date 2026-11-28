using Mindweave.Application.Diagnostics;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>The request list on 诊断 (D2 §2.2), read from <c>ai_request</c> and its task; writes nothing.</summary>
internal sealed class SqliteRequestList(SqliteDatabase database) : IRequestList
{
    public Task<IReadOnlyList<RequestRow>> RecentAsync(DateTimeOffset since, bool failuresOnly, int limit, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<RequestRow>>(
            connection =>
            {
                using var command = connection.Command(null, $"""
                    SELECT r.id, r.started_at, r.finished_at, COALESCE(r.purpose, t.kind, r.kind, 'answer'), COALESCE(r.model, t.model),
                           COALESCE(r.base_url, t.base_url), r.outcome, r.http_status, r.provider_request_id, r.error_kind, r.error_detail,
                           r.response_ms, r.first_text_ms, r.prompt_tokens, r.completion_tokens, r.cache_read_tokens, r.cache_write_tokens,
                           CASE WHEN t.kind = 'answer' THEN t.target_message_id END
                    FROM ai_request r LEFT JOIN generation_task t ON t.id = r.task_id
                    WHERE r.started_at >= $since {(failuresOnly ? "AND r.outcome = 'failed'" : string.Empty)}
                    ORDER BY r.started_at DESC, r.rowid DESC
                    LIMIT $limit;
                    """, ("$since", since.Ms()), ("$limit", limit));
                using var reader = command.ExecuteReader();
                var rows = new List<RequestRow>();
                while (reader.Read())
                {
                    var kind = reader.NullableString(9);
                    rows.Add(new RequestRow(
                        Guid.ParseExact(reader.GetString(0), "D"),
                        reader.Time(1),
                        reader.Time(2),
                        reader.GetString(3),
                        reader.NullableString(4),
                        RequestRow.HostOf(reader.NullableString(5)),
                        SqlNames.ParseOutcome(reader.GetString(6)),
                        reader.NullableInt64(7) is { } status ? (int)status : null,
                        reader.NullableString(8),
                        kind is null ? null : SqlNames.ParseErrorKind(kind),
                        reader.NullableString(10),
                        reader.NullableInt64(11) is { } response ? TimeSpan.FromMilliseconds(response) : null,
                        reader.NullableInt64(12) is { } first ? TimeSpan.FromMilliseconds(first) : null,
                        Int(13),
                        Int(14),
                        Int(15),
                        Int(16),
                        reader.NullableString(17) is { } answer ? MessageId.Parse(answer) : null));
                }

                return rows;

                int? Int(int ordinal) => reader.NullableInt64(ordinal) is { } value ? (int)value : null;
            },
            cancellationToken);
}
