using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Summaries;

/// <summary>
/// The summaries the user already has on what a question is about (requirements §10.9, docs/design/reply-quality.md Q4): one
/// local keyword search of a workspace's summaries before an answer, whose titles go with the question so the model builds on
/// them. No model request. Words and CJK pairs are matched as the summary search tool matches them (<see cref="SearchTerms"/>);
/// words that carry no topic are left out.
/// <para>A summary matches when one of these holds:</para>
/// <list type="number">
/// <item>every word of its title is in the query;</item>
/// <item>a word of its title and at least <see cref="MinTerms"/> words in all are in the query;</item>
/// <item>at least <see cref="MinBodyTerms"/> words of the query are in its body.</item>
/// </list>
/// <para>In a workspace of at least three summaries, a word found in all of them says nothing and does not count. The
/// matches are ranked as the summary search tool ranks them (rare words weigh more, the title triple) and the first
/// <see cref="MaxTitles"/> are kept.</para>
/// </summary>
public sealed class MatchingSummaries(ISummaryStore summaries)
{
    /// <summary>At most this many titles go with a question.</summary>
    public const int MaxTitles = 3;

    /// <summary>A title match needs this many query words in the summary in all, unless the title is matched whole.</summary>
    public const int MinTerms = 2;

    /// <summary>Without a title match, the body must hold this many words of the query.</summary>
    public const int MinBodyTerms = 3;

    /// <summary>Characters of a body that are searched (a summary is usually far shorter).</summary>
    public const int MaxBody = 8_000;

    /// <summary>Characters of the query that are searched; a long question is about what its start says.</summary>
    public const int MaxQuery = 2_000;

    /// <summary>
    /// The display names of the best-matching summaries of <paramref name="workspace"/> for <paramref name="query"/>, best
    /// first; none of a conversation in <paramref name="excluded"/> (the ones whose text is in the request already).
    /// </summary>
    public async Task<IReadOnlyList<string>> TitlesAsync(WorkspaceId workspace, string query, IReadOnlyCollection<ConversationId> excluded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(excluded);
        var terms = TermsOf(query.Length > MaxQuery ? query[..MaxQuery] : query);
        if (terms.Count == 0)
        {
            return [];
        }

        var all = await summaries.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        var entries = all.Where(e => !excluded.Contains(e.Node.Conversation)).ToList();
        if (entries.Count == 0)
        {
            return [];
        }

        var bodies = await summaries.ExcerptsAsync([.. entries.Select(e => e.Current.Id)], MaxBody, cancellationToken).ConfigureAwait(false);
        var documents = entries.Select(e => new Document(
            e.Node.DisplayName,
            TermsOf(e.Node.DisplayName),
            TermsOf(bodies.GetValueOrDefault(e.Current.Id) ?? string.Empty),
            e.Node.UpdatedAt)).ToList();
        return Rank(terms, documents, all.Count);
    }

    /// <summary>The ranking itself, apart from storage: <paramref name="workspaceSize"/> counts the excluded summaries too.</summary>
    public static IReadOnlyList<string> Rank(IReadOnlySet<string> terms, IReadOnlyList<Document> documents, int workspaceSize)
    {
        var frequency = terms.ToDictionary(t => t, t => documents.Count(d => d.Title.Contains(t) || d.Body.Contains(t)), StringComparer.Ordinal);

        // A word in every summary says nothing about which one; with fewer than three summaries every word still counts.
        bool Counts(string term) => workspaceSize < 3 || frequency[term] < documents.Count;
        double Weight(string term) => Math.Log(1 + ((double)documents.Count / Math.Max(1, frequency[term])));

        return [.. documents
            .Select(d =>
            {
                var inTitle = d.Title.Where(t => terms.Contains(t) && Counts(t)).ToList();
                var inBody = terms.Where(t => d.Body.Contains(t) && Counts(t)).ToList();
                var matched = inTitle.Union(inBody).Count();
                var matches = (d.Title.Count > 0 && d.Title.All(terms.Contains) && inTitle.Count > 0)
                    || (inTitle.Count > 0 && matched >= MinTerms)
                    || inBody.Count >= MinBodyTerms;
                var score = inTitle.Sum(t => 3 * Weight(t)) + inBody.Sum(Weight);
                return (d.Name, d.UpdatedAt, matches, score);
            })
            .Where(d => d.matches)
            .OrderByDescending(d => d.score)
            .ThenByDescending(d => d.UpdatedAt)
            .Take(MaxTitles)
            .Select(d => d.Name)];
    }

    /// <summary>The searched words of a text: <see cref="SearchTerms"/>' words and CJK pairs, without those that carry no topic.</summary>
    public static HashSet<string> TermsOf(string text) =>
        [.. SearchTerms.Of(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(Carries)];

    /// <summary>
    /// A word that can say what a text is about: a word of at least three letters or two digits, or a CJK pair without a
    /// particle or pronoun in it (a pair across two words, such as "道的", is in almost any Chinese text); and not one of the
    /// common words of a question.
    /// </summary>
    private static bool Carries(string term)
    {
        if (term.Length < 2 || Common.Contains(term))
        {
            return false;
        }

        if (term.Length == 2 && term.All(c => c is >= 'a' and <= 'z'))
        {
            return false;
        }

        return !(term.Length == 2 && char.GetUnicodeCategory(term[0]) == System.Globalization.UnicodeCategory.OtherLetter
            && term.Any(c => Particles.Contains(c)));
    }

    /// <summary>Chinese particles and pronouns: a pair with one of them is no topic.</summary>
    private const string Particles = "的了吗呢吧么着我你他她它们是也都就这那";

    /// <summary>Words of a question that name no topic: question words, auxiliaries and the like, in English and Chinese pairs.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "the", "and", "are", "was", "were", "has", "have", "had", "does", "did", "can", "could", "would", "should", "will", "may",
        "might", "what", "why", "how", "when", "where", "which", "who", "whom", "whose", "that", "this", "these", "those", "there",
        "their", "they", "them", "then", "than", "with", "from", "into", "about", "for", "not", "but", "you", "your", "yours",
        "please", "explain", "tell", "give", "more", "some", "any", "all", "its", "it's", "also", "just", "only", "very", "much",
        "many", "one", "ones", "other", "same", "such", "like", "want", "know", "mean", "means", "use", "used", "get", "make",
        "between", "difference", "work", "works", "simpler", "again", "example", "examples", "say", "said", "thing", "things",
        "什么", "怎么", "为什", "为何", "如何", "哪些", "哪个", "是什", "么是", "是不", "不是", "有什", "么区", "区别", "一下", "可以",
        "能不", "不能", "这个", "那个", "这些", "那些", "这样", "那样", "一个", "我们", "你们", "他们", "我想", "想知", "知道", "请问",
        "解释", "说明", "讲讲", "介绍", "简单", "一点", "还有", "就是", "因为", "所以", "然后", "如果", "的是", "是怎", "么样", "怎样",
    };

    /// <summary>One summary as the ranking sees it.</summary>
    public sealed record Document(string Name, HashSet<string> Title, HashSet<string> Body, DateTimeOffset UpdatedAt);
}
