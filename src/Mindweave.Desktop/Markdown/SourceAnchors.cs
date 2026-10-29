using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;

namespace Mindweave.Desktop.Markdown;

/// <summary>One rendered text block's share of a selection (format 1, verified in prototypes/livemarkdown-chat).</summary>
/// <param name="SourceStart">Start of the whole Markdown source block the control renders (inclusive).</param>
/// <param name="SourceEnd">End of that source block (inclusive).</param>
/// <param name="Ordinal">Index among visible controls with the same source block.</param>
/// <param name="LayoutHash">Hash of the control's displayed text.</param>
/// <param name="Start">Selection start in the displayed text (UTF-16).</param>
/// <param name="Length">Selection length in the displayed text.</param>
/// <param name="Quote">The selected displayed text.</param>
/// <param name="ExactRawStart">Raw Markdown offset, only when the source block and its displayed text are identical.</param>
/// <param name="Readable">
/// The quote with each formula (shown as one object character in the layout) replaced by its LaTeX source; null when
/// the selection has no formula. <paramref name="Quote"/> stays the displayed text, which restoring checks against.
/// </param>
/// <param name="FormulasAfter">
/// The LaTeX source, delimiters included, of the display formula blocks between this segment and the next, one per
/// line; null when there are none. Formula blocks are not text blocks and cannot hold a selection themselves, but a
/// selection running across one includes it. Only quoting uses it; restoring ignores it.
/// </param>
public sealed record AnchorSegment(int SourceStart, int SourceEnd, int Ordinal, string LayoutHash, int Start, int Length, string Quote, int? ExactRawStart = null, string? Readable = null, string? FormulasAfter = null);

/// <summary>A selection inside one Markdown text, checked against a prefix hash of the text it was captured from.</summary>
public sealed record SelectionAnchor(int Version, int SourceLength, string SourceHash, IReadOnlyList<AnchorSegment> Segments);

/// <summary>
/// A reading position inside one message: a character of a source block's control and how far the reading line was
/// below that character's top. <see cref="SourceStart"/> is -1 when the position is the message itself (a question,
/// or an answer whose text was not under the reading line); <see cref="Within"/> is then measured from the message top.
/// </summary>
public sealed record ReadingLocator(int SourceStart, int SourceEnd, int Ordinal, int Character, double Within)
{
    public static ReadingLocator MessageTop(double within) => new(-1, -1, 0, 0, within);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMessageTop => SourceStart < 0;
}

/// <summary>A validated selection segment bound to the control that shows it now.</summary>
public sealed record ResolvedSegment(MarkdownTextBlock Block, int Start, int Length);

/// <summary>
/// The source adapter of architecture §5: turns native LiveMarkdown selections into versioned anchors and back,
/// and refuses rather than guesses. Uses public API only; it never searches the text for a similar passage.
/// </summary>
public static class SourceAnchors
{
    public const int FormatVersion = 1;
    public const int LocatorVersion = 1;

    /// <summary>Identity of the rendering setup anchors are valid for; a different profile may lay text out differently.</summary>
    public const string RenderProfile = "livemarkdown-2.5.1/math-3";

    /// <summary>
    /// Profiles whose anchors are still worth trying. Before LaTeX rendering (and, in math-1, for backslash
    /// delimiters), formulas were shown as source text; answers without them lay out the same, and those with them
    /// fail the layout check and fall back. Since math-3 a paragraph holding only a one-line display formula is drawn
    /// as a formula block, not as a text block (<see cref="DisplayMathExtension"/>); every other text block keeps its
    /// source span, ordinal and displayed text, so math-2 anchors still restore unless they cover such a paragraph.
    /// </summary>
    public static readonly IReadOnlySet<string> CompatibleProfiles = new HashSet<string>(StringComparer.Ordinal)
    {
        RenderProfile,
        "livemarkdown-2.5.1/math-2",
        "livemarkdown-2.5.1/math-1",
        "livemarkdown-2.5.1/default-pipeline",
    };

    /// <summary>How a formula appears in a text block's layout text.</summary>
    private const char FormulaPlaceholder = '\uFFFC';

