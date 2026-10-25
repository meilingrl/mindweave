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
}

public sealed record Message(
    MessageId Id,
    ConversationId ConversationId,
    int Seq,
    MessageRole Role,
    string Body,
    MessageState State,
    long Revision,
    MessageId? Supersedes,
    GenerationErrorKind? ErrorKind,
    string? ErrorDetail,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public bool IsFinished => State is not (MessageState.Pending or MessageState.Streaming);

    /// <summary>Usable as conversation context: a finished answer with text, or any user message.</summary>
    public bool CountsAsContext =>
        Role == MessageRole.User
        || (State is MessageState.Complete or MessageState.Stopped && !string.IsNullOrWhiteSpace(Body));
}
