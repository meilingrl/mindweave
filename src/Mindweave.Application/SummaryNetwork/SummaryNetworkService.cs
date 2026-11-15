using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.SummaryNetwork;

/// <summary>A summary node as the network draws it: short label, full name for details, markers, where it came from.</summary>
/// <param name="Root">The root conversation of its exploration; groups the network even when the root has no summary.</param>
/// <param name="SourceArchived">The source conversation is hidden by an archive; the node and its place stay (D-021).</param>
/// <param name="Ancestors">
/// Every conversation from the exploration's root down to the source's parent, root first, summarized or not; empty
/// for a root. Lets the details spell out the whole source path even where no summarized ancestor exists.
/// </param>
public sealed record SummaryNetworkNode(
    SummaryNodeId Id,
    ConversationId Conversation,
    ConversationId Root,
    string ShortTitle,
    string DisplayName,
    SummaryMarkers Markers,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ConversationId> Ancestors,
    bool SourceArchived = false);

/// <summary>
/// A real exploration source between two summarized conversations (architecture §11). <paramref name="Via"/> lists
/// the conversations in between that have no summary, root side first; empty for a direct parent and child.
/// </summary>
public sealed record SummaryNetworkEdge(SummaryNodeId From, SummaryNodeId To, IReadOnlyList<ConversationId> Via)
{
    public bool IsDirect => Via.Count == 0;
}

public sealed record SummaryNetworkProjection(IReadOnlyList<SummaryNetworkNode> Nodes, IReadOnlyList<SummaryNetworkEdge> Edges);

/// <summary>
/// The workspace's network, derived from summaries and branch sources on every read (no second graph store), plus
/// the saved node positions. Never reads summary bodies.
/// </summary>
public sealed class SummaryNetworkService(ISummaryStore summaries, ISummaryNetworkStore network, IClock clock) : ISummaryNetworkLayout
{
    public async Task<SummaryNetworkProjection> ProjectAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        var entries = await summaries.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        var chains = await network.AncestorChainsAsync(workspace, cancellationToken).ConfigureAwait(false);
        var byConversation = entries.ToDictionary(e => e.Node.Conversation);
        var nodes = entries
            .OrderBy(e => e.Node.CreatedAt).ThenBy(e => e.Node.Id.Value)
            .Select(e => new SummaryNetworkNode(
                e.Node.Id,
                e.Node.Conversation,
                chains.TryGetValue(e.Node.Conversation, out var chain) && chain.Count > 0 ? chain[0] : e.Node.Conversation,
                SummaryRules.ShortTitleOf(e.Current.Title, e.Current.ShortTitle),
                e.Node.DisplayName,
                e.Markers,
                e.Node.CreatedAt,
                chains.TryGetValue(e.Node.Conversation, out var ancestors) ? ancestors : [],
                e.SourceArchived))
            .ToList();

        // Each node links to its nearest summarized ancestor; unsummarized conversations in between are kept on the
        // edge, never collapsed into a pretended direct parent.
        var edges = new List<SummaryNetworkEdge>();
        foreach (var node in nodes)
        {
            if (!chains.TryGetValue(node.Conversation, out var chain))
            {
                continue;
            }

            var via = new List<ConversationId>();
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (byConversation.TryGetValue(chain[i], out var ancestor))
                {
                    via.Reverse();
                    edges.Add(new SummaryNetworkEdge(ancestor.Node.Id, node.Id, via));
                    break;
                }

                via.Add(chain[i]);
            }
        }

        return new SummaryNetworkProjection(nodes, edges);
    }

    public Task<IReadOnlyDictionary<SummaryNodeId, NodePosition>> GetLayoutAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        network.GetLayoutAsync(workspace, cancellationToken);

    /// <summary>Saves a new node's automatic position by itself (U3); false when not written (node gone or already placed).</summary>
    public Task<bool> PlaceAsync(WorkspaceId workspace, SummaryNodeId node, NodePosition position, CancellationToken cancellationToken) =>
        network.PlaceAsync(workspace, node, position, clock.UtcNow, cancellationToken);

    public Task<int> SaveLayoutAsync(WorkspaceId workspace, IReadOnlyDictionary<SummaryNodeId, NodePosition> positions, CancellationToken cancellationToken) =>
        network.SaveLayoutAsync(workspace, positions, clock.UtcNow, cancellationToken);
}
