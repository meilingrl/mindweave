using LiveMarkdown.Avalonia;

namespace Mindweave.Desktop.Markdown;

/// <summary>Process-wide LiveMarkdown configuration: the node types answers may contain beyond the built-in ones.</summary>
public static class MarkdownSetup
{
    private static int _registered;

    /// <summary>
    /// Registers LaTeX nodes (inline <c>$…$</c> and block <c>$$…$$</c>, rendered by RaTeX). Registration is global in
    /// LiveMarkdown, so this runs once per process however many applications are created (tests create several).
    /// </summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            MarkdownNode.Edit(builder => builder.Register<MathInlineNode>().Register<MathBlockNode>());
        }
    }
}
