using System.Globalization;
using System.Text;
using Mindweave.Core.Context;
using Mindweave.Core.Resources;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Application.Tools;

/// <summary>
/// A long page as the model gets it (requirements §10.9, architecture §15.4, docs/design/reply-quality.md Q8): its opening
/// (title and lead, about <see cref="OpeningTokens"/>), then the paragraphs that best match the question and the searches
/// that led to the page, in page order, with a mark at each gap that names the part it continues in. Paragraphs are ranked
/// locally by keyword (BM25 over the page's own paragraphs), with the same terms as the attachment search: words, and CJK
/// text as two-character pieces (<see cref="SearchTerms"/>). No model request.
/// </summary>
public static class PagePassages
{
    /// <summary>The opening: the page's start up to about this many estimated tokens.</summary>
    public const int OpeningTokens = 300;

    /// <summary>A line longer than this many estimated tokens is ranked in pieces, cut at a sentence end where one is near.</summary>
    public const int ParagraphTokens = 400;

    /// <summary>A paragraph is sent only when it scores at least this share of the best one's score.</summary>
    public const double MinShare = 0.25;

    /// <summary>
    /// A paragraph matches when it has a telling term of the queries: one found in at most this share of the page's
    /// paragraphs (in at least one), so a word the whole page repeats does not make every paragraph a match.
    /// </summary>
    public const double TellingShare = 0.25;

    /// <summary>
    /// Text in which at least this share of the visible characters are brackets, braces, double quotes, bars or tabs is
    /// data (JSON, a table), not prose: it keeps its start.
    /// </summary>
    public const double DataShare = 0.05;

    /// <summary>The excerpt counts as full once fewer than this many tokens are left.</summary>
    private const int FullWithin = 20;

    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>What stands where text is left out before a passage in part <paramref name="part"/> of <paramref name="parts"/>.</summary>
    public static string Gap(int part, int parts) => T(
        string.Create(CultureInfo.InvariantCulture, $"[…] (part {part} of {parts})"),
        string.Create(CultureInfo.InvariantCulture, $"【……】（第 {part} 部分，共 {parts} 部分）"));

    /// <summary>What stands where the rest of the page is left out.</summary>
    public static string Rest => T("[…]", "【……】");

    /// <summary>
    /// <paramref name="text"/> within about <paramref name="tokens"/> estimated tokens as its opening and best-matching paragraphs;
    /// null when it is data, when nothing in it matches <paramref name="queries"/> well, or when it fits whole: the caller then
    /// sends its start. The parts named in the marks are those the stored text is read by (<see cref="ResourceChunker"/>).
    /// </summary>
    public static string? Select(string text, IEnumerable<string?> queries, int tokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(queries);
        if (TokenEstimator.Estimate(text) <= tokens || LooksLikeData(text))
        {
            return null;
        }

        var terms = queries.Where(q => !string.IsNullOrWhiteSpace(q)).SelectMany(q => SearchTerms.QueryTerms(q!)).ToHashSet(StringComparer.Ordinal);
        var paragraphs = Paragraphs(text);
        if (terms.Count == 0 || paragraphs.Count < 2)
        {
            return null;
        }

        var (openingEnd, first) = Opening(text, paragraphs, Math.Min(OpeningTokens, tokens));
        var ranked = Rank(text, paragraphs, terms, first);
        if (ranked.Count == 0)
        {
            return null;
        }

        var parts = ResourceChunker.Split(text);
        var chosen = new SortedSet<int>();
        var excerpt = Build(text, openingEnd, paragraphs, chosen, parts);
        foreach (var index in ranked)
        {
            chosen.Add(index);
            var next = Build(text, openingEnd, paragraphs, chosen, parts);
            if (TokenEstimator.Estimate(next) > tokens)
            {
                chosen.Remove(index);
                continue;
            }

            excerpt = next;
            if (TokenEstimator.Estimate(excerpt) >= tokens - FullWithin)
            {
                break;
            }
        }

        return chosen.Count == 0 ? null : excerpt;
    }

    /// <summary>Whether the text is data rather than prose (<see cref="DataShare"/>).</summary>
    public static bool LooksLikeData(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int marks = 0, visible = 0;
        foreach (var c in text)
        {
            if (c is '\t')
            {
                marks++;
                visible++;
            }
            else if (!char.IsWhiteSpace(c))
            {
                visible++;
                if (c is '{' or '}' or '[' or ']' or '"' or '|')
                {
                    marks++;
                }
            }
        }

        return visible > 0 && marks >= visible * DataShare;
    }

    /// <summary>The lines of the text with text in them, trimmed; a long one in pieces of at most <see cref="ParagraphTokens"/>.</summary>
    private static List<(int Start, int End)> Paragraphs(string text)
    {
        var list = new List<(int, int)>();
        var at = 0;
        while (at < text.Length)
        {
            var lineEnd = text.IndexOf('\n', at);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var (start, end) = (at, lineEnd);
            while (start < end && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }

            while (start < end)
            {
                var cut = Cut(text, start, end, ParagraphTokens);
                list.Add((start, cut));
                start = cut;
                while (start < end && char.IsWhiteSpace(text[start]))
                {
                    start++;
                }
            }

            at = lineEnd + 1;
        }

        return list;
    }

