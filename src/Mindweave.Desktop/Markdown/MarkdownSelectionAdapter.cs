using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Mindweave.Core.Branches;

namespace Mindweave.Desktop.Markdown;

public sealed record MarkdownFragment(MarkdownTextBlock Block, int Start, int Length);

/// <summary>Versioned display ranges tied to source blocks; never finds a quote by text search.</summary>
public static class MarkdownSelectionAdapter
{
    public const string RendererProfile = "livemarkdown/2.5.1;mindweave/1";
    public const int FormatVersion = 1;
    private const string Highlight = "saved-source";
    private const int MaxFragments = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16, RespectRequiredConstructorParameters = true };
    private sealed record Envelope(int SourceLength, string SourceHash, IReadOnlyList<Segment> Segments);
    private sealed record Segment(int SourceStart, int SourceEnd, int Ordinal, string LayoutHash, int Start, int Length, string Quote, int? ExactRawStart);

    public static IReadOnlyList<MarkdownTextBlock> Blocks(MarkdownRenderer renderer) => renderer.GetVisualDescendants()
        .OfType<MarkdownTextBlock>().Where(b => b.IsVisible && b.GetVisualAncestors().All(a => a.IsVisible)).ToArray();

    public static SourceSelection Capture(Guid messageId, long revision, string markdown, MarkdownRenderer renderer)
    {
        if (messageId == Guid.Empty || revision < 0) throw new InvalidDataException("消息身份或修订号无效。");
        var blocks = Blocks(renderer);
        var segments = new List<Segment>();
        var ordinals = new Dictionary<(int, int), int>();
        foreach (var block in blocks)
        {
            var span = block.SourceSpan;
            var key = (span.Start, span.End); var ordinal = ordinals.GetValueOrDefault(key); ordinals[key] = ordinal + 1;
            var start = Math.Min(block.SelectionStart, block.SelectionEnd); var end = Math.Max(block.SelectionStart, block.SelectionEnd);
            if (start == end) continue;
            ValidateRange(markdown.Length, span.Start, span.End, block.LayoutText.Length, start, end - start);
            var raw = markdown[span.Start..(span.End + 1)];
            segments.Add(new(span.Start, span.End, ordinal, Hash(block.LayoutText), start, end - start,
                block.LayoutText[start..end], raw == block.LayoutText ? span.Start + start : null));
        }
        if (segments.Count == 0) throw new InvalidOperationException("请先拖选正文文字。");
        if (segments.Count > MaxFragments) throw new InvalidDataException("来源选区过大。");
        ValidateSupported(markdown, segments);
        var payload = JsonSerializer.Serialize(new Envelope(markdown.Length, Hash(markdown), segments), JsonOptions);
        if (Encoding.UTF8.GetByteCount(payload) > SourceAnchorProtocol.MaximumPayloadBytes) throw new InvalidDataException("来源选区过大。");
        var selection = new SourceSelection(messageId, revision, Hash(markdown), Quote(segments), new(FormatVersion, RendererProfile, payload));
        // Run the same complete validation on capture, including block order and contiguous selection.
        Resolve(selection, markdown, renderer);
        return selection;
    }

    public static IReadOnlyList<MarkdownFragment> Restore(SourceSelection selection, string markdown, MarkdownRenderer renderer)
    {
        var resolved = Resolve(selection, markdown, renderer);
        // No UI changes occur until the entire envelope has passed validation.
        foreach (var block in Blocks(renderer)) { block.SelectionStart = block.SelectionEnd = 0; block.Highlights.Remove(Highlight); }
        foreach (var part in resolved)
        {
            part.Block.SelectionStart = part.Start; part.Block.SelectionEnd = part.Start + part.Length;
            var styles = part.Block.HighlightStyles ?? new TextHighlightStyles();
            styles.Set(Highlight, new TextHighlightStyle { Background = Brush.Parse("#E6EBCF"), Foreground = Brush.Parse("#35402B"), CornerRadius = new CornerRadius(3) });
            part.Block.HighlightStyles = styles;
            part.Block.Highlights.Set(Highlight, [new TextHighlightRange(part.Start, part.Length)], 20);
        }
        return resolved;
    }

    private static IReadOnlyList<MarkdownFragment> Resolve(SourceSelection selection, string markdown, MarkdownRenderer renderer)
    {
        if (selection is null || selection.MessageId == Guid.Empty || selection.MessageRevision < 0 || selection.Anchor is not { } anchor ||
            anchor.FormatVersion != FormatVersion || anchor.RendererProfile != RendererProfile ||
            string.IsNullOrEmpty(anchor.Payload) || Encoding.UTF8.GetByteCount(anchor.Payload) > SourceAnchorProtocol.MaximumPayloadBytes)
            throw new InvalidDataException("来源格式或渲染配置不匹配。");
        Envelope envelope;
        try
        {
            using var json = JsonDocument.Parse(anchor.Payload, new JsonDocumentOptions { MaxDepth = 16 });
            ValidateJson(json.RootElement);
            envelope = json.RootElement.Deserialize<Envelope>(JsonOptions) ?? throw new JsonException();
        }
        catch (JsonException error) { throw new InvalidDataException("来源信封无法解析。", error); }
        if (envelope.SourceLength <= 0 || envelope.SourceLength > markdown.Length || envelope.Segments is not { Count: > 0 and <= MaxFragments } ||
            envelope.SourceHash != selection.SourceBodyHash || Hash(markdown[..envelope.SourceLength]) != envelope.SourceHash || string.IsNullOrEmpty(selection.Quote))
            throw new InvalidDataException("捕获时的正文已改变，无法精确定位原句。");
        var blocks = Blocks(renderer); var resolved = new List<MarkdownFragment>(); var previous = -1;
        foreach (var segment in envelope.Segments)
        {
            if (segment is null) throw new InvalidDataException("来源区间缺失。");
            ValidateRange(envelope.SourceLength, segment.SourceStart, segment.SourceEnd, int.MaxValue, segment.Start, segment.Length);
            var matches = blocks.Select((block, index) => (block, index)).Where(x => x.block.SourceSpan.Start == segment.SourceStart && x.block.SourceSpan.End == segment.SourceEnd).ToArray();
            if (segment.Ordinal < 0 || segment.Ordinal >= matches.Length) throw new InvalidDataException("来源区块不再一致。");
            var (block, index) = matches[segment.Ordinal];
            if (index <= previous || (previous >= 0 && index != previous + 1)) throw new InvalidDataException("来源区块次序或连续性无效。");
            if (resolved.Count > 0 && (resolved[^1].Start + resolved[^1].Length != resolved[^1].Block.LayoutText.Length || segment.Start != 0))
                throw new InvalidDataException("跨区块选区不完整。");
            previous = index;
            ValidateRange(envelope.SourceLength, segment.SourceStart, segment.SourceEnd, block.LayoutText.Length, segment.Start, segment.Length);
            if (Hash(block.LayoutText) != segment.LayoutHash || block.LayoutText.Substring(segment.Start, segment.Length) != segment.Quote)
                throw new InvalidDataException("显示文字或局部选区已改变。");
            if (segment.ExactRawStart is { } exact && (markdown[segment.SourceStart..(segment.SourceEnd + 1)] != block.LayoutText || exact != segment.SourceStart + segment.Start))
                throw new InvalidDataException("精确原文范围与显示范围不一致。");
            resolved.Add(new(block, segment.Start, segment.Length));
        }
        if (Quote(envelope.Segments) != selection.Quote) throw new InvalidDataException("保存引用与来源区间不一致。");
        ValidateSupported(markdown[..envelope.SourceLength], envelope.Segments);
        return resolved;
    }

    private static void ValidateRange(int sourceLength, int sourceStart, int sourceEnd, int layoutLength, int start, int length)
    {
        if (sourceStart < 0 || sourceEnd < sourceStart || sourceEnd >= sourceLength || start < 0 || length <= 0 || start > layoutLength - length)
            throw new InvalidDataException("来源或显示区间越界。");
    }
    private static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate field.");
                ValidateJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) ValidateJson(child);
    }
    private static void ValidateSupported(string markdown, IReadOnlyList<Segment> segments)
    {
        var document = Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline);
        var start = segments.Min(s => s.SourceStart); var end = segments.Max(s => s.SourceEnd);
        foreach (var node in document.Descendants())
        {
            if (node.Span.Start > end || node.Span.End < start) continue;
            var supported = node switch
            {
                HeadingBlock or ParagraphBlock or FencedCodeBlock or Markdig.Syntax.CodeBlock or ListBlock or ListItemBlock or QuoteBlock => true,
                LiteralInline or EmphasisInline or Markdig.Syntax.Inlines.CodeInline or LineBreakInline or AutolinkInline => true,
                LinkInline link => !link.IsImage,
                _ => false
            };
            if (!supported) throw new InvalidDataException("选区包含暂不支持精确定位的表格、公式或嵌入内容。");
        }
    }
    private static string Quote(IReadOnlyList<Segment> segments) => string.Join("\n", segments.Select(s => s.Quote));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static void Reveal(MarkdownFragment fragment, ScrollViewer scroll)
    {
        scroll.UpdateLayout();
        var ranges = fragment.Block.GetTextRangeBoundsInControl(fragment.Start, fragment.Length);
        if (ranges.Count == 0 || fragment.Block.TranslatePoint(ranges[0].TopLeft, scroll) is not { } point)
            throw new InvalidDataException("来源当前没有可定位的文字布局。");
        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + point.Y - 24));
        scroll.UpdateLayout();
    }

    public static ReadingLocation? CaptureReading(Guid messageId, MarkdownRenderer renderer, ScrollViewer scroll)
    {
        if (messageId == Guid.Empty) return null;
        foreach (var block in Blocks(renderer))
        {
            if (block.SourceSpan.Start < 0 || block.TranslatePoint(default, scroll) is not { } origin || origin.Y + block.Bounds.Height <= 0 || origin.Y >= scroll.Viewport.Height) continue;
            return new(messageId, block.SourceSpan.Start, Math.Max(0, -origin.Y), FormatVersion, RendererProfile);
        }
        return null;
    }

    /// <returns>False when identity/profile/block is unavailable; caller can fall back to the stable message.</returns>
    public static bool RestoreReading(ReadingLocation location, Guid messageId, MarkdownRenderer renderer, ScrollViewer scroll)
    {
        if (location.MessageId != messageId || location.FormatVersion != FormatVersion || location.RendererProfile != RendererProfile ||
            location.SourceStart < 0 || !double.IsFinite(location.OffsetWithinBlock) || location.OffsetWithinBlock < 0) return false;
        var matches = Blocks(renderer).Where(b => b.SourceSpan.Start == location.SourceStart).ToArray();
        if (matches.Length != 1 || matches[0].TranslatePoint(default, scroll) is not { } origin) return false;
        var block = matches[0];
        // A block-local pixel offset preserves the reading block across width changes, independent of source selection.
        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + origin.Y + Math.Min(location.OffsetWithinBlock, Math.Max(0, block.Bounds.Height - 1))));
        scroll.UpdateLayout(); return true;
    }
}
