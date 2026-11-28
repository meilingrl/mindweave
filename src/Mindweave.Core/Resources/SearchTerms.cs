using System.Globalization;
using System.Text;

namespace Mindweave.Core.Resources;

/// <summary>
/// Terms for local keyword retrieval over chunks (architecture §15.4): words of letters and digits, lower-cased, and runs of
/// CJK characters as overlapping character pairs ("机器学习" → "机器 器学 学习"), since CJK text has no spaces. The index stores
/// <see cref="Of"/> of each chunk; a question becomes <see cref="Query"/> of the same terms, any of which may match.
/// </summary>
public static class SearchTerms
{
    /// <summary>At most this many distinct terms of a question are searched for.</summary>
    public const int MaxQueryTerms = 64;

    public static string Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Join(' ', Terms(text));
    }

    /// <summary>An FTS5 query matching chunks with any term of <paramref name="question"/>, or null when it has none.</summary>
    public static string? Query(string question)
    {
        var terms = QueryTerms(question);
        return terms.Count == 0 ? null : string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
    }

    /// <summary>
    /// The distinct terms of <paramref name="question"/> that are searched for, at most <see cref="MaxQueryTerms"/>: a lone CJK
    /// character (a run of one) is left out, as it matches too much.
    /// </summary>
    public static IReadOnlyList<string> QueryTerms(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return Terms(question).Where(t => t.Length > 1 || !IsCjk(t[0])).Distinct(StringComparer.Ordinal).Take(MaxQueryTerms).ToList();
    }

    private static IEnumerable<string> Terms(string text)
    {
        var word = new StringBuilder();
        var cjk = new List<string>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjk(rune))
            {
                foreach (var t in Flush(word))
                {
                    yield return t;
                }

                cjk.Add(rune.ToString());
                continue;
            }

            foreach (var t in Pairs(cjk))
            {
                yield return t;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                word.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else
            {
                foreach (var t in Flush(word))
                {
                    yield return t;
                }
            }
        }

        foreach (var t in Flush(word))
        {
            yield return t;
        }

        foreach (var t in Pairs(cjk))
        {
            yield return t;
        }
    }

    private static List<string> Flush(StringBuilder word)
    {
        if (word.Length == 0)
        {
            return [];
        }

        var term = word.ToString();
        word.Clear();
        return [term];
    }

    private static List<string> Pairs(List<string> run)
    {
        var pairs = run.Count switch
        {
            0 => [],
            1 => [run[0]],
            _ => Enumerable.Range(0, run.Count - 1).Select(i => run[i] + run[i + 1]).ToList(),
        };
        run.Clear();
        return pairs;
    }

    private static bool IsCjk(char c) => IsCjk(new Rune(c));

    private static bool IsCjk(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category != UnicodeCategory.OtherLetter)
        {
            return false;
        }

        var v = rune.Value;
        return v is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7AF)
            or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x2FA1F);
    }
}