    /// <summary>
    /// Where a piece of [<paramref name="start"/>, <paramref name="end"/>) of at most <paramref name="tokens"/> ends: the end
    /// when it fits, else after the last sentence end in the piece's second half, else where the size is reached.
    /// </summary>
    private static int Cut(string text, int start, int end, int tokens)
    {
        // Grow by whole runes until the next would pass the limit (ASCII counts a third, everything else one, as
        // TokenEstimator counts), so a very long line costs no more than its length.
        int ascii = 0, other = 0, at = start;
        while (at < end)
        {
            var wide = char.IsHighSurrogate(text[at]) && at + 1 < end ? 2 : 1;
            var (a, o) = text[at] < 0x80 ? (ascii + 1, other) : (ascii, other + 1);
            if (o + ((a + 2) / 3) > tokens)
            {
                break;
            }

            (ascii, other, at) = (a, o, at + wide);
        }

        if (at >= end)
        {
            return end;
        }

        var limit = Math.Max(at, start + 1);
        for (var i = limit - 1; i > start + ((limit - start) / 2); i--)
        {
            if (text[i] is '。' or '！' or '？' or '；' || (text[i] is '.' or '!' or '?' or ';' && i + 1 < end && char.IsWhiteSpace(text[i + 1])))
            {
                return i + 1;
            }
        }

        return limit;
    }

    /// <summary>
    /// The opening: whole paragraphs from the start while they fit, then, when they fill less than half of it, as much of
    /// the next as fits; and the first paragraph not in it (one cut short is left out, its rest being marked as a gap).
    /// </summary>
    private static (int End, int First) Opening(string text, List<(int Start, int End)> paragraphs, int tokens)
    {
        var end = 0;
        var index = 0;
        while (index < paragraphs.Count && TokenEstimator.Estimate(text[..paragraphs[index].End]) <= tokens)
        {
            end = paragraphs[index].End;
            index++;
        }

        if (index < paragraphs.Count)
        {
            // A paragraph cut short only when little of the page fits otherwise (a long first paragraph), not to fill a tail.
            var room = tokens - TokenEstimator.Estimate(text[..end]);
            var next = paragraphs[index];
            if (room > tokens / 2)
            {
                var cut = Cut(text, next.Start, next.End, room);
                if (TokenEstimator.Estimate(text[..cut]) <= tokens)
                {
                    end = cut;
                }
            }

            if (end > next.Start)
            {
                index++;
            }
        }

        return (end, index);
    }

    /// <summary>
    /// The paragraphs from <paramref name="first"/> on that match (a telling term, <see cref="TellingShare"/>) and score at
    /// least <see cref="MinShare"/> of the best, best first; equal scores in page order.
    /// </summary>
    private static List<int> Rank(string text, List<(int Start, int End)> paragraphs, HashSet<string> terms, int first)
    {
        var n = paragraphs.Count;
        var lengths = new int[n];
        var counts = new Dictionary<string, int>[n];
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var all = SearchTerms.Of(text[paragraphs[i].Start..paragraphs[i].End]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            lengths[i] = all.Length;
            counts[i] = all.Where(terms.Contains).GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var term in counts[i].Keys)
            {
                df[term] = df.GetValueOrDefault(term) + 1;
            }
        }

        var average = Math.Max(1.0, lengths.Average());
        var telling = Math.Max(1, (int)(n * TellingShare));
        var scored = new List<(int Index, double Score)>();
        for (var i = first; i < n; i++)
        {
            if (!counts[i].Keys.Any(t => df[t] <= telling))
            {
                continue;
            }

            var score = 0.0;
            foreach (var (term, f) in counts[i])
            {
                var idf = Math.Log(1 + ((n - df[term] + 0.5) / (df[term] + 0.5)));
                score += idf * f * (K1 + 1) / (f + (K1 * (1 - B + (B * lengths[i] / average))));
            }

            scored.Add((i, score));
        }

        if (scored.Count == 0)
        {
            return [];
        }

        var best = scored.Max(s => s.Score);
        return [.. scored.Where(s => s.Score >= best * MinShare).OrderByDescending(s => s.Score).ThenBy(s => s.Index).Select(s => s.Index)];
    }

    private static string Build(string text, int openingEnd, List<(int Start, int End)> paragraphs, SortedSet<int> chosen, IReadOnlyList<ResourceChunk> parts)
    {
        var excerpt = new StringBuilder(text[..openingEnd].TrimEnd());
        var at = openingEnd;
        foreach (var index in chosen)
        {
            var (start, end) = paragraphs[index];
            if (HasText(text, at, start))
            {
                excerpt.Append("\n\n").Append(Gap(PartAt(parts, start), parts.Count));
            }

            excerpt.Append("\n\n").Append(text, start, end - start);
            at = end;
        }

        if (HasText(text, at, text.Length))
        {
            excerpt.Append("\n\n").Append(Rest);
        }

        return excerpt.ToString();
    }

    private static bool HasText(string text, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The 1-based part that holds <paramref name="offset"/>.</summary>
    private static int PartAt(IReadOnlyList<ResourceChunk> parts, int offset)
    {
        for (var i = 0; i < parts.Count; i++)
        {
            if (offset < parts[i].End)
            {
                return i + 1;
            }
        }

        return parts.Count;
    }
}
