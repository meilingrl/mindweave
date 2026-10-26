using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

public sealed record PreparedReplyRequest(Guid ReplyTaskId, ChatRequest Request,
    IReadOnlyList<PreparedContextUse> Uses, DateTimeOffset PreparedAt);

// Display-only metadata: a prepared manifest is not evidence that transport started.
public sealed record ContextUsageMetadata(Guid AnswerMessageId, int SummaryCount, int PlannedSummaryCount, string? ErrorCode);

public interface IContextStore
{
    Task<IReadOnlyList<ContextUsageMetadata>> LoadContextUsageAsync(Guid conversationId, IReadOnlyList<Guid> answerMessageIds);
    Task<ReplyContextPlan?> LoadContextPlanAsync(Guid replyTaskId);
    Task<CompletedContextSummary?> LoadCompletedSummaryAsync(Guid replyTaskId, string cacheKey);
    Task<ContextCompressionAttempt> StartCompressionAsync(Guid replyTaskId, string cacheKey);
    Task<ContextCompressionAttempt?> LoadCompressionAttemptAsync(Guid attemptId);
    Task<ContextCompressionAttempt?> LoadCompressionAttemptForReplyAsync(Guid replyTaskId, string cacheKey);
    Task<bool> WriteCompressionAsync(Guid replyTaskId, Guid attemptId, long revision, string payload, GenerationStatus status,
        string? finishReason = null, string? errorCode = null, string? actualModelId = null);
    Task<PreparedReplyRequest> PrepareReplyRequestAsync(Guid replyTaskId);
    Task<PreparedReplyRequest?> LoadPreparedReplyRequestAsync(Guid replyTaskId);
}
