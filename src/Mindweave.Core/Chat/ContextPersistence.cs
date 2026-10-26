namespace Mindweave.Core.Chat;

public sealed record ContextCompressionAttempt(Guid Id, Guid SourceConversationId, ContextChunk Chunk,
    AiConfiguration Configuration, GenerationStatus Status, long Revision, string Payload, string? FinishReason,
    string? ErrorCode, string? ActualModelId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CompletedContextSummary(Guid Id, Guid SourceConversationId, Guid AttemptId, ContextChunk Chunk,
    AiConfiguration Configuration, ContextSummaryContent Content, string PayloadHash, string? ActualModelId, DateTimeOffset CompletedAt);
public enum ContextUseKind { Verbatim, Summary, SourceQuote }
public sealed record PreparedContextUse(int InputIndex, ContextUseKind Kind, Guid? MessageId, Guid? SummaryId);
