namespace Mindweave.Core.Conversations;

public enum MessageRole
{
    User,
    Assistant,
}

/// <summary>
/// Lifecycle of a message. User messages are always <see cref="Complete"/>. An answer moves
/// Pending → Streaming → one terminal state; terminal bodies never change again.
/// </summary>
public enum MessageState
{
    Complete,
    Pending,
    Streaming,
    Stopped,
    Failed,
    Interrupted,
    Cancelled,
}

public enum GenerationErrorKind
{
    Unauthorized,
    QuotaOrRateLimited,
    ContextTooLong,
    ServerError,
    Network,
    Timeout,
    InvalidResponse,
    CredentialUnavailable,
    NotConfigured,
    StorageUnavailable,

    /// <summary>Older context had to be summarized to fit and summarizing failed; the question is kept for retry.</summary>
    CompressionFailed,

    /// <summary>The service stopped the answer at its output limit (health check 2026-11-12); what came is kept, marked incomplete.</summary>
    OutputLimit,

    /// <summary>The service's content filter stopped the answer; what came is kept, marked incomplete.</summary>
    ContentFiltered,
}

public sealed record Message(
    MessageId Id,
    ConversationId ConversationId,
    int Seq,
    MessageRole Role,
    IReadOnlyList<ContentBlock> Blocks,
    MessageState State,
    long Revision,
    MessageId? Supersedes,
    GenerationErrorKind? ErrorKind,
    string? ErrorDetail,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>The text blocks as one Markdown string (see <see cref="ContentBlocks.TextOf"/>).</summary>
    public string Text => ContentBlocks.TextOf(Blocks);

    public bool IsFinished => State is not (MessageState.Pending or MessageState.Streaming);

    /// <summary>Usable as conversation context: a finished answer with text, or any user message.</summary>
    public bool CountsAsContext =>
        Role == MessageRole.User
        || (State is MessageState.Complete or MessageState.Stopped && !string.IsNullOrWhiteSpace(Text));
}
