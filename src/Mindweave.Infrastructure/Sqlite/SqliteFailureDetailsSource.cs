using Mindweave.Application.Diagnostics;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>An answer's last failed request, read from <c>ai_request</c> and its task (D2 §2.1); writes nothing.</summary>
internal sealed class SqliteFailureDetailsSource(SqliteDatabase database) : IFailureDetailsSource
{
    public Task<FailureDetails?> ForAnswerAsync(MessageId answer, CancellationToken cancellationToken) =>
        ReadAsync("t.target_message_id = $owner", "target_message_id = $owner", answer.ToString(), cancellationToken);

    public Task<FailureDetails?> ForTaskAsync(GenerationTaskId task, CancellationToken cancellationToken) =>
        ReadAsync("t.id = $owner", "id = $owner", task.ToString(), cancellationToken);

    private Task<FailureDetails?> ReadAsync(string requestOwner, string taskOwner, string owner, CancellationToken cancellationToken) =>
        database.ReadAsync(
            connection =>
            {
                using (var command = connection.Command(null, $"""
                    SELECT r.started_at, r.finished_at, r.http_status, r.provider_request_id, r.error_detail, COALESCE(r.model, t.model)
                    FROM ai_request r JOIN generation_task t ON t.id = r.task_id
                    WHERE {requestOwner} AND r.outcome = 'failed'
                    ORDER BY r.started_at DESC, r.rowid DESC
                    LIMIT 1;
                    """, ("$owner", owner)))
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var started = reader.Time(0);
                        var finished = reader.Time(1);
                        var detail = reader.NullableString(4);
                        return new FailureDetails(
                            finished,
                            reader.NullableString(5),
                            reader.NullableInt64(2) is { } status ? (int)status : null,
                            reader.NullableString(3),
                            finished - started,
                            string.IsNullOrWhiteSpace(detail) ? null : Redaction.Scrub(detail.Trim()));
                    }
                }

                // Nothing was recorded (the request never left, or its record was lost): the task still names the model.
                using var task = connection.Command(null, $"""
                    SELECT model, COALESCE(finished_at, created_at) FROM generation_task
                    WHERE {taskOwner}
                    ORDER BY created_at DESC, rowid DESC
                    LIMIT 1;
                    """, ("$owner", owner));
                using var row = task.ExecuteReader();
                return row.Read() ? FailureDetails.Kept(row.Time(1), row.GetString(0), null) : (FailureDetails?)null;
            },
            cancellationToken);
}
