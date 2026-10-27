using System.Collections.Immutable;
using Mindweave.Core.Chat;

namespace Mindweave.Core.Summaries;

// Hashes reference full content; no summary bodies or generated output copies in durable naming jobs.
public sealed record NodeNamingMember(Guid NodeId, Guid CurrentVersionId, long NameRevision, bool NameIsManual,
    string ContentHash);
public sealed record NodeNamingSnapshot(Guid WorkspaceId, string GroupKey, long GroupEpoch, ImmutableArray<NodeNamingMember> Members, string SnapshotHash);
public sealed record NodeNamingJob(Guid Id, Guid OperationId, NodeNamingSnapshot Snapshot, AiConfiguration Configuration,
    GenerationStatus Status, long Revision, DateTimeOffset AcceptedAt, string? ErrorCode);
public sealed record NodeNamingMaterial(NodeNamingMember Member, string BaseTitle, string DisplayName, SummaryContent Content);
public sealed record NodeNameProposal(Guid NodeId, string DisplayName);
public sealed record NodeNamingCompletion(Guid TaskId, long ExpectedRevision, string ExpectedSnapshotHash, ImmutableArray<NodeNameProposal> Names);
public sealed record NodeNamingResult(Guid TaskId, ImmutableArray<Guid> ChangedNodeIds);
