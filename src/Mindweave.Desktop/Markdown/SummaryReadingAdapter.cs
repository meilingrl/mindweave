using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using LiveMarkdown.Avalonia;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.Markdown;

public sealed record SummaryViewport(string ReadableText, ImmutableArray<ReadableInterval> Intervals, bool FitsViewport, SummaryReadLocation? Location);

/// <summary>Maps NFC grapheme text to measured glyph ranges. Offsets are normalized Unicode scalars, never Markdown offsets.</summary>
public static class SummaryReadingAdapter
{
    public const string Profile = MarkdownSelectionAdapter.RendererProfile + ";summary/1";
    private sealed record Glyph(string Text, MarkdownTextBlock Block, int Start, int Length);
    private static List<Glyph> Map(MarkdownRenderer renderer)
    {
        var units = new List<Glyph>(); Glyph? space = null;
        foreach (var block in MarkdownSelectionAdapter.Blocks(renderer))
        {
            if (units.Count > 0) space = new(" ", block, 0, block.LayoutText.Length == 0 ? 0 : StringInfo.GetNextTextElement(block.LayoutText, 0).Length);
            var elements = StringInfo.GetTextElementEnumerator(block.LayoutText);
            while (elements.MoveNext())
            {
                var raw = elements.GetTextElement(); var text = raw.Normalize(NormalizationForm.FormC);
                foreach (var rune in text.EnumerateRunes())
                {
                    var glyph = new Glyph(rune.ToString(), block, elements.ElementIndex, raw.Length);
                    if (Rune.IsWhiteSpace(rune)) { space = glyph with { Text = " " }; continue; }
                    if (space is not null && units.Count > 0) units.Add(space);
                    space = null; units.Add(glyph);
                }
            }
        }
        return units;
    }
    public static SummaryViewport? Measure(MarkdownRenderer renderer, ScrollViewer scroll)
    {
        if (scroll.Viewport.Height <= 0 || scroll.Viewport.Width <= 0 || renderer.Bounds.Width <= 0) return null;
        var glyphs = Map(renderer); if (glyphs.Count == 0) return null;
        var text = string.Concat(glyphs.Select(g => g.Text));
        if (text != SummaryReadPolicy.NormalizeReadableText(text)) return null;
        var visible = new List<ReadableInterval>(); SummaryReadLocation? location = null;
        var viewport = new Rect(0, 0, scroll.Viewport.Width, scroll.Viewport.Height);
        var allFit = renderer.Bounds.Height <= scroll.Viewport.Height && renderer.Bounds.Width <= scroll.Viewport.Width;
        // IsVisible does not mean in the viewport: reject offscreen blocks geometrically
        // before asking for any individual glyph ranges.
        var visibleBlocks = glyphs.Select(g => g.Block).Distinct().Where(b => b.TranslatePoint(default, scroll) is { } origin &&
            viewport.Intersects(new Rect(origin, b.Bounds.Size))).ToHashSet();
        for (var i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            if (g.Length <= 0 || !visibleBlocks.Contains(g.Block)) continue;
            var bounds = g.Block.GetTextRangeBoundsInControl(g.Start, g.Length);
            if (!bounds.Any(b => g.Block.TranslatePoint(b.TopLeft, scroll) is { } point &&
                viewport.Intersects(new Rect(point, b.Size)) && b.Width > 0 && b.Height > 0)) continue;
            if (visible.Count > 0 && visible[^1].End == i) visible[^1] = visible[^1] with { End = i + 1 };
            else visible.Add(new(i, i + 1));
            if (location is null && g.Block.TranslatePoint(default, scroll) is { } origin)
            {
                var first = i; while (first > 0 && glyphs[first - 1].Block == g.Block) first--;
                location = new(first, Math.Max(0, -origin.Y), Profile);
            }
        }
        return new(text, visible.ToImmutableArray(), allFit, location);
    }
    public static bool Restore(SummaryReadLocation location, MarkdownRenderer renderer, ScrollViewer scroll)
    {
        if (location.RendererProfile != Profile || location.ScalarOffset < 0 || !double.IsFinite(location.OffsetWithinBlock) || location.OffsetWithinBlock < 0) return false;
        var glyphs = Map(renderer);
        if (location.ScalarOffset >= glyphs.Count) return false;
        var block = glyphs[location.ScalarOffset].Block;
        if (block.TranslatePoint(default, scroll) is not { } point) return false;
        scroll.Offset = new(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + point.Y + Math.Min(location.OffsetWithinBlock, Math.Max(0, block.Bounds.Height - 1))));
        scroll.UpdateLayout(); return true;
    }
}
