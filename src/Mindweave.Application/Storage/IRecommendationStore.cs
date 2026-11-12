using Mindweave.Application.Recommendations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>
/// One summary as recommendations may draw on it: its shown name, its current version's title and the start of that
/// version's body (cut in storage, so a long body is never read whole), and when it was made.
/// </summary>
public sealed record RecommendationBasis(SummaryNodeId Node, string DisplayName, string Title, string BodyStart, DateTimeOffset CreatedAt);

/// <summary>
/// The summaries recommendations may draw on: the <paramref name="Listed"/> most recently updated (all of them when there
/// are few), how many the workspace has, and when the newest was made (null without any).
/// </summary>
public sealed record RecommendationBases(IReadOnlyList<RecommendationBasis> Listed, int Count, DateTimeOffset? NewestCreatedAt);

/// <summary>
/// Recommendations kept per workspace (requirements §10.3, migration v11). Every method is one transaction. Rows of a
/// workspace go with it, and rows drawing on a summary go with that summary (cascades), so nothing here deletes them.
/// </summary>
public interface IRecommendationStore
{
    /// <summary>Every row of the kind in the workspace, pending ones first (oldest batch first, in batch order), then the rest, newest first.</summary>
    Task<IReadOnlyList<Recommendation>> ListAsync(WorkspaceId workspace, RecommendationKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a batch made by one request. With <paramref name="replacePending"/> the kind's pending rows go first (换一批).
    /// Nothing is stored when the workspace no longer exists (a late result); an item whose basis summary no longer
    /// exists is left out. Returns how many were stored, or null when the workspace was gone.
    /// </summary>
    Task<int?> AddBatchAsync(WorkspaceId workspace, RecommendationKind kind, RecommendationBatch batch, bool replacePending, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes these pending rows of the kind in the workspace without remembering them (a network whose summaries fell
    /// below a tier shows fewer nodes, §10.3.2); used and ignored rows are left alone. Returns how many went.
    /// </summary>
    Task<int> RemovePendingAsync(WorkspaceId workspace, RecommendationKind kind, IReadOnlyCollection<RecommendationId> ids, CancellationToken cancellationToken);

    /// <summary>
    /// A pending row becomes used or ignored; an ignored one keeps only <paramref name="normalized"/> (its question in the
    /// form duplicates are matched in) and no reason. False when it was not pending (or is gone).
    /// </summary>
    Task<bool> SetStateAsync(RecommendationId id, RecommendationState state, string normalized, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// The workspace's summaries for a request: at most <paramref name="limit"/>, most recently updated first, each body
    /// cut to its first <paramref name="bodyChars"/> characters.
    /// </summary>
    Task<RecommendationBases> ListBasesAsync(WorkspaceId workspace, int limit, int bodyChars, CancellationToken cancellationToken);
}
