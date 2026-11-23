using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.SummaryNetwork;

/// <summary>
/// A conversation as the network draws it: short label, full name for details, its summary's markers (if it has one),
/// where it came from and how much was asked.
/// </summary>
/// <param name="Id">The summary node's id; for a conversation without a summary, an id made from the conversation's.</param>
/// <param name="Root">The root conversation of its exploration.</param>
/// <param name="SourceArchived">The conversation is hidden by an archive; its dot and place stay (D-021).</param>
/// <param name="Ancestors">Every conversation from the exploration's root down to the parent, root first; empty for a root.</param>
/// <param name="Weight">How much was asked in the conversation and its branches (<see cref="ConversationWeights"/>).</param>
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

/// <summary>A line of the network: real lineage, or the time thread between explorations (architecture §11).</summary>
public sealed record SummaryNetworkEdge(SummaryNodeId From, SummaryNodeId To, SummaryNetworkEdgeKind Kind = SummaryNetworkEdgeKind.Branch);

public sealed record SummaryNetworkProjection(IReadOnlyList<SummaryNetworkNode> Nodes, IReadOnlyList<SummaryNetworkEdge> Edges);

/// <summary>
/// The workspace's network, derived from conversations, branch sources and summaries on every read (no second graph
/// store), plus the explorations' saved places. Never reads summary bodies.
/// </summary>
public sealed class SummaryNetworkService(ISummaryStore summaries, ISummaryNetworkStore network, IConversationStore conversations, IClock clock) : ISummaryNetworkLayout
{
    /// <summary>
    /// Every conversation as a node (a summary is an attribute of its node, not a node of its own), branch edges from
    /// parent to child and a time thread through the roots in creation order.
    /// </summary>
    public async Task<SummaryNetworkProjection> ProjectAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        var listed = await conversations.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        var entries = await summaries.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        var chains = await network.AncestorChainsAsync(workspace, cancellationToken).ConfigureAwait(false);
        var questions = await network.QuestionCountsAsync(workspace, cancellationToken).ConfigureAwait(false);
        var summarized = entries.ToDictionary(e => e.Node.Conversation);
        var weights = ConversationWeights.Of(listed.ToDictionary(c => c.Conversation.Id, c => c.Parent), questions);

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
                edges.Add(new SummaryNetworkEdge(from, node.Id));
            }
        }

        var roots = nodes.Where(n => n.Root == n.Conversation).ToList();
        for (var i = 1; i < roots.Count; i++)
        {
            edges.Add(new SummaryNetworkEdge(roots[i - 1].Id, roots[i].Id, SummaryNetworkEdgeKind.TimeThread));
        }

        return new SummaryNetworkProjection(nodes, edges);
    }

    public Task<IReadOnlyDictionary<ConversationId, ExplorationPlace>> GetExplorationPlacesAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        network.GetExplorationPlacesAsync(workspace, cancellationToken);

    public Task<int> SaveExplorationPlacesAsync(WorkspaceId workspace, IReadOnlyDictionary<ConversationId, ExplorationPlace> places, CancellationToken cancellationToken) =>
        network.SaveExplorationPlacesAsync(workspace, places, clock.UtcNow, cancellationToken);
}
