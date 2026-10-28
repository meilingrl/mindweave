using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>A node centre in absolute canvas coordinates (architecture §11.2).</summary>
public readonly record struct NodePosition(double X, double Y);

/// <summary>Network persistence: node positions only; nodes and edges are projected from summaries and branch sources.</summary>
public interface INetworkStore
{
    Task<IReadOnlyDictionary<SummaryNodeId, NodePosition>> GetLayoutAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// Writes one node's position on its own (U3: a new summary's automatic place), only if that node still exists
    /// and has no saved position yet. Returns whether it was written.
    /// </summary>
    Task<bool> PlaceAsync(WorkspaceId workspace, SummaryNodeId node, NodePosition position, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a layout snapshot in one transaction (explicit save). Nodes deleted meanwhile are skipped, never
    /// written back. Returns the number of positions written.
    /// </summary>
    Task<int> SaveLayoutAsync(WorkspaceId workspace, IReadOnlyDictionary<SummaryNodeId, NodePosition> positions, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Every branch conversation of the workspace with its ancestors, root first.</summary>
    Task<IReadOnlyDictionary<ConversationId, IReadOnlyList<ConversationId>>> AncestorChainsAsync(WorkspaceId workspace, CancellationToken cancellationToken);
}
