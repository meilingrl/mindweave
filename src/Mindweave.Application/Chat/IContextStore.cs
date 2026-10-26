using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

public sealed record PreparedReplyRequest(Guid ReplyTaskId, ChatRequest Request,
    IReadOnlyList<PreparedContextUse> Uses, DateTimeOffset PreparedAt);

public interface IContextStore
{
    Task<ReplyContextPlan?> LoadContextPlanAsync(Guid replyTaskId);
    Task<CompletedContextSummary?> LoadCompletedSummaryAsync(Guid replyTaskId, string cacheKey);
    Task<ContextCompressionAttempt> StartCompressionAsync(Guid replyTaskId, string cacheKey);
    Task<ContextCompressionAttempt?> LoadCompressionAttemptAsync(Guid attemptId);
    Task<bool> WriteCompressionAsync(Guid attemptId, long revision, string payload, GenerationStatus status,
        string? finishReason = null, string? errorCode = null, string? actualModelId = null);
    Task<PreparedReplyRequest> PrepareReplyRequestAsync(Guid replyTaskId);
    Task<PreparedReplyRequest?> LoadPreparedReplyRequestAsync(Guid replyTaskId);
}
