using Mindweave.Core.Conversations;

namespace Mindweave.Application.Diagnostics;

/// <summary>
/// One row of the request list on 诊断 (D2 §2.2, requirements §10.8): a recorded AI request with what it was for, which model
/// and service host, how it ended and how long it took. Holds no content, because none is stored. <see cref="Purpose"/> is a
/// stored name: the task's kind, the request's own purpose within a task, or the kind of a request made without a task.
/// <see cref="Answer"/> is the answer a request was made for, so a failure block can find its row.
/// </summary>
public sealed record RequestRow(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Purpose,
    string? Model,
    string? Host,
    AiRequestOutcome Outcome,
    int? HttpStatus,
    string? ProviderRequestId,
    GenerationErrorKind? ErrorKind,
    string? ErrorDetail,
    TimeSpan? TimeToResponse,
    TimeSpan? TimeToFirstText,
    int? PromptTokens,
    int? CompletionTokens,
    int? CacheReadTokens,
    int? CacheWriteTokens,
    MessageId? Answer)
{
    public TimeSpan Duration => FinishedAt - StartedAt;

    /// <summary>The same details a failure block shows.</summary>
    public FailureDetails Details => new(FinishedAt, Model, HttpStatus, ProviderRequestId, Duration, ErrorDetail is { Length: > 0 } text ? Redaction.Scrub(text) : null);

    /// <summary>The host of a service address, never its path or query; null when there is none.</summary>
    public static string? HostOf(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Authority : null;
}

/// <summary>Reads the request records (D2 §2.2); writes nothing.</summary>
public interface IRequestList
{
    /// <summary>The requests started at or after <paramref name="since"/>, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<RequestRow>> RecentAsync(DateTimeOffset since, bool failuresOnly, int limit, CancellationToken cancellationToken);
}
