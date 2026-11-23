using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>A node centre in absolute canvas coordinates (architecture §11.2).</summary>
public readonly record struct NodePosition(double X, double Y);

/// <summary>
/// Where an exploration sits in the spatial view (design session-network §16, option C): its root
/// dot in absolute canvas coordinates, the direction of its oldest branch from the root (radians), and how many
/// conversations it had, so a changed exploration can be told from an unchanged one. The molecule inside is laid out again
/// on every load.
/// </summary>
public sealed record ExplorationPlace(double X, double Y, double Angle, int Size);

/// <summary>Summary network persistence: the explorations' places only; dots and lines are projected from conversations, branch sources and summaries.</summary>
public interface ISummaryNetworkStore
{
    /// <summary>Each exploration's saved place in the spatial view, by its root conversation.</summary>
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
