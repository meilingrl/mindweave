using Mindweave.Application.Ai;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Diagnostics;

/// <summary>Collects the facts of one AI request as its stream is consumed; not thread-safe (one consumer).</summary>
internal sealed class RequestTrace
{
    private readonly GenerationTaskId? _task;
    private readonly Conversations.RequestAttribution? _attribution;
    private readonly IClock _clock;
    private readonly ChatRequest _request;
    private readonly int _estimate;
    private readonly DateTimeOffset _started;
    private TimeSpan? _toResponse;
    private TimeSpan? _toFirstText;
    private int? _status;
    private string? _providerRequestId;
    private bool _truncated;
    private int _characters;
    private int? _promptTokens;
    private int? _completionTokens;
    private int? _cacheReadTokens;
    private int? _cacheWriteTokens;

    /// <summary>A request made for a task (its model is the task's snapshot).</summary>
    public RequestTrace(GenerationTaskId task, IClock clock, ChatRequest request, string? purpose = null)
        : this(clock, request)
    {
        _task = task;
        _purpose = purpose;
    }

    private readonly string? _purpose;

    /// <summary>A request without a task row, counted for a workspace and a kind (2026-11-11); its model is recorded with it.</summary>
    public RequestTrace(Conversations.RequestAttribution attribution, IClock clock, ChatRequest request)
        : this(clock, request)
    {
        _attribution = attribution;
    }

    private RequestTrace(IClock clock, ChatRequest request)
    {
        _clock = clock;
        _request = request;
        _estimate = ChatTurns.Estimate(request);
        _started = clock.UtcNow;
    }

    public void Observe(ChatStreamEvent item)
    {
        switch (item)
        {
            case ChatStreamEvent.ResponseStarted started:
                _toResponse ??= _clock.UtcNow - _started;
                _status = started.HttpStatus;
                _providerRequestId = started.ProviderRequestId;
                break;
            case ChatStreamEvent.TextDelta delta:
                _toFirstText ??= _clock.UtcNow - _started;
                _characters += delta.Text.Length;
                break;
            case ChatStreamEvent.ReasoningDelta:
                // Not answer text: time to first text and the character count stay about what the reader sees.
                break;
            case ChatStreamEvent.Completed completed:
                _truncated = completed.Truncated;
                break;
            case ChatStreamEvent.Usage usage:
                _promptTokens = usage.PromptTokens ?? _promptTokens;
                _completionTokens = usage.CompletionTokens ?? _completionTokens;
                _cacheReadTokens = usage.CacheReadTokens ?? _cacheReadTokens;
                _cacheWriteTokens = usage.CacheWriteTokens ?? _cacheWriteTokens;
                break;
        }
    }

    public void Fail(ChatClientException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        _status ??= ex.HttpStatus;
        _providerRequestId ??= ex.ProviderRequestId;
    }

    public AiRequestRecord Finish(bool stopped, GenerationErrorKind? error, string? detail) =>
        new(
            Guid.CreateVersion7(),
            _task,
            _started,
            _clock.UtcNow,
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
            _completionTokens,
            _estimate,
            _attribution,
            _attribution is null ? null : _request.Model.Model,
            _attribution is null ? null : _request.Model.BaseUrl,
            _purpose,
            _cacheReadTokens,
            _cacheWriteTokens);
}