    private static readonly System.Text.RegularExpressions.Regex Formula =
        new(@"\$\$(?<tex>.+?)\$\$|\$(?<tex>[^$\n]+?)\$|\\\[(?<tex>.+?)\\\]|\\\((?<tex>.+?)\\\)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private const string HighlightKey = "branch-source";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(SelectionAnchor anchor) => JsonSerializer.Serialize(anchor, Json);

    public static SelectionAnchor Deserialize(string json) =>
        JsonSerializer.Deserialize<SelectionAnchor>(json, Json) ?? throw new InvalidDataException("The anchor is empty.");

    public static string Serialize(ReadingLocator locator) => JsonSerializer.Serialize(locator, Json);

    public static ReadingLocator? DeserializeLocator(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReadingLocator>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Visible text controls of a renderer, in document order.</summary>
    public static IReadOnlyList<MarkdownTextBlock> Blocks(Visual renderer) => renderer.GetVisualDescendants()
        .OfType<MarkdownTextBlock>().Where(x => x.IsVisible && x.GetVisualAncestors().All(a => a is not Visual v || v.IsVisible)).ToArray();

    public static bool HasSelection(Visual renderer) => Blocks(renderer).Any(b => b.SelectionStart != b.SelectionEnd);

    /// <summary>Captures the current selection. Throws <see cref="InvalidOperationException"/> when nothing is selected.</summary>
    public static SelectionAnchor Capture(string markdown, Visual renderer)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var segments = new List<AnchorSegment>();
        var ordinals = new Dictionary<(int, int), int>();
        foreach (var block in Blocks(renderer))
        {
            var span = block.SourceSpan;
            var key = (span.Start, span.End);
            var ordinal = ordinals.GetValueOrDefault(key);
            ordinals[key] = ordinal + 1;
            var start = Math.Min(block.SelectionStart, block.SelectionEnd);
            var end = Math.Max(block.SelectionStart, block.SelectionEnd);
            if (start == end)
            {
                continue;
            }

            if (span.Start < 0 || span.End < span.Start || span.End >= markdown.Length || start < 0 || end > block.LayoutText.Length)
            {
                throw new InvalidDataException("The control's source or selection is incomplete; refusing to guess.");
            }

            var quote = block.LayoutText[start..end];
            var rawBlock = markdown[span.Start..(span.End + 1)];

            // A raw character offset is only asserted when source and displayed block are identical.
            int? exact = rawBlock == block.LayoutText ? span.Start + start : null;
            segments.Add(new(span.Start, span.End, ordinal, Hash(block.LayoutText), start, end - start, quote, exact, Readable(quote, block.LayoutText[..start], rawBlock)));
        }

        if (segments.Count == 0)
        {
            throw new InvalidOperationException("Nothing is selected.");
        }

        return new(FormatVersion, markdown.Length, Hash(markdown), WithFormulaBlocks(markdown, segments));
    }

    /// <summary>The selected text as the reader saw it, one line per text block and per display formula between them.</summary>
    public static string QuoteOf(SelectionAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return string.Join('\n', anchor.Segments.Select(s => s.FormulasAfter is null ? s.Readable ?? s.Quote : $"{s.Readable ?? s.Quote}\n{s.FormulasAfter}")).Trim();
    }

    /// <summary>
    /// Attaches each display formula block lying between the first and last selected text blocks to the segment just
    /// before it, matched by source span in the shared pipeline's parse of the same text (the parse the renderer
    /// shows), so the quote keeps them in reading order, as <see cref="Readable"/> does for inline formulas.
    /// </summary>
    private static List<AnchorSegment> WithFormulaBlocks(string markdown, List<AnchorSegment> segments)
    {
        var first = segments[0].SourceEnd;
        var last = segments[^1].SourceStart;
        if (last <= first)
        {
            return segments;
        }

        var formulas = Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline).Descendants<MathBlock>()
            .Where(f => f.Span.Start > first && f.Span.End < last && f.Span.End < markdown.Length)
            .OrderBy(f => f.Span.Start);
        foreach (var formula in formulas)
        {
            var index = segments.FindLastIndex(s => s.SourceEnd < formula.Span.Start);
            var source = markdown[formula.Span.Start..(formula.Span.End + 1)].Trim();
            var before = segments[index];
            segments[index] = before with { FormulasAfter = before.FormulasAfter is null ? source : $"{before.FormulasAfter}\n{source}" };
        }

        return segments;
    }

    /// <summary>
    /// The quote with formulas restored: the n-th placeholder in the block is the n-th formula in its source. Null when
    /// the quote has no formula; a placeholder without a matching formula stays as it is rather than being guessed.
    /// </summary>
    private static string? Readable(string quote, string before, string rawBlock)
    {
        if (!quote.Contains(FormulaPlaceholder, StringComparison.Ordinal))
        {
            return null;
        }

        var formulas = Formula.Matches(rawBlock).Select(m => m.Value).ToList();
        var index = before.Count(c => c == FormulaPlaceholder);
        var readable = new StringBuilder(quote.Length);
        foreach (var c in quote)
        {
            readable.Append(c == FormulaPlaceholder && index < formulas.Count ? formulas[index++] : c.ToString());
        }

        return readable.ToString();
    }

    /// <summary>
    /// Validates every segment against the current text and layout, then — and only then — selects and highlights
    /// it. Throws <see cref="InvalidDataException"/> on any mismatch; a failed restore changes nothing.
    /// </summary>
    public static IReadOnlyList<ResolvedSegment> Restore(SelectionAnchor anchor, string markdown, Visual renderer, IBrush highlight)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(markdown);
        if (anchor.Version != FormatVersion || anchor.SourceLength < 0 || markdown.Length < anchor.SourceLength ||
            Hash(markdown[..anchor.SourceLength]) != anchor.SourceHash || anchor.Segments is not { Count: > 0 })
        {
            throw new InvalidDataException("The text the anchor was captured from has changed.");
        }

