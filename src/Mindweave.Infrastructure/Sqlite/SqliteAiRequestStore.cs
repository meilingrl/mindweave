using Mindweave.Application.Diagnostics;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteAiRequestStore(SqliteDatabase database) : IAiRequestStore
{
    public Task AddAsync(AiRequestRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction, """
                INSERT INTO ai_request (id, task_id, started_at, finished_at, response_ms, first_text_ms, outcome,
                                        http_status, provider_request_id, error_kind, error_detail, output_chars,
                                        prompt_tokens, completion_tokens, estimated_tokens)
                VALUES ($id, $task, $started, $finished, $response, $first, $outcome, $status, $provider, $kind, $detail, $chars,
                        $prompt, $completion, $estimated);
                """,
                ("$id", record.Id.ToString("D")),
                ("$task", record.TaskId.ToString()),
                ("$started", record.StartedAt.Ms()),
                ("$finished", record.FinishedAt.Ms()),
                ("$response", (long?)record.TimeToResponse?.TotalMilliseconds),
                ("$first", (long?)record.TimeToFirstText?.TotalMilliseconds),
                ("$outcome", SqlNames.Of(record.Outcome)),
                ("$status", record.HttpStatus),
                ("$provider", record.ProviderRequestId),
                ("$kind", record.ErrorKind is { } k ? SqlNames.Of(k) : null),
                ("$detail", record.ErrorDetail is { Length: > 1000 } d ? d[..1000] : record.ErrorDetail),
                ("$chars", record.OutputCharacters),
                ("$prompt", record.PromptTokens),
                ("$completion", record.CompletionTokens),
                ("$estimated", record.EstimatedTokens)),
            cancellationToken);
    }

    public Task<IReadOnlyList<AiRequestRecord>> ForTaskAsync(GenerationTaskId task, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<AiRequestRecord>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT id, task_id, started_at, finished_at, response_ms, first_text_ms, outcome,
                           http_status, provider_request_id, error_kind, error_detail, output_chars, prompt_tokens, completion_tokens,
                           estimated_tokens
                    FROM ai_request WHERE task_id = $task ORDER BY started_at, rowid;
                    """, ("$task", task.ToString()));
                using var reader = command.ExecuteReader();
                var list = new List<AiRequestRecord>();
                while (reader.Read())
                {
                    var kind = reader.NullableString(9);
                    list.Add(new AiRequestRecord(
                        Guid.ParseExact(reader.GetString(0), "D"),
                        GenerationTaskId.Parse(reader.GetString(1)),
                        reader.Time(2),
                        reader.Time(3),
                        reader.NullableInt64(4) is { } response ? TimeSpan.FromMilliseconds(response) : null,
                        reader.NullableInt64(5) is { } first ? TimeSpan.FromMilliseconds(first) : null,
                        SqlNames.ParseOutcome(reader.GetString(6)),
                        reader.NullableInt64(7) is { } status ? (int)status : null,
                        reader.NullableString(8),
                        kind is null ? null : SqlNames.ParseErrorKind(kind),
                        reader.NullableString(10),
                        reader.GetInt32(11),
                        reader.NullableInt64(12) is { } prompt ? (int)prompt : null,
                        reader.NullableInt64(13) is { } completion ? (int)completion : null,
                        reader.NullableInt64(14) is { } estimated ? (int)estimated : null));
                }

                return list;
            },
            cancellationToken);

    public Task<IReadOnlyList<TokenSample>> RecentTokenSamplesAsync(string baseUrl, string model, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(model);
        return database.ReadAsync<IReadOnlyList<TokenSample>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT r.estimated_tokens, r.prompt_tokens
                    FROM ai_request r JOIN generation_task t ON t.id = r.task_id
                    WHERE t.base_url = $base AND t.model = $model
                      AND r.estimated_tokens IS NOT NULL AND r.prompt_tokens IS NOT NULL
                    ORDER BY r.started_at DESC, r.rowid DESC
                    LIMIT $limit;
                    """, ("$base", baseUrl), ("$model", model), ("$limit", limit));
                using var reader = command.ExecuteReader();
                var list = new List<TokenSample>();
                while (reader.Read())
                {
                    list.Add(new TokenSample(reader.GetInt32(0), reader.GetInt32(1)));
                }

                return list;
            },
            cancellationToken);
    }
}
