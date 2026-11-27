using LiveMarkdown.Avalonia;
using Markdig.Syntax;

namespace Mindweave.Desktop.Markdown;

/// <summary>Stored text read by the pipeline the renderer draws with.</summary>
internal static class AnswerMarkdown
{
    /// <summary>
    /// The text's document. The parser refuses text nested more than about a hundred levels deep, which no reader writes but
    /// a shared file can hold (docs/design/sharing-import.md §4): that is shown as it was written.
    /// </summary>
    public static MarkdownDocument Parse(string markdown)
    {
        try
        {
            return Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline);
        }
        catch (ArgumentException)
        {
            // A fence longer than any in the text, so the text cannot end it.
            var fence = new string('`', Math.Max(3, 1 + LongestRun(markdown)));
            return Markdig.Markdown.Parse($"{fence}\n{markdown}\n{fence}", MarkdownUpdateProducer.DefaultPipeline);
        }
    }

    private static int LongestRun(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }
}
