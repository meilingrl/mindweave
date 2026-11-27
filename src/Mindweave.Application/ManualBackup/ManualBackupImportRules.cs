using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.ManualBackup;

/// <summary>
/// The rules of importing a manual backup (docs/design/sharing-backup.md §2): everything by identity, and what is here is
/// never changed, except the numbers of a conversation joined by time (V2) and what refers to them.
/// </summary>
public static class ManualBackupImportRules
{
    /// <summary>
    /// Whether the package's documents can be true: each a workspace's, every identity once across them, parents in the same
    /// document and no path deeper than <see cref="ImportLimits.MaxDepth"/>, messages numbered upwards that begin with a
    /// question, summaries whose current version is theirs, links between summarized conversations, and every material a
    /// message refers to described, in the message's own conversation.
    /// </summary>
    public static bool IsSound(IReadOnlyList<ExchangeDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var workspaces = new HashSet<WorkspaceId>();
        var conversations = new HashSet<ConversationId>();
        var messages = new HashSet<MessageId>();
        var versions = new HashSet<SummaryVersionId>();
        var nodes = new HashSet<SummaryNodeId>();
        var resources = new HashSet<ResourceId>();
        foreach (var document in documents)
        {
            if (document is not { Kind: ExchangeDocument.DeviceKind, Workspace: { } workspace } || !workspaces.Add(workspace.Id)
                || !document.Conversations.All(c => conversations.Add(c.Id)) || !document.Resources.All(r => resources.Add(r.Id)))
            {
                return false;
            }

            var parents = document.Conversations.ToDictionary(c => c.Id, c => c.Parent);
            var material = document.Resources.ToDictionary(r => r.Id);
            foreach (var conversation in document.Conversations)
            {
                var depth = 1;
                for (var up = conversation.Parent; up is { } parent; up = parents[parent])
                {
                    if (!parents.ContainsKey(parent) || ++depth > ImportLimits.MaxDepth)
                    {
                        return false;
                    }
                }

                if (conversation.Content is not { } content || !content.Messages.All(m => messages.Add(m.Id) && m.Stored is not null)
                    || !Numbered(content.Messages)
                    || !content.Messages.SelectMany(ManualBackupDocument.Referred).All(r => material.TryGetValue(r, out var of) && (of.Conversation is null || of.Conversation == conversation.Id))
                    || (content.Source is { } source && (conversation.Parent is not { } from || !document.Conversations.Single(c => c.Id == from).Content!.Messages.Any(m => m.Id == source.Message)))
                    || (content.History is { } history && (!nodes.Add(history.Node) || !history.Versions.All(v => versions.Add(v.Id))
                        || history.Versions.All(v => v.Id != history.Current) || history.Versions.Select(v => v.Seq).Distinct().Count() != history.Versions.Count)))
                {
                    return false;
                }
            }

            var summarized = document.Conversations.Where(c => c.Content?.History is not null).Select(c => c.Id).ToHashSet();
            if (!document.Links.All(l => l.A != l.B && summarized.Contains(l.A) && summarized.Contains(l.B) && !SummaryLinkNote.IsTooLong(l.Note)))
            {
                return false;
            }
        }

        return documents.Count > 0;
    }

    private static bool Numbered(IReadOnlyList<ExchangeMessage> messages) =>
        (messages.Count == 0 || messages[0].Role == MessageRole.User) && messages.Zip(messages.Skip(1)).All(p => p.Second.Seq > p.First.Seq);

    /// <summary>A conversation's messages as joining sees them: a question when it was asked, an answer when its attempt began.</summary>
    public static IReadOnlyList<LineEntry> Line(IEnumerable<ExchangeMessage> messages) =>
        [.. messages.Select(m => new LineEntry(m.Id, m.Role, m.Seq, m.Stored?.CreatedAt ?? m.At))];

