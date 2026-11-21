using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig;
using Mindweave.Desktop.Markdown;
using RaTeX.Avalonia;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// How the shared Markdown pipeline draws emphasis and display formulas. Finished answers and summaries are parsed in
/// full, streaming answers through LiveMarkdown's live builder; both must look the same, so every case runs both ways.
/// </summary>
public class MarkdownRenderingTests
{
    public enum Feed
    {
        Full,
        Live,
    }

    private const string Emphasis = "火车的诞生不是一次发明，而是**蒸汽动力**与**轨道运输**两条技术线索的汇合。\n\n" +
        "1. **多管锅炉**：增加受热面积，使蒸汽产生得更快；\n2. **鼓风管**：用排出的废汽抽风。\n\n" +
        "Mixed **bold** and plain, 以及**中文**。";

    /// <summary>
    /// Bold must cover exactly the emphasised characters. The parse was always right (CommonMark: the closing
    /// <c>**</c> before <c>：</c> is right-flanking); the defect was in drawing, where CJK glyphs come from a fallback
    /// font and Avalonia 12.1.4 reused the previous run's fallback typeface, weight included, for the next run.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Feed.Full)]
    [InlineData(Feed.Live)]
    public async Task Bold_covers_exactly_the_emphasised_text_in_cjk_paragraphs_and_list_items(Feed feed)
    {
        var (window, renderer) = await RenderAsync(Emphasis, feed);

        var runs = DrawnWeights(renderer);
        Assert.True(IsBold(runs, "蒸汽动力"));
        Assert.True(IsBold(runs, "轨道运输"));
        Assert.False(IsBold(runs, "与"));
        Assert.False(IsBold(runs, "两条技术线索的汇合。"));
        Assert.True(IsBold(runs, "多管锅炉"));
        Assert.False(IsBold(runs, "：增加受热面积，使蒸汽产生得更快；"));
        Assert.True(IsBold(runs, "鼓风管"));
        Assert.False(IsBold(runs, "：用排出的废汽抽风。"));
        Assert.True(IsBold(runs, "bold"));
        Assert.False(IsBold(runs, " and plain, 以及"));
        Assert.True(IsBold(runs, "中文"));

        // Bold stops right after the emphasised word at the end of the mixed paragraph (its last "。", not the first).
        var end = runs.Text.LastIndexOf("中文。", StringComparison.Ordinal) + "中文".Length;
        Assert.Equal("。", runs.Text.Substring(end, 1));
        Assert.False(runs.Bold[end], "The full stop after **中文** is bold.");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(Feed.Full, "$$F = \\frac{0.85\\,p\\,d^2 s}{D}$$")]
    [InlineData(Feed.Live, "$$F = \\frac{0.85\\,p\\,d^2 s}{D}$$")]
    [InlineData(Feed.Full, "\\[\nF = \\frac{0.85\\,p\\,d^2 s}{D}\n\\]")]
    [InlineData(Feed.Live, "\\[F = \\frac{0.85\\,p\\,d^2 s}{D}\\]")]
    [InlineData(Feed.Full, "$$\nF = \\frac{0.85\\,p\\,d^2 s}{D}\n$$")]
    public async Task A_display_formula_is_a_centred_block_at_least_as_large_as_body_text(Feed feed, string formula)
    {
        var markdown = "估算起动牵引力：\n\n" + formula + "\n\n其中 $p$ 是锅炉压力。";
        var (window, renderer) = await RenderAsync(markdown, feed);

        var formulas = Formulas(renderer);
        Assert.Equal(2, formulas.Count);
        var display = formulas.Single(f => f.Classes.Contains("MathBlock"));
        var inline = formulas.Single(f => f.Classes.Contains("Math"));
        var body = SourceAnchors.Blocks(renderer).First(b => b.LayoutText.StartsWith("其中", StringComparison.Ordinal));

        var centre = display.TranslatePoint(new Point(display.Bounds.Width / 2, 0), renderer)!.Value.X;
        Assert.InRange(centre, (renderer.Bounds.Width / 2) - 2, (renderer.Bounds.Width / 2) + 2);
        Assert.True(display.Bounds.Width < renderer.Bounds.Width / 2, "The formula is sized to its content, not stretched.");
        Assert.True(display.FontSize >= body.FontSize, $"Display formula {display.FontSize} is smaller than body text {body.FontSize}.");
        Assert.True(display.Bounds.Height > body.Bounds.Height, "A fraction in display style is taller than one line of text.");

        // Inline formulas keep their place and size in the line.
        Assert.Equal(body.FontSize, inline.FontSize);
        Assert.Contains(body.GetVisualDescendants(), v => ReferenceEquals(v, inline));
        Assert.DoesNotContain(SourceAnchors.Blocks(renderer), b => b.LayoutText.Contains("\\frac", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(Feed.Full)]
    [InlineData(Feed.Live)]
    public async Task A_double_dollar_formula_inside_a_sentence_stays_in_the_line(Feed feed)
    {
        var (window, renderer) = await RenderAsync("能量 $$E = mc^2$$ 与质量等价。", feed);

        var formula = Assert.Single(Formulas(renderer));
        Assert.Contains("Math", formula.Classes);
        Assert.Equal("能量 \uFFFC 与质量等价。", Assert.Single(SourceAnchors.Blocks(renderer)).LayoutText);
        window.Close();
    }

    /// <summary>
    /// The extended mathematics parser alone already reads a one-line <c>\[…\]</c> paragraph as a formula block; only
    /// the one-line <c>$$…$$</c> needs <see cref="DisplayMathExtension"/>.
    /// </summary>
    [Fact]
    public void Only_the_one_line_double_dollar_form_needs_promoting()
    {
        MarkdownSetup.EnsureRegistered();
        var extended = new MarkdownPipelineBuilder().UseExtendedMathematics().Build();
        Assert.IsType<Markdig.Extensions.Mathematics.MathBlock>(Assert.Single(Markdig.Markdown.Parse("\\[F = \\frac{p}{D}\\]", extended)));
        Assert.IsType<Markdig.Syntax.ParagraphBlock>(Assert.Single(Markdig.Markdown.Parse("$$F = \\frac{p}{D}$$", extended)));
        Assert.IsType<Markdig.Extensions.Mathematics.MathBlock>(Assert.Single(Markdig.Markdown.Parse("$$F = \\frac{p}{D}$$", MarkdownUpdateProducer.DefaultPipeline)));
    }

    /// <summary>
    /// List numbers and bullets are drawn in the same face as the answer text beside them, so digits in a marker and in
    /// the item match. Code blocks keep their own monospaced face.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Feed.Full)]
    [InlineData(Feed.Live)]
    public async Task List_markers_use_the_answer_font(Feed feed)
    {
        var (window, renderer) = await RenderAsync("1. **多管锅炉**：1804 年\n2. 鼓风管\n\n- 无序 12 项\n\n```\ncode 1\n```", feed);
        var answerFont = Assert.IsType<FontFamily>(Avalonia.Application.Current!.FindResource("AnswerFontFamily"));

        var texts = renderer.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible && !t.GetVisualAncestors().OfType<CodeBlock>().Any()).ToList();
        Assert.Contains(texts, t => t is not MarkdownTextBlock);
        Assert.All(texts, t => Assert.Equal(answerFont, t.FontFamily));
        window.Close();
    }

    /// <summary>
    /// Body text (paragraphs, list items, quotes) is set at the answer line height, wider than the font's own; headings
    /// and code keep their own spacing. List markers share it, so each sits on the first line of its item.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Feed.Full)]
    [InlineData(Feed.Live)]
    public async Task Body_text_and_list_markers_use_the_answer_line_height(Feed feed)
    {
        // Two one-item lists, so each marker has exactly one item beside it.
        var (window, renderer) = await RenderAsync("## 标题\n\n正文第一段。\n\n1. 第一项\n\n- 无序项\n\n> 引用\n\n```\ncode 1\ncode 2\n```", feed);
        var lineHeight = Assert.IsType<double>(Avalonia.Application.Current!.FindResource("AnswerLineHeight"));
        var blocks = renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Where(b => b.IsVisible).ToList();

        var body = blocks.Where(b => b.Classes.Contains("ParagraphBlock")).ToList();
        Assert.Equal(4, body.Count);
        Assert.All(body, b => Assert.Equal(lineHeight, b.TextLayout.TextLines[0].Height, 0.5));

        Assert.True(double.IsNaN(Assert.Single(blocks, b => b.Classes.Contains("Heading2")).LineHeight));
        var code = blocks.Where(b => b.GetVisualAncestors().OfType<CodeBlock>().Any()).ToList();
        Assert.NotEmpty(code);
        Assert.All(code, b => Assert.True(double.IsNaN(b.LineHeight)));

        var markers = renderer.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible && (t.Classes.Contains("ListBlockNumber") || t.Classes.Contains("ListBlockBullet"))).ToList();
        Assert.Equal(2, markers.Count);
        foreach (var marker in markers)
        {
            var list = marker.GetVisualAncestors().OfType<MarkdownListGrid>().First();
            var item = Assert.Single(body, b => b.GetVisualAncestors().Contains(list));
            Assert.Equal(Baseline(item, window), Baseline(marker, window), 1.0);
        }

        window.Close();
    }

