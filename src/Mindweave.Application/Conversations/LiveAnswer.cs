using System.Text;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

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
            return new LiveAnswerSnapshot(_text.ToString(), _revision, _state, _errorKind, _errorDetail);
        }
    }

    internal void MarkStreaming() => Update(() => _state = MessageState.Streaming);

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

public sealed record LiveAnswerSnapshot(string Text, long Revision, MessageState State, GenerationErrorKind? ErrorKind, string? ErrorDetail)
{
    /// <summary>The content as stored. A streamed answer is one text block until generation produces other kinds.</summary>
    public IReadOnlyList<ContentBlock> Blocks => ContentBlocks.FromText(Text);
}
