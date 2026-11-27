using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mindweave.Infrastructure.Tests.RunEval;

/// <summary>
/// The mechanical checks on what a model wrote (docs/design/reply-quality.md Q10): whether a branch answer restates the answer
/// it branched from, whether a summary opens with a gist and lists key terms. They run with the fake model too, so they are
/// plain text rules: rough, and only ever reported.
/// </summary>
internal static partial class EvalText
{
    /// <summary>Runs of this many letters count as copied when the parent answer has them too.</summary>
    public const int Run = 12;

    /// <summary>A branch answer whose share of copied runs reaches this restates its parent.</summary>
    public const double RestatedLimit = 0.2;

    /// <summary>
    /// The share of <paramref name="answer"/>'s runs of <see cref="Run"/> letters (spaces and Markdown marks left out) that
    /// <paramref name="parent"/> has too, apart from those of the selected <paramref name="quote"/>: an answer may quote the
    /// passage it explains. 0 for an answer shorter than one run.
    /// </summary>
    public static double RestatedShare(string parent, string quote, string answer)
    {
        var own = Runs(Letters(answer));
        if (own.Count == 0)
        {
            return 0;
        }

        var quoted = Runs(Letters(quote)).ToHashSet(StringComparer.Ordinal);
        var earlier = Runs(Letters(parent)).Where(r => !quoted.Contains(r)).ToHashSet(StringComparer.Ordinal);
        return (double)own.Count(earlier.Contains) / own.Count;
    }

    /// <summary>The text's sentences, each with what ends it; a line break ends one too.</summary>
    public static IReadOnlyList<string> Sentences(string text) =>
        [.. SentenceEnd().Split(text.ReplaceLineEndings("\n")).Select(s => s.Trim()).Where(s => s.Length > 0)];

    /// <summary>
    /// The first block of a summary body and how many sentences it has; 0 when it is not a paragraph (a heading, a list, a
    /// quote, a table) — then there is no gist.
    /// </summary>
    public static (string Block, int Sentences) Gist(string body)
    {
        var block = body.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        if (block.Length == 0 || NotParagraph().IsMatch(block))
        {
            return (block, 0);
        }

        return (block, Sentences(block).Count);
    }

    /// <summary>The number of items in the summary's list of key terms (under a heading or bold line that names them); 0 without one.</summary>
    public static int KeyTerms(string body)
    {
        var lines = body.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).ToList();
        var heading = lines.FindIndex(l => TermsHeading().IsMatch(l));
        if (heading < 0)
        {
            return 0;
        }

        return lines.Skip(heading + 1).SkipWhile(l => l.Length == 0).TakeWhile(l => ListItem().IsMatch(l)).Count();
    }

    private static string Letters(string text)
    {
        var s = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                s.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
        }

        return s.ToString();
    }

    private static List<string> Runs(string letters) =>
        letters.Length < Run ? [] : [.. Enumerable.Range(0, letters.Length - Run + 1).Select(i => letters.Substring(i, Run))];

    /// <summary>After 。！？ or after . ! ? that a space or the end follows; or a line break.</summary>
    [GeneratedRegex(@"(?<=[。！？])|(?<=[.!?])(?=\s|$)|\n")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"^(#|[-*+>|]\s|\d+[.、)]\s?)")]
    private static partial Regex NotParagraph();

    [GeneratedRegex(@"^(#{1,6}\s*|\*\*)?\s*(关键术语|术语|名词解释|key terms|terms)\s*[：:]?\s*(\*\*)?\s*[：:]?$", RegexOptions.IgnoreCase)]
    private static partial Regex TermsHeading();

    [GeneratedRegex(@"^([-*+]\s|\d+[.、)]\s?)")]
    private static partial Regex ListItem();
}
