using Mindweave.Application.Ai;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Diagnostics;

/// <summary>Collects the facts of one AI request as its stream is consumed; not thread-safe (one consumer).</summary>
internal sealed class RequestTrace(GenerationTaskId task, IClock clock)
{
    private readonly DateTimeOffset _started = clock.UtcNow;
    private TimeSpan? _toResponse;
    private TimeSpan? _toFirstText;
    private int? _status;
    private string? _providerRequestId;
    private bool _truncated;
    private int _characters;
    private int? _promptTokens;
    private int? _completionTokens;

    public void Observe(ChatStreamEvent item)
    {
        switch (item)
        {
            case ChatStreamEvent.ResponseStarted started:
                _toResponse ??= clock.UtcNow - _started;
                _status = started.HttpStatus;
                _providerRequestId = started.ProviderRequestId;
                break;
            case ChatStreamEvent.TextDelta delta:
                _toFirstText ??= clock.UtcNow - _started;
                _characters += delta.Text.Length;
                break;
            case ChatStreamEvent.Completed completed:
                _truncated = completed.Truncated;
                break;
            case ChatStreamEvent.Usage usage:
                _promptTokens = usage.PromptTokens ?? _promptTokens;
                _completionTokens = usage.CompletionTokens ?? _completionTokens;
                break;
        }
    }

    public void Fail(ChatClientException ex) => _status ??= ex.HttpStatus;

    public AiRequestRecord Finish(bool stopped, GenerationErrorKind? error, string? detail) =>
        new(
            Guid.CreateVersion7(),
            task,
            _started,
            clock.UtcNow,
            _toResponse,
            _toFirstText,
            stopped ? AiRequestOutcome.Stopped
                : error is not null ? AiRequestOutcome.Failed
                : _truncated ? AiRequestOutcome.Truncated
                : AiRequestOutcome.Completed,
            _status,
            _providerRequestId,
            error,
            detail is null ? null : Redaction.Scrub(detail),
            _characters,
            _promptTokens,
            _completionTokens);
}
