using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Mindweave.Desktop.Markdown;

/// <summary>
/// Turns every Markdown image into a plain link that shows its alt text (or, without one, its address), so nothing is
/// ever loaded (health check 2026-11-12). LiveMarkdown fetches an image the moment it is drawn — over HTTP, following
/// redirects, or from a local file — so text from the model (or from content the user pasted) could make the
/// application send conversation text to any server inside an image address, while streaming and on every reopening.
/// Images are outside the first version (architecture §3); link clicks are already ignored. Runs on every parse of the
/// shared pipeline, so finished answers, streaming answers and summaries all get the same document.
/// </summary>
public sealed class NoImagesExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.DocumentProcessed -= Disarm;
        pipeline.DocumentProcessed += Disarm;
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }

    private static void Disarm(MarkdownDocument document)
    {
        foreach (var image in document.Descendants<LinkInline>().Where(l => l.IsImage).ToList())
        {
            image.IsImage = false;
            if (image.FirstChild is null && !string.IsNullOrEmpty(image.Url))
            {
                image.AppendChild(new LiteralInline(image.Url) { Span = image.Span, Line = image.Line, Column = image.Column });
            }
        }
    }
}
