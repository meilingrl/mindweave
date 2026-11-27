using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
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

        return document.Links.All(l => parents.ContainsKey(l.A) && parents.ContainsKey(l.B) && l.A != l.B && !SummaryLinkNote.IsTooLong(l.Note)) && ContentIsSound(document);
    }

    /// <summary>
    /// What a share with content says can be true (§7): every message and version once, numbers that grow, answers only after
    /// a question with one that stands among them, and sources that are in the parent. A structure-only share says nothing.
    /// </summary>
    private static bool ContentIsSound(ExchangeDocument document)
    {
        if (document.Kind != ExchangeDocument.ContentKind)
        {
            return document.Conversations.All(c => c.Content is null);
        }

        var messages = new HashSet<MessageId>();
        var versions = new HashSet<SummaryVersionId>();
        var parts = document.Conversations.ToDictionary(c => c.Id, c => (c.Content?.Messages ?? []).DistinctBy(m => m.Id).ToDictionary(m => m.Id, m => m.Role));
        return document.Conversations.All(c => c.Content is { } content
            && content.Messages.All(m => messages.Add(m.Id))
            && MessagesAreSound(content.Messages)
            && (content.Summary is null || versions.Add(content.Summary.Id))
            && (content.Source is null || SourceIsSound(content.Source, c.Parent, parts)));
    }

    private static bool MessagesAreSound(IReadOnlyList<ExchangeMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (i == 0 ? message.Role != MessageRole.User : message.Seq <= messages[i - 1].Seq)
            {
                return false;
            }

            if (message.Role == MessageRole.User)
            {
                if (message is not { State: MessageState.Complete, Activity: null, ReplacedBy: null, Citations.Count: 0 } || message.Text.Length > ConversationService.MaxQuestionLength
                    || (string.IsNullOrWhiteSpace(message.Text) && message.Attachments.Count == 0))
                {
                    return false;
                }

                continue;
            }

            // The last answer to a question stands; those before it say which one that is.
            var next = i + 1 < messages.Count && messages[i + 1].Role == MessageRole.Assistant ? messages[i + 1] : null;
            if (string.IsNullOrWhiteSpace(message.Text) || message.Attachments.Count > 0
                || message.Citations.Select(c => c.Number).Distinct().Count() != message.Citations.Count
                || message.ReplacedBy != (next is null ? null : next.ReplacedBy ?? next.Id))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A quoted sentence is an answer's. The parent itself can be missing from the file only when it is in the store.</summary>
    private static bool SourceIsSound(ExchangeBranchSource source, ConversationId? parent, Dictionary<ConversationId, Dictionary<MessageId, MessageRole>> parts) =>
        parent is { } id && (!parts.TryGetValue(id, out var left) || (left.TryGetValue(source.Message, out var role) && CanBeLeft(source, role)));

    private static bool CanBeLeft(ExchangeBranchSource source, MessageRole role) => source.Quote is null || role == MessageRole.Assistant;

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
    /// <param name="stored">What the store has of the contents; without it every held conversation stays as it is.</param>
    public static ImportPlan? Plan(ExchangeDocument document, IReadOnlyDictionary<ConversationId, WorkspaceId> holdings, WorkspaceId? target, StoredContents? stored = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(holdings);
        var contents = new Contents(stored ?? StoredContents.Nothing);
        var gains = new List<ImportGain>();
        var unfit = new HashSet<ConversationId>();
        var own = 0;
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
                switch (contents.Compare(conversation, out var gain))
                {
                    case Fit.Gains:
                        gains.Add(gain!);
                        break;
                    case Fit.Unfit:
                        unfit.Add(conversation.Id);
                        break;
                    case Fit.Own:
                        own++;
                        break;
                    default:
                        here++;
                        break;
                }
            }
            else if (conversation.Parent is { } parent && Elsewhere(parent, out workspace))
            {
                elsewhere[conversation.Id] = workspace;
            }
            else if (conversation.Parent is { } missing && !decided.Contains(missing) && !holdings.ContainsKey(missing))
            {
                return null;
            }
            else if (!contents.Fits(conversation) || (conversation.Parent is { } skipped && unfit.Contains(skipped) && !holdings.ContainsKey(skipped)))
            {
                unfit.Add(conversation.Id);
            }
            else
            {
                isNew.Add(conversation);
                contents.Arrives(conversation);
            }
        }

        var there = decided.Where(id => !elsewhere.ContainsKey(id)).ToHashSet();
        return new ImportPlan(isNew, here, [.. elsewhere.Values.GroupBy(w => w).Select(g => new ImportSkipped(g.Key, g.Count()))])
        {
            Gains = gains,
            Unfit = unfit.Count,
            Own = own,
            Links = [.. contents.NewLinks(document.Links.Where(l => there.Contains(l.A) && there.Contains(l.B)))],
        };
    }

    /// <summary>How the file's part of a conversation that is here compares with it.</summary>
    private enum Fit
    {
        Same,
        Gains,
        Unfit,

        /// <summary>The user's own, and the file has messages for it that are not here.</summary>
        Own,
    }

    /// <summary>The store's contents, with what the plan adds so far.</summary>
    private sealed class Contents(StoredContents stored)
    {
        private readonly Dictionary<ConversationId, Dictionary<MessageId, MessageRole>> _messages =
            stored.Conversations.ToDictionary(c => c.Key, c => c.Value.Messages.Values.ToDictionary(m => m.Id, m => m.Role));
        private readonly HashSet<ConversationId> _summarized = [.. stored.Conversations.Where(c => c.Value.HasSummary).Select(c => c.Key)];

        /// <summary>No message or version of it is another conversation's here.</summary>
        public bool Fits(ExchangeConversation conversation) => conversation.Content is not { } content
            || (content.Messages.All(m => !stored.Messages.TryGetValue(m.Id, out var at) || at == conversation.Id)
                && (content.Summary is not { } summary || !stored.Versions.TryGetValue(summary.Id, out var of) || of == conversation.Id));

        public void Arrives(ExchangeConversation conversation)
        {
            if (conversation.Content is { } content)
            {
                Arrive(conversation.Id, content.Messages, content.Summary is not null);
            }
        }

        public Fit Compare(ExchangeConversation conversation, out ImportGain? gain)
        {
            gain = null;
            if (conversation.Content is not { } content || !stored.Conversations.TryGetValue(conversation.Id, out var held))
            {
                return Fit.Same;
            }

            var lacking = content.Messages.Where(m => !stored.Messages.ContainsKey(m.Id)).ToList();
            if (!held.IsReceived && lacking.Count > 0)
            {
                return Fit.Own;
            }

            if (!Fits(conversation) || lacking.Any(m => held.Messages.ContainsKey(m.Seq)) || !FitsBetween(held, lacking))
            {
                return Fit.Unfit;
            }

            // Parents come first, so what the parent gains is known.
            var source = held is { IsReceived: true, HasSource: false } && content.Source is { } left && conversation.Parent is { } parent && held.Parent == parent && IsThere(parent, left);
            var summary = content.Summary is { } version && !stored.Versions.ContainsKey(version.Id);
            if (lacking.Count == 0 && !source && !summary)
            {
                return Fit.Same;
            }

            gain = new ImportGain(conversation, lacking, source, summary);
            Arrive(conversation.Id, lacking, summary);
            return Fit.Gains;
        }

        /// <summary>Both have a summary here, and no link joins them yet.</summary>
        public IEnumerable<ExchangeLink> NewLinks(IEnumerable<ExchangeLink> links) => links
            .Where(l => _summarized.Contains(l.A) && _summarized.Contains(l.B) && !stored.Linked.Contains(StoredContents.Pair(l.A, l.B)))
            .DistinctBy(l => StoredContents.Pair(l.A, l.B));

        /// <summary>
        /// What arrives below a number that is here can only be an attempt another answer replaced, which no context uses:
        /// anything else would change what the user's own continuations are asked about (§2).
        /// </summary>
        private static bool FitsBetween(StoredConversation held, List<ExchangeMessage> lacking)
        {
            if (held.Messages.Count == 0 || lacking.Count == 0)
            {
                return true;
            }

            var last = held.Messages.Keys.Max();
            var merged = held.Messages.Select(m => (Seq: m.Key, m.Value.Role, IsNew: false)).Concat(lacking.Select(m => (m.Seq, m.Role, IsNew: true))).OrderBy(m => m.Seq).ToList();
            return merged.Zip(merged.Skip(1)).All(p => !p.First.IsNew || p.First.Seq > last || (p.First.Role == MessageRole.Assistant && p.Second.Role == MessageRole.Assistant));
        }

        private bool IsThere(ConversationId conversation, ExchangeBranchSource source) =>
            _messages.TryGetValue(conversation, out var messages) && messages.TryGetValue(source.Message, out var role) && CanBeLeft(source, role);

        private void Arrive(ConversationId conversation, IReadOnlyList<ExchangeMessage> messages, bool summary)
        {
            if (!_messages.TryGetValue(conversation, out var held))
            {
                _messages[conversation] = held = [];
            }

            foreach (var message in messages)
            {
                held[message.Id] = message.Role;
            }

            if (summary)
            {
                _summarized.Add(conversation);
            }
        }
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
