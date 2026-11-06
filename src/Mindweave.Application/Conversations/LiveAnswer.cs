using System.Text;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

/// <summary>What a running answer is doing before its text arrives.</summary>
public enum AnswerPhase
{
    /// <summary>Waiting for the service (queued, connecting, or its first text not yet arrived).</summary>
    Waiting,

    /// <summary>Summarizing older context so the request fits (S3).</summary>
    Compressing,

    /// <summary>A reasoning model sent reasoning before any answer text; the reasoning itself is not kept (2026-11-06).</summary>
    Thinking,
}

/// <summary>
/// The in-memory, always-newest view of one answer while the application owns it. View models read it
/// instead of the database, so a page that is closed and reopened mid-stream never shows older text than
/// what has arrived. <see cref="Changed"/> is raised on worker threads.
/// </summary>
public sealed class LiveAnswer
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();
    private MessageState _state;
    private AnswerPhase _phase;
    private long _revision;
    private GenerationErrorKind? _errorKind;
    private string? _errorDetail;

    public LiveAnswer(MessageId messageId, ConversationId conversationId, string initialText = "", long initialRevision = 0)
    {
        MessageId = messageId;
        ConversationId = conversationId;
        _text.Append(initialText);
        _revision = initialRevision;
        _state = MessageState.Pending;
    }

    public event EventHandler? Changed;

    public MessageId MessageId { get; }

    public ConversationId ConversationId { get; }

    public LiveAnswerSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new LiveAnswerSnapshot(_text.ToString(), _revision, _state, _errorKind, _errorDetail, _phase);
        }
    }

    internal void MarkStreaming() => Update(() => _state = MessageState.Streaming);

    internal void SetPhase(AnswerPhase phase) => Update(() => _phase = phase);

    /// <summary>
    /// The service is reasoning. Moves <see cref="AnswerPhase.Waiting"/> to <see cref="AnswerPhase.Thinking"/> while the answer
    /// is running with no text; any other phase is kept. Raises <see cref="Changed"/> only when the phase changes, as reasoning
    /// arrives in hundreds of small chunks.
    /// </summary>
    internal void MarkThinking()
    {
        lock (_gate)
        {
            if (_phase != AnswerPhase.Waiting || _text.Length > 0 || _state is not (MessageState.Pending or MessageState.Streaming))
            {
                return;
            }

            _phase = AnswerPhase.Thinking;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Append(string delta)
    {
        if (delta.Length == 0)
        {
            return;
        }

        Update(() =>
        {
            if (_state is MessageState.Pending or MessageState.Streaming)
            {
                _text.Append(delta);
                _revision++;
            }
        });
    }

    /// <summary>Terminal: later deltas are ignored. Returns the final revision to persist.</summary>
    internal long Finish(MessageState state, GenerationErrorKind? errorKind, string? errorDetail)
    {
        long revision = 0;
        Update(() =>
        {
            _state = state;
            _errorKind = errorKind;
            _errorDetail = errorDetail;
            revision = ++_revision;
        });
        return revision;
    }

    private void Update(Action change)
    {
        lock (_gate)
        {
            change();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record LiveAnswerSnapshot(string Text, long Revision, MessageState State, GenerationErrorKind? ErrorKind, string? ErrorDetail, AnswerPhase Phase = AnswerPhase.Waiting)
{
    /// <summary>The content as stored. A streamed answer is one text block until generation produces other kinds.</summary>
    public IReadOnlyList<ContentBlock> Blocks => ContentBlocks.FromText(Text);
}
