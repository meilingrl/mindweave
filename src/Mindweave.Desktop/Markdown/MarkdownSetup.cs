using LiveMarkdown.Avalonia;
using Markdig;

namespace Mindweave.Desktop.Markdown;

/// <summary>Process-wide LiveMarkdown configuration: the node types answers may contain beyond the built-in ones.</summary>
public static class MarkdownSetup
{
    private static int _registered;

    /// <summary>
    /// Registers LaTeX nodes rendered by RaTeX, and the parser for every common delimiter: <c>$…$</c>, <c>$$…$$</c>,
    /// <c>\(…\)</c> and <c>\[…\]</c>. Models often use the backslash forms, which plain Markdown shows as source. Both
    /// settings are global in LiveMarkdown and the parser must be configured before the shared pipeline is first
    /// built, so this runs once per process, at startup, however many applications are created (tests create several).
    /// A paragraph that holds only a one-line display formula becomes a formula block (<see cref="DisplayMathExtension"/>);
    /// an image becomes a link to it and is never loaded (<see cref="NoImagesExtension"/>). Emphasis follows Markdig's
    /// CJK-friendly rule, so <c>的**“火箭号”**在</c> is bold: CommonMark does not let <c>**</c> open between a letter and
    /// punctuation, or close between punctuation and a letter, which models write often in Chinese. Text without CJK
    /// characters parses as plain CommonMark.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            MarkdownUpdateProducer.ConfigurePipeline += builder => builder.UseExtendedMathematics().UseCjkFriendlyEmphasis().Use<DisplayMathExtension>().Use<NoImagesExtension>();
            MarkdownNode.Edit(builder => builder.Register<MathInlineNode>().Register<MathBlockNode>());
        }
    }
}
