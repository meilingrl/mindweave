using Mindweave.Application.Conversations;
using Mindweave.Application.Diagnostics;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteAiRequestStore(SqliteDatabase database) : IAiRequestStore
{
    public Task AddAsync(AiRequestRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if ((record.TaskId is null) == (record.Attribution is null))
        {
            throw new ArgumentException("A request is recorded for a task or for a workspace, not both or neither.", nameof(record));
        }

        if (record.Attribution is { } owner && !RequestAttribution.Known.Contains(owner.Kind))
        {
            throw new ArgumentException($"Unknown request kind '{owner.Kind}'.", nameof(record));
        }

        return database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction, """
                INSERT INTO ai_request (id, task_id, workspace_id, kind, model, base_url, started_at, finished_at, response_ms,
                                        first_text_ms, outcome, http_status, provider_request_id, error_kind, error_detail,
                                        output_chars, prompt_tokens, completion_tokens, estimated_tokens, purpose)
                VALUES ($id, $task, $workspace, $owner, $model, $base, $started, $finished, $response, $first, $outcome, $status,
                        $provider, $kind, $detail, $chars, $prompt, $completion, $estimated, $purpose);
                """,
                ("$id", record.Id.ToString("D")),
                ("$task", record.TaskId?.ToString()),
                ("$workspace", record.Attribution?.Workspace.ToString()),
                ("$owner", record.Attribution?.Kind),
                ("$model", record.Attribution is null ? null : record.Model),
                ("$base", record.Attribution is null ? null : record.BaseUrl),
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
                ("$estimated", record.EstimatedTokens),
                ("$purpose", record.Purpose)),
            cancellationToken);
    }

    public Task<IReadOnlyList<AiRequestRecord>> ForTaskAsync(GenerationTaskId task, CancellationToken cancellationToken) =>
        ReadAsync("task_id = $owner", task.ToString(), cancellationToken);

    public Task<IReadOnlyList<AiRequestRecord>> ForWorkspaceAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        ReadAsync("workspace_id = $owner", workspace.ToString(), cancellationToken);

    private Task<IReadOnlyList<AiRequestRecord>> ReadAsync(string where, string owner, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<AiRequestRecord>>(
            connection =>
            {
                using var command = connection.Command(null, $"""
                    SELECT id, task_id, started_at, finished_at, response_ms, first_text_ms, outcome,
                           http_status, provider_request_id, error_kind, error_detail, output_chars, prompt_tokens, completion_tokens,
                           estimated_tokens, workspace_id, kind, model, base_url, purpose
                    FROM ai_request WHERE {where} ORDER BY started_at, rowid;
                    """, ("$owner", owner));
                using var reader = command.ExecuteReader();
                var list = new List<AiRequestRecord>();
                while (reader.Read())
                {
                    var kind = reader.NullableString(9);
                    var workspace = reader.NullableString(15);
                    list.Add(new AiRequestRecord(
                        Guid.ParseExact(reader.GetString(0), "D"),
                        reader.NullableString(1) is { } taskId ? GenerationTaskId.Parse(taskId) : null,
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
                        reader.NullableInt64(14) is { } estimated ? (int)estimated : null,
                        workspace is null ? null : new RequestAttribution(WorkspaceId.Parse(workspace), reader.GetString(16)),
                        reader.NullableString(17),
                        reader.NullableString(18),
                        reader.NullableString(19)));
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
