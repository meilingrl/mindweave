using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>Run steps (migration v17, architecture §15.12). A step's end and the answer's blocks so far are one transaction.</summary>
internal sealed class SqliteRunStore(SqliteDatabase database) : IRunStore
{
    public Task AddStepAsync(RunStepRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction, """
                INSERT INTO run_step (task_id, ordinal, kind, state, ai_request_id, call_id, tool, risk, approval, target, input, started_at)
                VALUES ($task, $ord, $kind, $state, $request, $call, $tool, $risk, $approval, $target, $input, $started)
                ON CONFLICT (task_id, ordinal) DO NOTHING;
                """,
                ("$task", record.Task.ToString()),
                ("$ord", record.Ordinal),
                ("$kind", SqlNames.Of(record.Kind)),
                ("$state", SqlNames.Of(record.State)),
                ("$request", record.AiRequestId?.ToString("D")),
                ("$call", record.CallId),
                ("$tool", record.Tool),
                ("$risk", record.Risk is { } risk ? SqlNames.Of(risk) : null),
                ("$approval", record.Tool is null ? null : SqlNames.Of(record.Approval ?? Core.Runs.Approval.NotNeeded)),
                ("$target", Bounded(record.Target)),
                ("$input", Bounded(record.Input)),
                ("$started", record.StartedAt.Ms())),
            cancellationToken);
    }

    public Task<bool> FinishStepAsync(RunStepRecord record, MessageId answer, IReadOnlyList<ContentBlock>? blocks, long revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                connection.Execute(transaction, """
                    UPDATE run_step
                    SET state = $state, finished_at = $finished, ai_request_id = coalesce($request, ai_request_id),
                        result_resource_id = $result, error_kind = $kind, error_detail = $detail,
                        approval = coalesce($approval, approval), target = coalesce($target, target)
                    WHERE task_id = $task AND ordinal = $ord;
                    """,
                    ("$task", record.Task.ToString()),
                    ("$ord", record.Ordinal),
                    ("$state", SqlNames.Of(record.State)),
                    ("$finished", record.FinishedAt?.Ms()),
                    ("$request", record.AiRequestId?.ToString("D")),
                    ("$result", record.Result?.ToString()),
                    ("$kind", record.ErrorKind is { } k ? SqlNames.Of(k) : null),
                    ("$detail", Bounded(record.ErrorDetail)),
                    ("$approval", record.Approval is { } approval ? SqlNames.Of(approval) : null),
                    ("$target", Bounded(record.Target)));
                if (record.Result is { } result)
                {
                    connection.Execute(transaction,
                        "UPDATE resource SET produced_by_task = $task, produced_by_step = $ord WHERE id = $id;",
                        ("$task", record.Task.ToString()), ("$ord", record.Ordinal), ("$id", result.ToString()));
                }

                return blocks is not null && SqliteConversationStore.CheckpointBlocks(connection, transaction, answer, blocks, revision);
            },
            cancellationToken);
    }

    public Task SetLimitReachedAsync(GenerationTaskId task, Core.Runs.RunLimit limit, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction,
                "UPDATE generation_task SET run_limit_reached = $limit WHERE id = $id;",
                ("$id", task.ToString()), ("$limit", SqlNames.Of(limit))),
            cancellationToken);

    public Task<IReadOnlyDictionary<MessageId, RunRecord>> RunsAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyDictionary<MessageId, RunRecord>>(
            connection =>
            {
                var tasks = new Dictionary<string, (MessageId Answer, Core.Runs.RunLimit? Limit)>(StringComparer.Ordinal);
                using (var command = connection.Command(null, """
                    SELECT id, target_message_id, run_limit_reached FROM generation_task
                    WHERE conversation_id = $c AND kind = 'answer' AND tools IS NOT NULL AND target_message_id IS NOT NULL;
                    """, ("$c", conversation.ToString())))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        tasks[reader.GetString(0)] = (MessageId.Parse(reader.GetString(1)), SqlNames.ParseRunLimit(reader.NullableString(2)));
                    }
                }

                var steps = tasks.Keys.ToDictionary(k => k, _ => new List<RunStepRecord>(), StringComparer.Ordinal);
                using (var command = connection.Command(null, """
                    SELECT s.task_id, s.ordinal, s.kind, s.state, s.ai_request_id, s.call_id, s.tool, s.risk, s.input,
                           s.result_resource_id, s.started_at, s.finished_at, s.error_kind, s.error_detail, s.approval, s.target
                    FROM run_step s JOIN generation_task t ON t.id = s.task_id
                    WHERE t.conversation_id = $c
                    ORDER BY s.task_id, s.ordinal;
                    """, ("$c", conversation.ToString())))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (steps.TryGetValue(reader.GetString(0), out var list))
                        {
                            list.Add(Read(reader));
                        }
                    }
                }

                return tasks.ToDictionary(t => t.Value.Answer, t => new RunRecord(t.Value.Answer, steps[t.Key], t.Value.Limit));
            },
            cancellationToken);

    private static RunStepRecord Read(SqliteDataReader reader)
    {
        var request = reader.NullableString(4);
        var result = reader.NullableString(9);
        var finished = reader.NullableInt64(11);
        var error = reader.NullableString(12);
        return new RunStepRecord(
            GenerationTaskId.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            SqlNames.ParseStepKind(reader.GetString(2)),
            SqlNames.ParseStepState(reader.GetString(3)),
            reader.Time(10),
            finished is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
            request is null ? null : Guid.ParseExact(request, "D"),
            reader.NullableString(5),
            reader.NullableString(6),
            reader.IsDBNull(7) ? null : SqlNames.ParseRisk(reader.GetString(7)),
            reader.NullableString(8),
            result is null ? null : ResourceId.Parse(result),
            error is null ? null : SqlNames.ParseErrorKind(error),
            reader.NullableString(13))
        {
            Approval = SqlNames.ParseApproval(reader.NullableString(14)) is { } a && a != Core.Runs.Approval.NotNeeded ? a : null,
            Target = reader.NullableString(15),
        };
    }

    public Task SetApprovalAsync(GenerationTaskId task, int ordinal, Core.Runs.RunStepState state, Core.Runs.Approval approval, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction,
                "UPDATE run_step SET state = $state, approval = $approval WHERE task_id = $task AND ordinal = $ord;",
                ("$task", task.ToString()), ("$ord", ordinal), ("$state", SqlNames.Of(state)), ("$approval", SqlNames.Of(approval))),
            cancellationToken);

    private static string? Bounded(string? text) => text is { Length: > 500 } ? text[..500] : text;
}