        var blocks = Blocks(renderer);
        var resolved = new List<ResolvedSegment>();
        foreach (var segment in anchor.Segments)
        {
            var matches = blocks.Where(x => x.SourceSpan.Start == segment.SourceStart && x.SourceSpan.End == segment.SourceEnd).ToArray();
            if (segment.Ordinal < 0 || segment.Ordinal >= matches.Length)
            {
                throw new InvalidDataException("The source block is no longer laid out the same way.");
            }

            var block = matches[segment.Ordinal];
            if (Hash(block.LayoutText) != segment.LayoutHash || segment.Start < 0 || segment.Length <= 0 ||
                segment.Start > block.LayoutText.Length - segment.Length || block.LayoutText.Substring(segment.Start, segment.Length) != segment.Quote)
            {
                throw new InvalidDataException("The displayed text or the selected range has changed.");
            }

            if (segment.ExactRawStart is { } rawStart)
            {
                var rawBlock = markdown[segment.SourceStart..(segment.SourceEnd + 1)];
                if (rawBlock != block.LayoutText || rawStart != segment.SourceStart + segment.Start ||
                    rawStart < 0 || rawStart > markdown.Length - segment.Length || markdown.Substring(rawStart, segment.Length) != segment.Quote)
                {
                    throw new InvalidDataException("The exact source range disagrees with the displayed selection.");
                }
            }

            resolved.Add(new(block, segment.Start, segment.Length));
        }

        ClearHighlight(renderer);
        foreach (var part in resolved)
        {
            var styles = part.Block.HighlightStyles ?? new TextHighlightStyles();
            styles.Set(HighlightKey, new TextHighlightStyle { Background = highlight, CornerRadius = new CornerRadius(3) });
            part.Block.HighlightStyles = styles;
            part.Block.Highlights.Set(HighlightKey, [new TextHighlightRange(part.Start, part.Length)], 20);
        }

        return resolved;
    }

    public static void ClearHighlight(Visual renderer)
    {
        foreach (var block in Blocks(renderer))
        {
            block.Highlights.Remove(HighlightKey);
        }
    }

    /// <summary>The vertical position of a resolved segment's first line, relative to <paramref name="relativeTo"/>.</summary>
    public static double? TopOf(ResolvedSegment segment, Visual relativeTo)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var bounds = segment.Block.GetTextRangeBoundsInControl(segment.Start, segment.Length);
        return bounds.Count > 0 && segment.Block.TranslatePoint(bounds[0].TopLeft, relativeTo) is { } point ? point.Y : null;
    }

    /// <summary>The character shown at <paramref name="y"/> (relative to <paramref name="relativeTo"/>) inside the renderer, if any.</summary>
    public static ReadingLocator? LocatorAt(Visual renderer, Visual relativeTo, double y)
    {
        var ordinals = new Dictionary<(int, int), int>();
        foreach (var block in Blocks(renderer))
        {
            var span = block.SourceSpan;
            var ordinal = ordinals.GetValueOrDefault((span.Start, span.End));
            ordinals[(span.Start, span.End)] = ordinal + 1;
            if (block.TranslatePoint(default, relativeTo) is not { } origin || origin.Y + block.Bounds.Height <= y)
            {
                continue;
            }

            var hit = block.TextLayout.HitTestPoint(new Point(0, Math.Max(0, y - origin.Y)));
            var bounds = block.GetTextRangeBoundsInControl(hit.TextPosition, Math.Min(1, block.LayoutText.Length - hit.TextPosition));
            var top = bounds.Count > 0 ? origin.Y + bounds[0].Y : origin.Y;
            return new ReadingLocator(span.Start, span.End, ordinal, hit.TextPosition, y - top);
        }

        return null;
    }

    /// <summary>
    /// Where the reading line should be now for this locator (relative to <paramref name="relativeTo"/>): the
    /// character's top plus the saved distance into it. Null if its block is gone.
    /// </summary>
    public static double? ReadingLineOf(ReadingLocator locator, Visual renderer, Visual relativeTo)
    {
        ArgumentNullException.ThrowIfNull(locator);
        var matches = Blocks(renderer).Where(b => b.SourceSpan.Start == locator.SourceStart && b.SourceSpan.End == locator.SourceEnd).ToArray();
        if (locator.Ordinal < 0 || locator.Ordinal >= matches.Length)
        {
            return null;
        }

        var block = matches[locator.Ordinal];
        var character = Math.Clamp(locator.Character, 0, Math.Max(0, block.LayoutText.Length - 1));
        var bounds = block.GetTextRangeBoundsInControl(character, Math.Min(1, block.LayoutText.Length));
        return bounds.Count > 0 && block.TranslatePoint(bounds[0].TopLeft, relativeTo) is { } point ? point.Y + locator.Within : null;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
