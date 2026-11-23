using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.SummaryNetwork;

/// <summary>The network projection and its saved layout, as a workspace's network session uses them (course requirement C3).</summary>
public interface ISummaryNetworkLayout
{
    Task<SummaryNetworkProjection> ProjectAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    Task<SummaryNetworkProjection> ProjectSessionsAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<SummaryNodeId, NodePosition>> GetLayoutAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    Task<bool> PlaceAsync(WorkspaceId workspace, SummaryNodeId node, NodePosition position, CancellationToken cancellationToken);

    Task<int> SaveLayoutAsync(WorkspaceId workspace, IReadOnlyDictionary<SummaryNodeId, NodePosition> positions, CancellationToken cancellationToken);

    /// <summary>Each exploration's saved place in the session network's spatial view (design session-network §16); none by default.</summary>
    Task<IReadOnlyDictionary<ConversationId, ExplorationPlace>> GetExplorationPlacesAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<ConversationId, ExplorationPlace>>(new Dictionary<ConversationId, ExplorationPlace>());

    /// <summary>Saves new or changed exploration places directly (like a new node's place, U3); nothing by default.</summary>
    Task<int> SaveExplorationPlacesAsync(WorkspaceId workspace, IReadOnlyDictionary<ConversationId, ExplorationPlace> places, CancellationToken cancellationToken) =>
        Task.FromResult(0);
}
