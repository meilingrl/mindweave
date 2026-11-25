using Mindweave.Application.Share;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Exchange;

/// <summary>A summary link between two conversations' summaries.</summary>
public sealed record ExchangeLink(ConversationId A, ConversationId B)
{
    /// <summary>The note, only in a share with content (S7).</summary>
    public string? Note { get; init; }
}

/// <summary>A conversation in a share: its stored identity and where it stands in the path.</summary>
/// <param name="Name">Null: the page calls it "Untitled conversation" in the reader's language.</param>
/// <param name="StartedAt">An instant, in UTC: the sharer's time zone is not in it (§6.6, point 6).</param>
public sealed record ExchangeConversation(ConversationId Id, ConversationId? Parent, string? Name, DateTimeOffset StartedAt, int Questions, bool HasSummary)
{
    /// <summary>What was said, only in a share with content.</summary>
    public ExchangeContent? Content { get; init; }
}

/// <summary>
/// The exchange document of a share (seam E5, docs/design/sharing.md §6.1): a format of its own, never a stored record.
/// What may leave is decided here and nowhere else: the writers and the page never see what was left out.
/// </summary>
public sealed record ExchangeDocument(
    string Kind,
    string Title,
    DateTimeOffset MadeAt,
    string AppVersion,
    IReadOnlyList<ExchangeConversation> Conversations,
    IReadOnlyList<ExchangeLink> Links)
{
    /// <summary>The format's name and version (E5).</summary>
    public const string Format = "mindweave-export";

    public const int Version = 1;

    /// <summary>Titles, lines, dates and counts; no message text.</summary>
    public const string StructureKind = "structure";

    /// <summary>With the questions, the answers, the sentences branches left from and the summaries (S6).</summary>
    public const string ContentKind = "content";

    public int Explorations => Conversations.Count(c => c.Parent is null);

    public int Questions => Conversations.Sum(c => c.Questions);

    /// <summary>Every attachment's file name, in order; null for pasted text.</summary>
    public IReadOnlyList<string?> Attachments => [.. Messages.SelectMany(m => m.Attachments)];

    /// <summary>The citations that carry a web page's title and address.</summary>
    public int WebCitations => Messages.Sum(m => m.Citations.Count(c => c.Url is not null));

    /// <summary>The citations of the user's own material.</summary>
    public int OwnCitations => Messages.Sum(m => m.Citations.Count) - WebCitations;

    /// <summary>The answers kept only because a shared branch left from them (§6.6, point 4).</summary>
    public int EarlierAnswers => Messages.Count(m => m.ReplacedBy is not null);

    public int LinkNotes => Links.Count(l => l.Note is not null);

    public int Summaries => Conversations.Count(c => c.HasSummary);

    private IEnumerable<ExchangeMessage> Messages => Conversations.SelectMany(c => c.Content?.Messages ?? []);

    /// <summary>The structure-only share of what is ticked (S6): nothing of an unticked conversation is in it (S7).</summary>
    public static ExchangeDocument Structure(string title, ShareSource source, ShareSelection selection, DateTimeOffset madeAt, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        var ticked = selection.Over(source.Parents).Ticked;
        return new ExchangeDocument(
            StructureKind,
            title.Trim(),
            madeAt.ToUniversalTime(),
            appVersion,
            [.. source.Conversations.Where(c => ticked.Contains(c.Id))
                .Select(c => new ExchangeConversation(c.Id, c.Parent, c.Name, c.StartedAt.ToUniversalTime(), c.Questions, c.HasSummary))
                .OrderBy(c => c.StartedAt).ThenBy(c => c.Id.Value)],
            [.. source.Links.Where(l => ticked.Contains(l.A) && ticked.Contains(l.B))]);
    }

    /// <summary>
    /// The share with content of what is ticked (S6, S7). Conversations go by the name the app uses for them (§6.6, point 1),
    /// and every number covers what is in the share. A conversation whose content could not be read any more was deleted
    /// meanwhile: it is left out with its branches.
    /// </summary>
    public static ExchangeDocument Content(string title, ShareSource source, ShareSelection selection, ShareContents contents, DateTimeOffset madeAt, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(contents);
        var parents = source.Parents;
        bool IsThere(ConversationId id) => contents.Conversations.ContainsKey(id) && (parents[id] is not { } parent || !parents.ContainsKey(parent) || IsThere(parent));
        var there = source with { Conversations = [.. source.Conversations.Where(c => !selection.Ticked.Contains(c.Id) || IsThere(c.Id))] };
        var structure = Structure(title, there, selection, madeAt, appVersion);
        var names = there.Conversations.ToDictionary(c => c.Id, c => c.ContentName);
        var filter = new ShareContentFilter(contents, structure.Conversations.Select(c => c.Id).ToHashSet());
        return structure with
        {
            Kind = ContentKind,
            Conversations = [.. structure.Conversations.Select(c =>
            {
                var content = filter.Of(c.Id);
                return c with
                {
                    Name = names[c.Id],
                    Questions = content.Messages.Count(m => m.Role == MessageRole.User),
                    HasSummary = content.Summary is not null,
                    Content = content,
                };
            })],
            Links = [.. filter.Links()],
        };
    }

    /// <summary>The share without these conversations and the links to them (they were deleted before it was saved, §9).</summary>
    public ExchangeDocument Without(IEnumerable<ConversationId> gone)
    {
        var left = gone.ToHashSet();
        return this with
        {
            Conversations = [.. Conversations.Where(c => !left.Contains(c.Id))],
            Links = [.. Links.Where(l => !left.Contains(l.A) && !left.Contains(l.B))],
        };
    }

    /// <summary>The same conversations and links, whatever the title and the time the documents were made.</summary>
    public bool SameContents(ExchangeDocument other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Conversations.SequenceEqual(other.Conversations) && Links.SequenceEqual(other.Links);
    }
}
