using LiveMarkdown.Avalonia;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Controls;

namespace LiveMarkdownSpike;

public sealed record AnchorSegment(int SourceStart, int SourceEnd, int Ordinal, string LayoutHash, int Start, int Length, string Quote, int? ExactRawStart = null);
public sealed record SelectionAnchor(int Version, int SourceLength, string SourceHash, IReadOnlyList<AnchorSegment> Segments);
public sealed record ResolvedSegment(MarkdownTextBlock Block, int Start, int Length);

public static class SelectionAnchors
{
    public static void Reveal(ResolvedSegment segment, ScrollViewer scroll)
    {
        scroll.UpdateLayout();
        var bounds = segment.Block.GetTextRangeBoundsInControl(segment.Start, segment.Length);
        if (bounds.Count == 0 || segment.Block.TranslatePoint(bounds[0].TopLeft, scroll) is not { } point)
            throw new InvalidDataException("来源存在，但当前没有可定位的文字布局。");
        scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y + point.Y - 100));
        scroll.UpdateLayout();
    }
    public static IReadOnlyList<MarkdownTextBlock> Blocks(MarkdownRenderer renderer) => renderer.GetVisualDescendants()
        .OfType<MarkdownTextBlock>().Where(x => x.IsVisible && x.GetVisualAncestors().All(a => a.IsVisible)).ToArray();

    public static SelectionAnchor Capture(string markdown, MarkdownRenderer renderer)
    {
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
            if (start == end) continue;
            if (span.Start < 0 || span.End < span.Start || span.End >= markdown.Length || start < 0 || end > block.LayoutText.Length)
                throw new InvalidDataException("控件的来源或选区不完整，拒绝猜测位置。");
            var quote = block.LayoutText[start..end];
            var rawBlock = markdown[span.Start..(span.End + 1)];
            // A raw character offset is only asserted when source and displayed block are identical.
            int? exact = rawBlock == block.LayoutText ? span.Start + start : null;
            segments.Add(new(span.Start, span.End, ordinal, Hash(block.LayoutText), start, end - start, quote, exact));
        }
        if (segments.Count == 0) throw new InvalidOperationException("请先拖选正文文字。");
        return new(1, markdown.Length, Hash(markdown), segments);
    }

    public static IReadOnlyList<ResolvedSegment> Restore(SelectionAnchor anchor, string markdown, MarkdownRenderer renderer)
    {
        if (anchor.Version != 1 || anchor.SourceLength < 0 || markdown.Length < anchor.SourceLength ||
            Hash(markdown[..anchor.SourceLength]) != anchor.SourceHash || anchor.Segments.Count == 0)
            throw new InvalidDataException("捕获版本正文已改变，拒绝恢复到相似文字。");
        var blocks = Blocks(renderer);
        var resolved = new List<ResolvedSegment>();
        foreach (var segment in anchor.Segments)
        {
            var matches = blocks.Where(x => x.SourceSpan.Start == segment.SourceStart && x.SourceSpan.End == segment.SourceEnd).ToArray();
            if (segment.Ordinal < 0 || segment.Ordinal >= matches.Length) throw new InvalidDataException("来源区块不再一致。");
            var block = matches[segment.Ordinal];
            if (Hash(block.LayoutText) != segment.LayoutHash || segment.Start < 0 || segment.Length <= 0 ||
                segment.Start > block.LayoutText.Length - segment.Length || block.LayoutText.Substring(segment.Start, segment.Length) != segment.Quote)
                throw new InvalidDataException("显示文字或局部选区已改变，拒绝恢复。");
            if (segment.ExactRawStart is { } rawStart)
            {
                var rawBlock = markdown[segment.SourceStart..(segment.SourceEnd + 1)];
                if (rawBlock != block.LayoutText || rawStart != segment.SourceStart + segment.Start ||
                    rawStart < 0 || rawStart > markdown.Length - segment.Length || markdown.Substring(rawStart, segment.Length) != segment.Quote)
                    throw new InvalidDataException("精确原文范围与显示选区不一致。");
            }
            resolved.Add(new(block, segment.Start, segment.Length));
        }
        // Validate everything before changing selection or highlights; failed restores have no partial effect.
        foreach (var block in blocks)
        {
            block.SelectionStart = block.SelectionEnd = 0;
            block.Highlights.Remove("saved-source");
        }
        foreach (var part in resolved)
        {
            part.Block.SelectionStart = part.Start;
            part.Block.SelectionEnd = part.Start + part.Length;
            var styles = part.Block.HighlightStyles ?? new TextHighlightStyles();
            styles.Set("saved-source", new TextHighlightStyle { Background = Brush.Parse("#E6EBCF"), Foreground = Brush.Parse("#35402B"), CornerRadius = new CornerRadius(3) });
            part.Block.HighlightStyles = styles;
            part.Block.Highlights.Set("saved-source", [new TextHighlightRange(part.Start, part.Length)], 20);
        }
        return resolved;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
