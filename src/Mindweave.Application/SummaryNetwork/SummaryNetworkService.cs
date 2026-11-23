using Mindweave.Application.Conversations;
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
    bool SourceArchived = false,
    bool HasSummary = true,
    int Weight = 1,
    string? FirstQuestion = null);

public enum SummaryNetworkEdgeKind
{
    /// <summary>A branch from its parent conversation.</summary>
    Branch,

    /// <summary>Consecutive explorations in time (between roots).</summary>
    TimeThread,
}

/// <summary>
/// A real exploration source between two summarized conversations (architecture §11). <paramref name="Via"/> lists
/// the conversations in between that have no summary, root side first; empty for a direct parent and child.
/// </summary>
public sealed record SummaryNetworkEdge(
    SummaryNodeId From,
    SummaryNodeId To,
    IReadOnlyList<ConversationId> Via,
    SummaryNetworkEdgeKind Kind = SummaryNetworkEdgeKind.Branch)
{
    public bool IsDirect => Via.Count == 0;
}

public sealed record SummaryNetworkProjection(IReadOnlyList<SummaryNetworkNode> Nodes, IReadOnlyList<SummaryNetworkEdge> Edges);

/// <summary>
/// The workspace's network, derived from summaries and branch sources on every read (no second graph store), plus
/// the saved node positions. Never reads summary bodies.
/// </summary>
public sealed class SummaryNetworkService(ISummaryStore summaries, ISummaryNetworkStore network, IConversationStore conversations, IClock clock) : ISummaryNetworkLayout
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

    /// <summary>
    /// Every conversation as a node (a summary is an attribute of its node, not a node of its own), branch edges from
    /// parent to child and a time thread through the roots in creation order.
    /// </summary>
    public async Task<SummaryNetworkProjection> ProjectSessionsAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        var listed = await conversations.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        var entries = await summaries.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        var chains = await network.AncestorChainsAsync(workspace, cancellationToken).ConfigureAwait(false);
        var questions = await network.QuestionCountsAsync(workspace, cancellationToken).ConfigureAwait(false);
        var summarized = entries.ToDictionary(e => e.Node.Conversation);
        var weights = SessionWeights.Of(listed.ToDictionary(c => c.Conversation.Id, c => c.Parent), questions);

        var nodes = new List<SummaryNetworkNode>(listed.Count);
        foreach (var item in listed.OrderBy(c => c.Conversation.CreatedAt).ThenBy(c => c.Conversation.Id.Value))
        {
            var id = item.Conversation.Id;
            var ancestors = chains.TryGetValue(id, out var chain) ? chain : [];
            var root = ancestors.Count > 0 ? ancestors[0] : id;
            var archived = item.IsArchived || item.IsHidden;
            if (summarized.TryGetValue(id, out var entry))
            {
                nodes.Add(new SummaryNetworkNode(
                    entry.Node.Id, id, root, SummaryRules.ShortTitleOf(entry.Current.Title, entry.Current.ShortTitle),
                    entry.Node.DisplayName, entry.Markers, item.Conversation.CreatedAt, ancestors, archived,
                    true, weights[id], item.FirstQuestion));
                continue;
            }

            // Named as the sidebar names the conversation (title, quote, first question, 新会话): the whole title, never
            // cut, since the timeline sizes each column to its widest label; the rest on one line and cut. The name is also
            // the display name, so the list and links never show a whole question. It is read in the language of the
            // moment; a language switch rebuilds the page, which projects again.
            var shortTitle = ConversationNames.Of(item.Conversation.Title, item.Quote, item.FirstQuestion);
            nodes.Add(new SummaryNetworkNode(
                new SummaryNodeId(id.Value), id, root, shortTitle, shortTitle,
                new SummaryMarkers(false, false, false), item.Conversation.CreatedAt, ancestors, archived,
                false, weights[id], item.FirstQuestion));
        }

        var edges = new List<SummaryNetworkEdge>();
        var idOf = nodes.ToDictionary(n => n.Conversation, n => n.Id);
        var parentOf = listed.ToDictionary(c => c.Conversation.Id, c => c.Parent);
        foreach (var node in nodes)
        {
            if (parentOf[node.Conversation] is { } parent && idOf.TryGetValue(parent, out var from))
            {
                edges.Add(new SummaryNetworkEdge(from, node.Id, []));
            }
        }

        var roots = nodes.Where(n => n.Root == n.Conversation).ToList();
        for (var i = 1; i < roots.Count; i++)
        {
            edges.Add(new SummaryNetworkEdge(roots[i - 1].Id, roots[i].Id, [], SummaryNetworkEdgeKind.TimeThread));
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

    public Task<IReadOnlyDictionary<ConversationId, ExplorationPlace>> GetExplorationPlacesAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        network.GetExplorationPlacesAsync(workspace, cancellationToken);

    public Task<int> SaveExplorationPlacesAsync(WorkspaceId workspace, IReadOnlyDictionary<ConversationId, ExplorationPlace> places, CancellationToken cancellationToken) =>
        network.SaveExplorationPlacesAsync(workspace, places, clock.UtcNow, cancellationToken);
}
