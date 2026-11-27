using Mindweave.Core.Conversations;

namespace Mindweave.Application.Share;

/// <summary>
/// Which conversations of a workspace a share holds (docs/design/sharing.md S1). A ticked conversation always has its
/// ancestors ticked, so a reader sees where each branch left from. Immutable.
/// </summary>
public sealed class ShareSelection
{
    private readonly IReadOnlyDictionary<ConversationId, ConversationId?> _parents;
    private readonly ILookup<ConversationId, ConversationId> _branches;

    private ShareSelection(IReadOnlyDictionary<ConversationId, ConversationId?> parents, ILookup<ConversationId, ConversationId> branches, IReadOnlySet<ConversationId> ticked)
    {
        _parents = parents;
        _branches = branches;
        Ticked = ticked;
    }

    public IReadOnlySet<ConversationId> Ticked { get; }

    /// <summary>
    /// The exploration <paramref name="opened"/> belongs to, from its root down; empty when it is not in the workspace.
    /// </summary>
    /// <param name="parents">Every conversation of the workspace with its parent (null for a root).</param>
    /// <param name="opened">The conversation the share was opened from.</param>
    public static ShareSelection Exploration(IReadOnlyDictionary<ConversationId, ConversationId?> parents, ConversationId opened)
    {
        ArgumentNullException.ThrowIfNull(parents);
        var none = Of(parents, new HashSet<ConversationId>());
        return parents.ContainsKey(opened) ? none.With(none.WithBranches(none.Ancestors(opened).LastOrDefault(opened))) : none;
    }

    /// <summary>Every conversation of the workspace: what a manual backup's readable page is drawn from.</summary>
    public static ShareSelection All(IReadOnlyDictionary<ConversationId, ConversationId?> parents)
    {
        ArgumentNullException.ThrowIfNull(parents);
        return Of(parents, parents.Keys.ToHashSet());
    }

    /// <summary>
    /// Ticks a conversation: a branch with its ancestors (its own branches and its siblings stay as they are), a root with
    /// its whole exploration.
    /// </summary>
    public ShareSelection Tick(ConversationId id)
    {
        if (!_parents.TryGetValue(id, out var parent))
        {
            return this;
        }

        return With(parent is null ? WithBranches(id) : Ancestors(id).Append(id));
    }

    /// <summary>Unticks a conversation with all of its branches.</summary>
    public ShareSelection Untick(ConversationId id) =>
        Of(_parents, _branches, Ticked.Except(WithBranches(id)).ToHashSet());

    /// <summary>
    /// The same ticks over the workspace as it is now: what is gone loses its tick (with anything left without its
    /// ancestors), and what is new has none.
    /// </summary>
    public ShareSelection Over(IReadOnlyDictionary<ConversationId, ConversationId?> parents)
    {
        ArgumentNullException.ThrowIfNull(parents);
        var now = Of(parents, new HashSet<ConversationId>());
        return Of(parents, now._branches, Ticked.Where(id => parents.ContainsKey(id) && now.Ancestors(id).All(Ticked.Contains)).ToHashSet());
    }

    private static ShareSelection Of(IReadOnlyDictionary<ConversationId, ConversationId?> parents, IReadOnlySet<ConversationId> ticked) =>
        Of(parents, parents.Where(p => p.Value is not null).ToLookup(p => p.Value!.Value, p => p.Key), ticked);

    private static ShareSelection Of(IReadOnlyDictionary<ConversationId, ConversationId?> parents, ILookup<ConversationId, ConversationId> branches, IReadOnlySet<ConversationId> ticked) =>
        new(parents, branches, ticked);

    private ShareSelection With(IEnumerable<ConversationId> more) => Of(_parents, _branches, Ticked.Concat(more).ToHashSet());

    /// <summary>The conversation's ancestors, its parent first.</summary>
    private IEnumerable<ConversationId> Ancestors(ConversationId id)
    {
        var at = id;
        while (_parents.TryGetValue(at, out var parent) && parent is { } p && _parents.ContainsKey(p))
        {
            yield return p;
            at = p;
        }
    }

    private List<ConversationId> WithBranches(ConversationId id)
    {
        var all = new List<ConversationId> { id };
        for (var i = 0; i < all.Count; i++)
        {
            all.AddRange(_branches[all[i]]);
        }

        return all;
    }
}
