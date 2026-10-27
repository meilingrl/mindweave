using System.Collections.Immutable;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

public sealed record PreparedKnowledgeSummaryRequest(Guid TaskId, int FormatVersion, string RequestHash, ChatRequest Request);

/// <summary>Single-writer transactional authority. Expected business conflicts return outcomes; IO/SQL failures throw and enter the shared persistence gate.</summary>
public interface IKnowledgeSummaryStore
{
    // Idempotency binds operation ID to exact conversation/configuration. Reject active Reply (rows AND jobs), active summary, candidate, empty/over-budget input before writes.
    Task<SummaryOutcome<KnowledgeSummaryJob>> AcceptKnowledgeSummaryAsync(Guid operationId, Guid conversationId, AiConfiguration configuration);
    Task<KnowledgeSummaryJob?> LoadKnowledgeSummaryJobAsync(Guid taskId);
    Task<KnowledgeSummaryJob?> FindKnowledgeSummaryOperationAsync(Guid operationId);
    Task<ImmutableArray<KnowledgeSummaryJob>> ListActiveKnowledgeSummaryJobsAsync();
    // Persist only format, references and canonical hash. Reconstruct/validate original bodies and quote bindings, never messages_json.
    Task<SummaryOutcome<PreparedKnowledgeSummaryRequest>> PrepareKnowledgeSummaryRequestAsync(Guid taskId, long expectedRevision);
    Task<PreparedKnowledgeSummaryRequest?> LoadPreparedKnowledgeSummaryRequestAsync(Guid taskId);
    // Metadata only; Completed is forbidden here. No partial Title/Markdown storage.
    Task<SummaryOutcome<KnowledgeSummaryJob>> UpdateKnowledgeSummaryMetadataAsync(SummaryGenerationMetadata update);
    // Atomic first node/current unread or later candidate unread + metadata. Repeated success returns original ID even if output content was later deleted.
    Task<SummaryOutcome<SummaryGenerationResult>> CompleteKnowledgeSummaryAsync(SummaryGenerationCompletion completion);
    // One transaction; startup never sends requests. Shared recovery should include all job families before releasing the application gate.
    Task RecoverInterruptedKnowledgeSummariesAsync();
}
