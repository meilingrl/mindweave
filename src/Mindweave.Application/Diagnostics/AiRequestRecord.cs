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
/// <see cref="ErrorDetail"/> is already redacted. <see cref="EstimatedTokens"/> is the app's own estimate of the request as
/// sent, kept beside <see cref="PromptTokens"/> to learn how the model counts (<c>TokenCalibration</c>).
/// A request is made either for a task (<see cref="TaskId"/>, whose row holds the model) or, without a task row, for a
/// workspace and a kind (<see cref="Attribution"/>, 2026-11-11, migration v10), with its own <see cref="Model"/> and
/// <see cref="BaseUrl"/> so its token use can still be counted by model.
/// </summary>
public sealed record AiRequestRecord(
    Guid Id,
    GenerationTaskId? TaskId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    TimeSpan? TimeToResponse,
    TimeSpan? TimeToFirstText,
    AiRequestOutcome Outcome,
    int? HttpStatus,
    string? ProviderRequestId,
    GenerationErrorKind? ErrorKind,
    string? ErrorDetail,
    int OutputCharacters,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? EstimatedTokens = null,
    Conversations.RequestAttribution? Attribution = null,
    string? Model = null,
    string? BaseUrl = null,
    string? Purpose = null);

/// <summary>
/// What a request within a task was for (architecture §15.11, migration v16): statistics count resource summaries apart from
/// the answer and the compression made in the same answer task. Null for other tasks and for requests recorded before.
/// </summary>
public static class RequestPurpose
{
    public const string Answer = "answer";
    public const string Compression = "compression";
    public const string ResourceSummary = "resource_summary";
}

/// <summary>One request's own estimate next to the prompt tokens the service billed for it.</summary>
public readonly record struct TokenSample(int Estimate, int Reported);

/// <summary>Diagnostic persistence. Best effort: a failed write is logged and never blocks generation.</summary>
public interface IAiRequestStore
{
    Task AddAsync(AiRequestRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<AiRequestRecord>> ForTaskAsync(GenerationTaskId task, CancellationToken cancellationToken);

    /// <summary>The requests made without a task for this workspace (2026-11-11), oldest first.</summary>
    Task<IReadOnlyList<AiRequestRecord>> ForWorkspaceAsync(Core.Workspaces.WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// The newest requests to this address and model that have both an estimate and billed prompt tokens, newest first.
    /// </summary>
    Task<IReadOnlyList<TokenSample>> RecentTokenSamplesAsync(string baseUrl, string model, int limit, CancellationToken cancellationToken);
}
