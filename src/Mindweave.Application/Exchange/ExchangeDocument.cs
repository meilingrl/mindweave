using Mindweave.Application.Share;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Exchange;

/// <summary>A summary link between two conversations' summaries, without its note (S7).</summary>
public sealed record ExchangeLink(ConversationId A, ConversationId B);

/// <summary>A conversation in a share: its stored identity and where it stands in the path.</summary>
/// <param name="Name">Null: the page calls it "Untitled conversation" in the reader's language.</param>
/// <param name="StartedAt">An instant, in UTC: the sharer's time zone is not in it (§6.6, point 6).</param>
public sealed record ExchangeConversation(ConversationId Id, ConversationId? Parent, string? Name, DateTimeOffset StartedAt, int Questions, bool HasSummary);

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

    public int Explorations => Conversations.Count(c => c.Parent is null);

    public int Questions => Conversations.Sum(c => c.Questions);

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
