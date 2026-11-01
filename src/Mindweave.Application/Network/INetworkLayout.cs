using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Network;

/// <summary>The network projection and its saved layout, as a workspace's network session uses them (course requirement C3).</summary>
public interface INetworkLayout
{
    Task<NetworkProjection> ProjectAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<SummaryNodeId, NodePosition>> GetLayoutAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    Task<bool> PlaceAsync(WorkspaceId workspace, SummaryNodeId node, NodePosition position, CancellationToken cancellationToken);

    Task<int> SaveLayoutAsync(WorkspaceId workspace, IReadOnlyDictionary<SummaryNodeId, NodePosition> positions, CancellationToken cancellationToken);
}
