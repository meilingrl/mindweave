using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Mindweave.Desktop.Markdown;

/// <summary>
/// Shows a paragraph that is nothing but one display formula — <c>$$…$$</c> or <c>\[…\]</c> on a single line — as a
/// formula block, the way multi-line <c>$$</c> fences and <c>\[</c> blocks already are. Markdig parses the one-line
/// forms as inline math, which LiveMarkdown draws in text style inside the line: small and left-aligned. A display
/// delimiter inside a sentence stays inline. Runs on every parse of the shared pipeline, so finished answers,
/// streaming answers and summaries all get the same document; the source text and every source span are unchanged.
/// </summary>
public sealed class DisplayMathExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.DocumentProcessed -= Promote;
        pipeline.DocumentProcessed += Promote;
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }

    private static void Promote(MarkdownDocument document)
    {
        foreach (var paragraph in document.Descendants<ParagraphBlock>().ToList())
        {
            if (paragraph.Parent is { } parent && SoleDisplayFormula(paragraph) is { } math)
            {
                var block = new MathBlock(null!)
                {
                    FencedChar = math.Delimiter,
                    OpeningFencedCharCount = 2,
                    ClosingFencedCharCount = 2,
                    Line = paragraph.Line,
                    Column = paragraph.Column,
                    Span = paragraph.Span,
                    IsOpen = false,
                };
                var content = math.Content;
                block.AppendLine(ref content, paragraph.Column, paragraph.Line, content.Start, trackTrivia: false);
                var index = parent.IndexOf(paragraph);
                parent.RemoveAt(index);
                parent.Insert(index, block);
            }
        }
    }

    /// <summary>The paragraph's only content when it is a <c>$$…$$</c> or <c>\[…\]</c> formula, otherwise null.</summary>
    private static MathInline? SoleDisplayFormula(ParagraphBlock paragraph)
    {
        MathInline? formula = null;
        for (var inline = paragraph.Inline?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case MathInline math when formula is null && IsDisplay(math):
                    formula = math;
                    break;
                case LiteralInline literal when literal.Content.IsEmptyOrWhitespace():
                    break;
                default:
                    return null;
            }
        }

        return formula;
    }

    /// <summary>
    /// <c>$$</c> is display math; the backslash parser marks both <c>\(</c> and <c>\[</c> with two delimiters, so the
    /// bracket is read from the source.
    /// </summary>
    private static bool IsDisplay(MathInline math) => math.DelimiterCount == 2 && math.Delimiter switch
    {
        '$' => true,
        '\\' => math.Span.Start + 1 < math.Content.Text.Length && math.Content.Text[math.Span.Start + 1] == '[',
        _ => false,
    };
}
