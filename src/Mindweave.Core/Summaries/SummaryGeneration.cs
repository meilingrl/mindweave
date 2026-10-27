using Mindweave.Core.Chat;

namespace Mindweave.Core.Summaries;

public sealed record SummaryGenerationBaseline(Guid NodeId, Guid? CurrentVersionId, long NodeRevision, bool CreatesNode);
public sealed record KnowledgeSummaryJob(Guid Id, Guid OperationId, Guid WorkspaceId, Guid ConversationId,
    SummaryGenerationBaseline Baseline, GenerationStatus Status, long Revision, KnowledgeSummaryInputPlan InputPlan,
    DateTimeOffset AcceptedAt, string? ErrorCode, string? ActualModelId, Guid? OutputVersionId);
public sealed record SummaryGenerationMetadata(Guid TaskId, long ExpectedRevision, GenerationStatus Status, string? ErrorCode = null, string? ActualModelId = null);
public sealed record SummaryGenerationCompletion(Guid TaskId, long ExpectedRevision, SummaryContent Content, string? ActualModelId);
public sealed record SummaryGenerationResult(Guid TaskId, Guid NodeId, Guid VersionId, bool IsCandidate);