    private static double Baseline(TextBlock block, Visual relativeTo) =>
        block.TranslatePoint(new Point(0, block.TextLayout.TextLines[0].Baseline), relativeTo)!.Value.Y;

    /// <summary>
    /// A display formula block between the first and last selected text blocks is part of what the reader selected,
    /// so the branch quote carries its LaTeX source, delimiters included, in reading order (as inline formulas do).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Feed.Full, "$$F = \\frac{p d^2 s}{D}$$")]
    [InlineData(Feed.Live, "$$F = \\frac{p d^2 s}{D}$$")]
    [InlineData(Feed.Full, "$$\nF = \\frac{p d^2 s}{D}\n$$")]
    [InlineData(Feed.Full, "\\[\nF = \\frac{p d^2 s}{D}\n\\]")]
    [InlineData(Feed.Live, "\\[F = \\frac{p d^2 s}{D}\\]")]
    public async Task A_selection_across_a_display_formula_quotes_the_formula(Feed feed, string formula)
    {
        var markdown = "估算起动牵引力：\n\n" + formula + "\n\n其中 $p$ 是锅炉压力。\n\n结尾。";
        var (window, renderer) = await RenderAsync(markdown, feed);
        var blocks = SourceAnchors.Blocks(renderer);
        Assert.Equal(3, blocks.Count);

        // The whole text before and after the formula.
        blocks[0].SelectionStart = 0;
        blocks[0].SelectionEnd = blocks[0].LayoutText.Length;
        blocks[1].SelectionStart = 0;
        blocks[1].SelectionEnd = blocks[1].LayoutText.Length;
        Assert.Equal("估算起动牵引力：\n" + formula + "\n其中 $p$ 是锅炉压力。", SourceAnchors.QuoteOf(SourceAnchors.Capture(markdown, renderer)));

        // Only the end of the text before and the start of the text after: the formula still sits between them.
        blocks[0].SelectionStart = 4;
        blocks[1].SelectionEnd = 2;
        var partial = SourceAnchors.Capture(markdown, renderer);
        Assert.Equal("牵引力：\n" + formula + "\n其中", SourceAnchors.QuoteOf(partial));
        Assert.Equal(2, SourceAnchors.Restore(partial, markdown, renderer, Brushes.Yellow).Count);

        // A selection that does not reach past the formula does not quote it.
        blocks[1].SelectionEnd = 0;
        blocks[1].SelectionStart = 0;
        Assert.Equal("牵引力：", SourceAnchors.QuoteOf(SourceAnchors.Capture(markdown, renderer)));
        blocks[0].SelectionStart = blocks[0].SelectionEnd = 0;
        blocks[1].SelectionStart = 0;
        blocks[1].SelectionEnd = blocks[1].LayoutText.Length;
        blocks[2].SelectionStart = 0;
        blocks[2].SelectionEnd = 2;
        Assert.Equal("其中 $p$ 是锅炉压力。\n结尾", SourceAnchors.QuoteOf(SourceAnchors.Capture(markdown, renderer)));
        window.Close();
    }

