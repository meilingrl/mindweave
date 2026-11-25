using Mindweave.Application.Exchange;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Import;

/// <summary>The rules of comparison, by identity (docs/design/sharing-import.md §2): a shared file only adds.</summary>
public static class ImportRules
{
    /// <summary>
    /// Whether the document can be true: every identity once, parents that lead to a root or out of the file, and no path
    /// deeper than <see cref="ImportLimits.MaxDepth"/>; links only between conversations in it.
    /// </summary>
    public static bool IsSound(ExchangeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var parents = new Dictionary<ConversationId, ConversationId?>(document.Conversations.Count);
        if (document.Conversations.Count == 0 || !document.Conversations.All(c => parents.TryAdd(c.Id, c.Parent)))
        {
            return false;
        }

        foreach (var conversation in document.Conversations)
        {
            var depth = 1;
            for (var up = conversation.Parent; up is { } parent && parents.TryGetValue(parent, out var next); up = next)
            {
                // A circle never ends, so it passes the limit too.
                if (++depth > ImportLimits.MaxDepth)
                {
                    return false;
                }
            }
        }

        return document.Links.All(l => parents.ContainsKey(l.A) && parents.ContainsKey(l.B));
    }

    /// <summary>The parents the document names but does not hold; they can only be in the store.</summary>
    public static IReadOnlyList<ConversationId> ParentsOutside(ExchangeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var inFile = document.Conversations.Select(c => c.Id).ToHashSet();
        return [.. document.Conversations.Select(c => c.Parent).OfType<ConversationId>().Where(p => !inFile.Contains(p)).Distinct()];
    }

    /// <summary>Everything the store is asked about: the file's conversations and the parents outside it.</summary>
    public static IEnumerable<ConversationId> Compared(ExchangeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Conversations.Select(c => c.Id).Concat(ParentsOutside(document));
    }

    /// <summary>A name from a file on one line, without control characters.</summary>
    public static string OneLine(string name) => ConversationRules.Shorten(name, MaxStoredName);

    /// <summary>Beyond any title the app makes, so a real one is never cut.</summary>
    private const int MaxStoredName = 200;

    /// <summary>What an import into <paramref name="target"/> would do; null when a parent is neither in the file nor in the store.</summary>
    /// <param name="holdings">The workspace of each of the file's conversations, and of the parents outside it, that the store has.</param>
    /// <param name="target">Null for a workspace that does not exist yet.</param>
    public static ImportPlan? Plan(ExchangeDocument document, IReadOnlyDictionary<ConversationId, WorkspaceId> holdings, WorkspaceId? target)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(holdings);
        var isNew = new List<ExchangeConversation>();
        var elsewhere = new Dictionary<ConversationId, WorkspaceId>();
        var decided = new HashSet<ConversationId>();
        var here = 0;

        bool Elsewhere(ConversationId id, out WorkspaceId workspace) =>
            elsewhere.TryGetValue(id, out workspace) || (holdings.TryGetValue(id, out workspace) && workspace != target);

        foreach (var conversation in ParentsFirst(document))
        {
            decided.Add(conversation.Id);
            if (Elsewhere(conversation.Id, out var workspace))
            {
                elsewhere[conversation.Id] = workspace;
            }
            else if (holdings.ContainsKey(conversation.Id))
            {
                here++;
            }
            else if (conversation.Parent is { } parent && Elsewhere(parent, out workspace))
            {
                elsewhere[conversation.Id] = workspace;
            }
            else if (conversation.Parent is { } missing && !decided.Contains(missing) && !holdings.ContainsKey(missing))
            {
                return null;
            }
            else
            {
                isNew.Add(conversation);
            }
        }

        return new ImportPlan(isNew, here, [.. elsewhere.Values.GroupBy(w => w).Select(g => new ImportSkipped(g.Key, g.Count()))]);
    }

    /// <summary>The workspace that holds most of the file's conversations; null when none holds any.</summary>
    public static WorkspaceId? Home(ExchangeDocument document, IReadOnlyDictionary<ConversationId, WorkspaceId> holdings)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(holdings);
        return document.Conversations
            .Where(c => holdings.ContainsKey(c.Id))
            .GroupBy(c => holdings[c.Id])
            .OrderByDescending(g => g.Count())
            .Select(g => (WorkspaceId?)g.Key)
            .FirstOrDefault();
    }

    /// <summary>The file's conversations, each after its parent, otherwise in the file's order.</summary>
    private static IEnumerable<ExchangeConversation> ParentsFirst(ExchangeDocument document)
    {
        var byId = document.Conversations.ToDictionary(c => c.Id);
        var given = new HashSet<ConversationId>(byId.Count);
        var path = new Stack<ExchangeConversation>();
        foreach (var conversation in document.Conversations)
        {
            for (ExchangeConversation? at = conversation; at is not null && given.Add(at.Id); at = at.Parent is { } up ? byId.GetValueOrDefault(up) : null)
            {
                path.Push(at);
            }

            while (path.TryPop(out var next))
            {
                yield return next;
            }
        }
    }
}
