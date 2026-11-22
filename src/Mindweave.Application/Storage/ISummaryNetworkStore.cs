using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>A node centre in absolute canvas coordinates (architecture §11.2).</summary>
public readonly record struct NodePosition(double X, double Y);

/// <summary>
/// Where an exploration sits in the spatial view of the session network (design session-network §16, option C): its root
/// dot in absolute canvas coordinates, the direction of its oldest branch from the root (radians), and how many
/// conversations it had, so a changed exploration can be told from an unchanged one. The molecule inside is laid out again
/// on every load.
/// </summary>
public sealed record ExplorationPlace(double X, double Y, double Angle, int Size);

/// <summary>Summary network persistence: node positions only; nodes and edges are projected from summaries and branch sources.</summary>
public interface ISummaryNetworkStore
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

    /// <summary>Each exploration's saved place in the session network's spatial view, by its root conversation.</summary>
    Task<IReadOnlyDictionary<ConversationId, ExplorationPlace>> GetExplorationPlacesAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// Saves explorations' places (new or changed ones) in one transaction, each only while its root conversation is still in
    /// the workspace. Returns the number written.
    /// </summary>
    Task<int> SaveExplorationPlacesAsync(WorkspaceId workspace, IReadOnlyDictionary<ConversationId, ExplorationPlace> places, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Every branch conversation of the workspace with its ancestors, root first.</summary>
    Task<IReadOnlyDictionary<ConversationId, IReadOnlyList<ConversationId>>> AncestorChainsAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>Number of questions (user messages) per conversation of the workspace; conversations without any are absent.</summary>
    Task<IReadOnlyDictionary<ConversationId, int>> QuestionCountsAsync(WorkspaceId workspace, CancellationToken cancellationToken);
}