    /// <summary>
    /// Render profile math-3 draws a paragraph holding only a one-line display formula as a formula block. Anchors
    /// captured under math-2 (the same parse without that step) restore in the text around it, and are refused,
    /// not guessed, when they covered the formula paragraph itself.
    /// </summary>
    [AvaloniaFact]
    public void Math_2_anchors_restore_around_a_promoted_display_formula_and_are_refused_on_it()
    {
        const string markdown = "估算起动牵引力：\n\n$$F = \\frac{p d^2 s}{D}$$\n\n其中 $p$ 是锅炉压力。";
        MarkdownSetup.EnsureRegistered();
        var math2 = new MarkdownPipelineBuilder().UseExtendedMathematics().Build();
        var (oldWindow, old) = RenderFull(Markdig.Markdown.Parse(markdown, math2));
        var (newWindow, current) = RenderFull(Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline));
        Assert.Equal(3, SourceAnchors.Blocks(old).Count);
        Assert.Equal(2, SourceAnchors.Blocks(current).Count);

        var after = SourceAnchors.Blocks(old)[2];
        after.SelectionStart = 0;
        after.SelectionEnd = after.LayoutText.Length;
        var aroundFormula = SourceAnchors.Capture(markdown, old);
        var restored = Assert.Single(SourceAnchors.Restore(aroundFormula, markdown, current, Brushes.Yellow));
        Assert.StartsWith("其中", restored.Block.LayoutText, StringComparison.Ordinal);