    /// <summary>What importing the package would do, compared with <paramref name="stored"/>.</summary>
    public static ManualBackupPlan Plan(ManualBackupFile file, ManualBackupStored stored)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(stored);
        return new ManualBackupPlan([.. file.Workspaces.Select(d => Plan(d, stored))]);
    }

    private static ManualBackupWorkspacePlan Plan(ExchangeDocument document, ManualBackupStored stored)
    {
        var workspace = document.Workspace!.Id;
        var plans = new Dictionary<ConversationId, ManualBackupConversationPlan>();
        var ordered = new List<ManualBackupConversationPlan>();
        foreach (var conversation in ParentsFirst(document))
        {
            var plan = stored.Conversations.TryGetValue(conversation.Id, out var held)
                ? Compare(conversation, held, workspace, stored)
                : Arrive(conversation, workspace, stored, conversation.Parent is { } parent ? plans[parent] : null);
            plans[conversation.Id] = plan;
            ordered.Add(plan);
        }

        // A link joins two summaries; it is added where both are here after the import, in one workspace, and not yet linked.
        var nodes = ordered.Where(p => p.Fit != ManualBackupFit.Unfit || stored.Conversations.ContainsKey(p.Conversation.Id))
            .Select(p => (p.Conversation.Id, Node: stored.Conversations.GetValueOrDefault(p.Conversation.Id)?.Node ?? (p.NewSummary ? p.Conversation.Content!.History!.Node : (SummaryNodeId?)null), p.Workspace))
            .Where(n => n.Node is not null)
            .ToDictionary(n => n.Id, n => (Node: n.Node!.Value, n.Workspace));
        var links = new List<ExchangeLink>();
        var added = new HashSet<(SummaryNodeId, SummaryNodeId)>();
        foreach (var link in document.Links)
        {
            if (nodes.TryGetValue(link.A, out var a) && nodes.TryGetValue(link.B, out var b) && a.Workspace == b.Workspace && a.Node != b.Node
                && SummaryLinkPair.Normalize(a.Node, b.Node) is var pair && !stored.Linked.Contains(pair) && added.Add(pair))
            {
                links.Add(link);
            }
        }

        return new ManualBackupWorkspacePlan(document, !stored.Workspaces.Contains(workspace), ordered, links);
    }

    /// <summary>A conversation that is not here: created as the package has it, beside its parent wherever that is here.</summary>
    private static ManualBackupConversationPlan Arrive(ExchangeConversation conversation, WorkspaceId workspace, ManualBackupStored stored, ManualBackupConversationPlan? parent)
    {
        var content = conversation.Content!;
        var unfit = parent is { Fit: ManualBackupFit.Unfit } && !stored.Conversations.ContainsKey(parent.Conversation.Id)
            || content.Messages.Any(m => stored.Messages.ContainsKey(m.Id))
            || !MaterialFits(conversation.Id, content.Messages, stored)
            || (content.History is { } history && (stored.Nodes.Contains(history.Node) || history.Versions.Any(v => stored.Versions.ContainsKey(v.Id))));
        if (unfit)
        {
            return Nothing(conversation, parent?.Workspace ?? workspace, ManualBackupFit.Unfit);
        }

        return new ManualBackupConversationPlan(
            conversation,
            parent?.Workspace ?? workspace,
            ManualBackupFit.New,
            content.Messages,
            ConversationJoin.Join([], Line(content.Messages)),
            content.History?.Versions ?? [],
            content.History is not null,
            GainsSource: false,
            Elsewhere: parent is not null && parent.Workspace != workspace,
            Running: false);
    }

    /// <summary>A conversation that is here: it keeps everything it has and gains what it lacks.</summary>
    private static ManualBackupConversationPlan Compare(ExchangeConversation conversation, ManualBackupStoredConversation held, WorkspaceId workspace, ManualBackupStored stored)
    {
        var content = conversation.Content!;
        var elsewhere = held.Workspace != workspace;

        // A message or a summary version that another conversation holds here cannot be this one's.
        if (content.Messages.Any(m => stored.Messages.TryGetValue(m.Id, out var of) && of != conversation.Id)
            || (content.History?.Versions.Any(v => stored.Versions.TryGetValue(v.Id, out var of) && of != conversation.Id) ?? false)
            || (held.Node is null && content.History is { } fresh && stored.Nodes.Contains(fresh.Node))
            || !MaterialFits(conversation.Id, content.Messages.Where(m => !stored.Messages.ContainsKey(m.Id)), stored))
        {
            return Nothing(conversation, held.Workspace, ManualBackupFit.Unfit) with { Elsewhere = elsewhere };
        }

        var arriving = content.Messages.Where(m => !stored.Messages.ContainsKey(m.Id)).ToList();
        var (fit, line) = arriving.Count == 0 ? (ManualBackupFit.Same, (JoinedLine?)null)
            : held.IsReceived || conversation.Received is not null ? AtOwnNumbers(held, arriving)
            : ConversationJoin.Join(held.Messages, Line(content.Messages)) is var joined ? (joined.Renumbered ? ManualBackupFit.Joins : ManualBackupFit.Appends, joined) : default;
        if (fit == ManualBackupFit.Unfit)
        {
            return Nothing(conversation, held.Workspace, fit) with { Elsewhere = elsewhere };
        }

        var versions = content.History is not { } history ? []
            : held.Node is null ? history.Versions
            : [.. history.Versions.Where(v => !stored.Versions.ContainsKey(v.Id))];
        return new ManualBackupConversationPlan(
            conversation,
            held.Workspace,
            fit,
            fit == ManualBackupFit.Same ? [] : arriving,
            line,
            versions,
            held.Node is null && content.History is not null,
            GainsSource: !held.HasSource && content.Source is not null,
            elsewhere,
            Running: fit != ManualBackupFit.Same && held.HasUnfinished);
    }

    /// <summary>
    /// A received conversation is read-only everywhere, so a package only brings more of it (docs/design/sharing-import.md
    /// §2): each message at its own number, and below a number that is here only an attempt that another answer replaced.
    /// </summary>
    private static (ManualBackupFit, JoinedLine?) AtOwnNumbers(ManualBackupStoredConversation held, List<ExchangeMessage> arriving)
    {
        var numbers = held.Messages.Select(m => m.Seq).ToHashSet();
        var last = held.Messages.Count > 0 ? held.Messages.Max(m => m.Seq) : -1;
        var merged = held.Messages.Select(m => (m.Seq, m.Id, m.Role, IsNew: false)).Concat(arriving.Select(m => (m.Seq, m.Id, m.Role, IsNew: true))).OrderBy(m => m.Seq).ToList();
        var fits = arriving.All(m => !numbers.Contains(m.Seq))
            && merged.Zip(merged.Skip(1)).All(p => !p.First.IsNew || p.First.Seq > last || (p.First.Role == MessageRole.Assistant && p.Second.Role == MessageRole.Assistant));
        return fits ? (ManualBackupFit.Appends, new JoinedLine([.. merged.Select(m => (m.Id, m.Seq))], Renumbered: false)) : (ManualBackupFit.Unfit, null);
    }

    /// <summary>The material the messages refer to is not another conversation's here.</summary>
    private static bool MaterialFits(ConversationId conversation, IEnumerable<ExchangeMessage> messages, ManualBackupStored stored) =>
        messages.SelectMany(ManualBackupDocument.Referred).All(r => !stored.Resources.TryGetValue(r, out var of) || of == conversation);

    private static ManualBackupConversationPlan Nothing(ExchangeConversation conversation, WorkspaceId workspace, ManualBackupFit fit) =>
        new(conversation, workspace, fit, [], null, [], false, false, false, false);

    /// <summary>The document's conversations, each after its parent, otherwise in the document's order.</summary>
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
