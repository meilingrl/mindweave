using LiveMarkdown.Avalonia;

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
    /// </summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            MarkdownUpdateProducer.ConfigurePipeline += builder => builder.UseExtendedMathematics();
            MarkdownNode.Edit(builder => builder.Register<MathInlineNode>().Register<MathBlockNode>());
        }
    }
}