        after.SelectionEnd = 0;
        var formula = SourceAnchors.Blocks(old)[1];
        formula.SelectionStart = 0;
        formula.SelectionEnd = formula.LayoutText.Length;
        var onFormula = SourceAnchors.Capture(markdown, old);
        Assert.Throws<InvalidDataException>(() => SourceAnchors.Restore(onFormula, markdown, current, Brushes.Yellow));
        Assert.Contains("livemarkdown-2.5.1/math-2", SourceAnchors.CompatibleProfiles);
        oldWindow.Close();
        newWindow.Close();
    }

    private static (Window Window, MarkdownRenderer Renderer) RenderFull(Markdig.Syntax.MarkdownDocument document)
    {
        var renderer = new MarkdownRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(document) };
        var window = new Window { Content = renderer, Width = 800, Height = 600 };
        window.Show();
        window.UpdateLayout();
        return (window, renderer);
    }

    private static async Task<(Window Window, MarkdownRenderer Renderer)> RenderAsync(string markdown, Feed feed)
    {
        MarkdownSetup.EnsureRegistered();
        var renderer = new MarkdownRenderer();
        var window = new Window { Content = new Border { Padding = new Thickness(20), Child = renderer }, Width = 800, Height = 600 };
        window.Show();
        if (feed == Feed.Full)
        {
            renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline));
        }
        else
        {
            // Streaming appends a piece at a time and the builder re-parses after each.
            var live = new ObservableStringBuilder();
            renderer.MarkdownBuilder = live;
            for (var i = 0; i < markdown.Length; i += 7)
            {
                live.Append(markdown.Substring(i, Math.Min(7, markdown.Length - i)));
                await UiWait.UntilAsync(() => renderer.DocumentUpdate?.Version == live.Version);
            }
        }

        window.UpdateLayout();
        return (window, renderer);
    }

    private static List<FormulaControl> Formulas(Visual renderer) =>
        renderer.GetVisualDescendants().OfType<FormulaControl>().Where(v => v.IsVisible).ToList();

    /// <summary>
    /// The displayed text of all blocks, and for each character whether it is drawn bold: its run asks for a bold
    /// weight and gets it, from a bold face or emboldened when the font has none.
    /// </summary>
    private static (string Text, bool[] Bold) DrawnWeights(Visual renderer)
    {
        var text = new System.Text.StringBuilder();
        var bold = new List<bool>();
        foreach (var block in SourceAnchors.Blocks(renderer))
        {
            foreach (var run in block.TextLayout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>())
            {
                var glyphs = run.GlyphRun.GlyphTypeface;
                var drawnBold = run.Properties.Typeface.Weight >= FontWeight.SemiBold &&
                    (glyphs.Weight >= FontWeight.SemiBold || glyphs.FontSimulations.HasFlag(FontSimulations.Bold));
                text.Append(run.Text.Span);
                bold.AddRange(Enumerable.Repeat(drawnBold, run.Length));
            }

            text.Append('\n');
            bold.Add(false);
        }

        return (text.ToString(), bold.ToArray());
    }

    /// <summary>True when every character of the first occurrence of <paramref name="part"/> is bold, false when none is.</summary>
    private static bool IsBold((string Text, bool[] Bold) drawn, string part)
    {
        var at = drawn.Text.IndexOf(part, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{part}' is not displayed.");
        var weights = drawn.Bold.AsSpan(at, part.Length).ToArray().Distinct().ToArray();
        Assert.True(weights.Length == 1, $"'{part}' is partly bold.");
        return weights[0];
    }
}
