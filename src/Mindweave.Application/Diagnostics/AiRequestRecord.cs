using Mindweave.Core.Conversations;

namespace Mindweave.Application.Diagnostics;

public enum AiRequestOutcome
{
    Completed,
    Truncated,
    Stopped,
    Failed,
}

/// <summary>
/// One HTTP request to an AI service, for diagnostics. A task may make several (an agent loop will), so
/// requests are recorded apart from the task. Holds timings and protocol facts, never content or secrets;
/// <see cref="ErrorDetail"/> is already redacted.
/// </summary>
public sealed record AiRequestRecord(
    Guid Id,
    GenerationTaskId TaskId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    TimeSpan? TimeToResponse,
    TimeSpan? TimeToFirstText,
    AiRequestOutcome Outcome,
    int? HttpStatus,
    string? ProviderRequestId,
    GenerationErrorKind? ErrorKind,
    string? ErrorDetail,
    int OutputCharacters);

/// <summary>Diagnostic persistence. Best effort: a failed write is logged and never blocks generation.</summary>
public interface IAiRequestStore
{
    Task AddAsync(AiRequestRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<AiRequestRecord>> ForTaskAsync(GenerationTaskId task, CancellationToken cancellationToken);
}
