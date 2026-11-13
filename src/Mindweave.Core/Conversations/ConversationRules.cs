using System.Globalization;
using System.Text;

namespace Mindweave.Core.Conversations;

/// <summary>Pure rules over a conversation's ordered messages.</summary>
public static partial class ConversationRules
{
    public const int FallbackTitleLength = 30;
    public const int MaxTitleLength = 60;

    private static readonly string[] TitlePrefixes = ["标题：", "标题:", "Title:", "Title："];
    private static readonly char[] TitleTrailing = ['。', '.', '！', '!', '？', '?', '：', ':', '；', ';', '，', ','];

    /// <summary>
    /// Messages sent as history: every user question, followed by the latest attempt at answering it,
    /// if that attempt is usable. Superseded attempts stay readable but are never sent.
    /// </summary>
    public static IReadOnlyList<Message> SelectContext(IReadOnlyList<Message> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var result = new List<Message>(ordered.Count);
        Message? latestAnswer = null;
        foreach (var message in ordered)
        {
            if (message.Role == MessageRole.User)
            {
                Flush();
                result.Add(message);
            }
            else
            {
                latestAnswer = message;
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (latestAnswer is { CountsAsContext: true })
            {
                result.Add(latestAnswer);
            }

            latestAnswer = null;
        }
    }

    /// <summary>The newest answer, which is the only one that may be retried or regenerated (U4).</summary>
    public static Message? LastAnswer(IReadOnlyList<Message> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        return ordered.Count > 0 && ordered[^1].Role == MessageRole.Assistant ? ordered[^1] : null;
    }

    public static bool CanRetry(IReadOnlyList<Message> ordered, MessageId answer) =>
        LastAnswer(ordered) is { IsFinished: true } last && last.Id == answer;

    public static bool HasUnfinishedAnswer(IReadOnlyList<Message> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        return ordered.Any(m => m.Role == MessageRole.Assistant && !m.IsFinished);
    }

    /// <summary>Shown until an automatic or manual title exists; never stored.</summary>
    public static string FallbackTitle(string firstQuestion) => Shorten(firstQuestion, FallbackTitleLength);

    /// <summary>Cleans a generated title: one line, no wrapping quotes or trailing punctuation, bounded length.</summary>
    /// <summary>
    /// Removes reasoning a model wrote into its text (<c>&lt;think&gt;…&lt;/think&gt;</c>, as some reasoning models do when
    /// served without a separate reasoning field) from text the program uses itself: titles, names, summaries (health check 2026-11-12). A reply
    /// that starts reasoning and never closes it has no usable text.
    /// </summary>
    public static string WithoutInlineReasoning(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleaned = InlineReasoning().Replace(text, string.Empty).Trim();
        return UnclosedReasoning().IsMatch(cleaned) ? string.Empty : cleaned;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<(think|thinking)>[\s\S]*?</\1>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex InlineReasoning();

    [System.Text.RegularExpressions.GeneratedRegex(@"^<(think|thinking)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UnclosedReasoning();

    public static string? CleanGeneratedTitle(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        foreach (var prefix in TitlePrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                line = line[prefix.Length..];
                break;
            }
        }

        // Headings, wrappers and trailing punctuation can nest in any order ("**“标题”**。"): strip until stable.
        string previous;
        do
        {
            previous = line;
            line = Text.EnclosingMarks.Strip(line.Trim().TrimStart('#').Trim().TrimEnd(TitleTrailing));
        }
        while (line != previous);

        line = Shorten(line, MaxTitleLength);
        return line.Length == 0 ? null : line;
    }

    private static bool IsInvisibleControl(char ch) =>
        ch != '\u200D' && CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.Control or UnicodeCategory.Format;

    /// <summary>
    /// At most <paramref name="maxTextElements"/> whole characters as the reader sees them (low finding
    /// gap-summary-10): cutting by UTF-16 index could end on half of an emoji or a combined character.
    /// </summary>
    public static string FirstTextElements(string text, int maxTextElements)
    {
        ArgumentNullException.ThrowIfNull(text);
        var info = new StringInfo(text);
        return info.LengthInTextElements <= maxTextElements ? text : info.SubstringByTextElements(0, maxTextElements);
    }

    public static string Shorten(string text, int maxTextElements)
    {
        ArgumentNullException.ThrowIfNull(text);
        var collapsed = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            // Invisible control and direction characters a model may emit (low finding gap-unprompted-9): a right-to-left
            // override would show a title backwards. The joiner inside emoji sequences stays.
            if (IsInvisibleControl(ch))
            {
                continue;
            }

            if (space && collapsed.Length > 0)
            {
                collapsed.Append(' ');
            }

            space = false;
            collapsed.Append(ch);
        }

        var value = collapsed.ToString();
        var info = new StringInfo(value);
        return info.LengthInTextElements <= maxTextElements ? value : info.SubstringByTextElements(0, maxTextElements) + "…";
    }
}
