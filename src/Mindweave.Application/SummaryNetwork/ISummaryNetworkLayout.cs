using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.SummaryNetwork;

/// <summary>The network projection and the explorations' saved places, as a workspace's network session uses them (course requirement C3).</summary>
public interface ISummaryNetworkLayout
{
    /// <summary>Every conversation of the workspace as a dot, its lineage and the time thread (architecture §11).</summary>
    Task<SummaryNetworkProjection> ProjectAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>Each exploration's saved place in the spatial view, by its root conversation (architecture §11.2).</summary>
    Task<IReadOnlyDictionary<ConversationId, ExplorationPlace>> GetExplorationPlacesAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>Saves new or changed exploration places directly, without a pending-save state (U3). Returns the number written.</summary>
    Task<int> SaveExplorationPlacesAsync(WorkspaceId workspace, IReadOnlyDictionary<ConversationId, ExplorationPlace> places, CancellationToken cancellationToken);
}
