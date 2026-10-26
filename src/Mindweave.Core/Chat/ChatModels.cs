namespace Mindweave.Core.Chat;

public enum MessageRole { User, Assistant }
public enum GenerationStatus { Queued, Running, Completed, Stopped, Failed, Interrupted }
public enum GenerationKind { Reply, Title }
public sealed record Conversation(Guid Id, Guid WorkspaceId, string Title, bool TitleIsManual, long TitleRevision, DateTimeOffset CreatedAt);
public sealed record ChatMessage(Guid Id, Guid ConversationId, long Sequence, MessageRole Role, string Body,
    GenerationStatus Status, long Revision, Guid? ReplyToUserId, Guid? SupersedesMessageId);
public sealed record AiConfiguration(Guid ConnectionId, Guid ModelConfigId, string Name, string BaseUrl,
    string CredentialReference, string ModelId, int ContextTokenBudget, int MaxOutputTokens, string TokenLimitParameter = "max_tokens");
public sealed record GenerationRecord(Guid Id, Guid ConversationId, GenerationKind Kind, Guid? AnswerMessageId,
    GenerationStatus Status, long Revision, AiConfiguration Configuration, IReadOnlyList<Guid> InputMessageIds,
    long? ExpectedTitleRevision, string? ErrorCode, string? ActualModelId);
public sealed record GenerationWork(GenerationRecord Task, IReadOnlyList<ChatMessage> InputMessages, string? SourceQuote = null, ReplyContextPlan? ContextPlan = null);
public sealed record ConversationDraft(string Text, long Revision);
