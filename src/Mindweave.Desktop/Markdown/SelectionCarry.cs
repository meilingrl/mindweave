using Avalonia;

namespace Mindweave.Desktop.Markdown;

/// <summary>One text block's share of a carried selection.</summary>
/// <param name="SourceStart">Where the block's source starts; a growing block keeps its start while its end moves.</param>
/// <param name="Ordinal">Index among visible blocks with the same source start.</param>
/// <param name="Start">Selection start in the block's displayed text.</param>
/// <param name="Quote">The selected displayed text.</param>
public sealed record CarriedSegment(int SourceStart, int Ordinal, int Start, string Quote);

/// <summary>A selection on its way from a streaming answer's view to the finished answer's view.</summary>
/// <param name="Segments">The selected parts, block by block (<see cref="SelectionCarry.Capture"/>).</param>
/// <param name="Focused">
/// Whether the answer had keyboard focus. Its view leaves the page with it, so focus is lost; the copy gesture only
/// reaches a focused answer, so the finished answer's view takes focus back to keep the selection copyable.
/// </param>
public sealed record CarriedSelection(IReadOnlyList<CarriedSegment> Segments, bool Focused);

/// <summary>
/// Keeps a reader's selection on the text they chose while an answer streams, and carries it to the view of the finished
/// text. The stored answer is a new view parsed in full, and its last block has grown since the selection was made, so a
/// branch anchor (bound to the block's whole span and text) cannot be used. Each part is found again by where its block
/// starts and by its selected text: at the same offset if it is still there, otherwise at the nearest place in that block
/// showing the same text. Displayed text can shift while it streams, e.g. when a closing <c>**</c> turns marked-up
/// characters into an emphasis and removes the markers before the selection (2026-11-10); keeping the offsets alone
/// would then select other characters. If any part is no longer shown, nothing is selected rather than a guess.
/// </summary>
public static class SelectionCarry
{
    /// <summary>The renderer's selection, or null when nothing is selected.</summary>
    public static IReadOnlyList<CarriedSegment>? Capture(Visual renderer)
    {
        var segments = new List<CarriedSegment>();
        var ordinals = new Dictionary<int, int>();
        foreach (var block in SourceAnchors.Blocks(renderer))
        {
            var key = block.SourceSpan.Start;
            var ordinal = ordinals.GetValueOrDefault(key);
            ordinals[key] = ordinal + 1;
            var start = Math.Min(block.SelectionStart, block.SelectionEnd);
            var end = Math.Max(block.SelectionStart, block.SelectionEnd);
            var text = block.LayoutText;
            if (start != end && start >= 0 && end <= text.Length)
            {
                segments.Add(new(key, ordinal, start, text[start..end]));
            }
        }

        return segments.Count > 0 ? segments : null;
    }

    /// <summary>Selects every carried part again (<see cref="Locate"/>), or nothing when any part is no longer shown; returns whether it did.</summary>
    public static bool Apply(Visual renderer, IReadOnlyList<CarriedSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var blocks = SourceAnchors.Blocks(renderer);
        var resolved = new List<(LiveMarkdown.Avalonia.MarkdownTextBlock Block, int Start, int End)>();
        foreach (var segment in segments)
        {
            var matches = blocks.Where(b => b.SourceSpan.Start == segment.SourceStart).ToArray();
            if (segment.Ordinal >= matches.Length)
            {
                return false;
            }

            var block = matches[segment.Ordinal];
            if (Locate(block.LayoutText, segment) is not { } start)
            {
                return false;
            }

            resolved.Add((block, start, start + segment.Quote.Length));
        }

        foreach (var (block, start, end) in resolved)
        {
            block.SelectionStart = start;
            block.SelectionEnd = end;
        }

        return true;
    }

    /// <summary>Whether two captured selections cover the same text at the same places.</summary>
    public static bool Same(IReadOnlyList<CarriedSegment>? a, IReadOnlyList<CarriedSegment>? b) =>
        a is null || b is null ? a is null && b is null : a.SequenceEqual(b);

    /// <summary>Where the part's text is shown in its block: at its offset, else at the nearest occurrence; null when absent.</summary>
    internal static int? Locate(string text, CarriedSegment segment)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(segment);
        var quote = segment.Quote;
        if (segment.Start >= 0 && segment.Start <= text.Length - quote.Length && string.CompareOrdinal(text, segment.Start, quote, 0, quote.Length) == 0)
        {
            return segment.Start;
        }

        int? best = null;
        for (var at = text.IndexOf(quote, StringComparison.Ordinal); at >= 0; at = text.IndexOf(quote, at + 1, StringComparison.Ordinal))
        {
            if (best is not { } b || Math.Abs(at - segment.Start) < Math.Abs(b - segment.Start))
            {
                best = at;
            }
        }

        return best;
    }
}
