using System.Collections.Immutable;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

public interface INodeNamingStore
{
    Task<NodeNamingSnapshot> ReadNodeNamingGroupAsync(Guid workspaceId, string normalizedGroupKey);
    // Verifies full membership/current version/name revision/manual flags/group epoch and snapshot hash, with one active job per group.
    Task<SummaryOutcome<NodeNamingJob>> AcceptNodeNamingAsync(Guid operationId, NodeNamingSnapshot expected, AiConfiguration configuration);
    Task<NodeNamingJob?> LoadNodeNamingJobAsync(Guid taskId);
    Task<NodeNamingJob?> FindNodeNamingOperationAsync(Guid operationId);
    Task<ImmutableArray<NodeNamingJob>> ListActiveNodeNamingJobsAsync();
    // Read-only exact current live versions/hash; no copied bodies retained in the job.
    Task<SummaryOutcome<ImmutableArray<NodeNamingMaterial>>> LoadNodeNamingMaterialsAsync(Guid taskId);
    Task<SummaryOutcome<NodeNamingJob>> UpdateNodeNamingMetadataAsync(SummaryGenerationMetadata update);
    // All-member CAS or zero writes; protect manual names, reject deleted nodes, never upsert. Output is not a version.
    Task<SummaryOutcome<NodeNamingResult>> CompleteNodeNamingAsync(NodeNamingCompletion completion);
    Task RecoverInterruptedNodeNamingAsync();
}
