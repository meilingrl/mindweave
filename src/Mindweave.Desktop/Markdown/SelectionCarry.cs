using Avalonia;

namespace Mindweave.Desktop.Markdown;

/// <summary>One text block's share of a carried selection.</summary>
/// <param name="SourceStart">Where the block's source starts; a growing block keeps its start while its end moves.</param>
/// <param name="Ordinal">Index among visible blocks with the same source start.</param>
/// <param name="Start">Selection start in the block's displayed text.</param>
/// <param name="Quote">The selected displayed text.</param>
public sealed record CarriedSegment(int SourceStart, int Ordinal, int Start, string Quote);

/// <summary>
/// Carries a reader's selection from a streaming answer's view to the view of its finished text. The stored answer is
/// a new view parsed in full, and its last block has grown since the selection was made, so a branch anchor (bound to
/// the block's whole span and text) cannot be used. Text before the end only ever grows, so each part is found again
/// by where its block starts and checked against the same displayed text at the same offset; if any part differs
/// (the final text changed the block, e.g. closed an emphasis), nothing is selected rather than a guess.
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

    /// <summary>Selects every carried part again, or nothing when any part is no longer shown the same way; returns whether it did.</summary>
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
            var text = block.LayoutText;
            if (segment.Start < 0 || segment.Start > text.Length - segment.Quote.Length || string.CompareOrdinal(text, segment.Start, segment.Quote, 0, segment.Quote.Length) != 0)
            {
                return false;
            }

            resolved.Add((block, segment.Start, segment.Start + segment.Quote.Length));
        }

        foreach (var (block, start, end) in resolved)
        {
            block.SelectionStart = start;
            block.SelectionEnd = end;
        }

        return true;
    }
}
