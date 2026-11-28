using Mindweave.Application.Ai;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Diagnostics;

/// <summary>
/// What the details of one failed request are (D2 §2.1, requirements §10.8): the time, the model, the status the service
/// returned, the service's request ID, the duration and the service's own text, already scrubbed. Every part may be
/// missing: requests are recorded best effort (D-007), and a failure before anything was sent has no status. Holds no
/// conversation text and no key.
/// </summary>
public sealed record FailureDetails(
    DateTimeOffset? At,
    string? Model,
    int? HttpStatus,
    string? ProviderRequestId,
    TimeSpan? Duration,
    string? ServiceText)
{
    /// <summary>Only what the failed message or result itself kept.</summary>
    public static FailureDetails Kept(DateTimeOffset? at, string? model, string? serviceText) =>
        new(at, model, null, null, null, Clean(serviceText));

    /// <summary>A failed request as the connection or web search test saw it.</summary>
    public static FailureDetails Of(ChatClientException failure, DateTimeOffset at, string? model, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(at, model, failure.HttpStatus, failure.ProviderRequestId, duration, Clean(failure.Message));
    }

    /// <summary>The recorded request, with the model of its task when the row holds none.</summary>
    public static FailureDetails Of(AiRequestRecord request, string? taskModel)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(
            request.FinishedAt,
            request.Model ?? taskModel,
            request.HttpStatus,
            request.ProviderRequestId,
            request.FinishedAt - request.StartedAt,
            Clean(request.ErrorDetail));
    }

    /// <summary>Whether there is anything to show beyond the time.</summary>
    public bool HasAny => Model is not null || HttpStatus is not null || ProviderRequestId is not null || Duration is not null || ServiceText is not null;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : Redaction.Scrub(text.Trim());
}

/// <summary>Reads the details of an answer's last failed request (D2 §2.1); nothing is stored for this.</summary>
public interface IFailureDetailsSource
{
    /// <summary>
    /// The newest failed request made for <paramref name="answer"/>; when none was recorded, what its task kept (the model
    /// and the time); null when not even a task is known.
    /// </summary>
    Task<FailureDetails?> ForAnswerAsync(MessageId answer, CancellationToken cancellationToken);

    /// <summary>As <see cref="ForAnswerAsync"/>, for a task that makes no answer (a summary).</summary>
    Task<FailureDetails?> ForTaskAsync(GenerationTaskId task, CancellationToken cancellationToken);
}
