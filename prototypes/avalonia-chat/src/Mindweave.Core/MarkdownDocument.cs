using System.Text;
using System.Text.RegularExpressions;

namespace Mindweave.Core;

public enum BlockKind { Paragraph, Heading, Bullet, Numbered, Code, Quote }
public sealed record TextRun(string Text, bool Bold = false, bool Code = false, string? Link = null);
public sealed record MarkdownBlock(string Id, BlockKind Kind, string Text, IReadOnlyList<TextRun> Runs, IReadOnlyList<int> RawIndices, int Level = 0, string? Label = null);
public sealed class MarkdownDocument
{
    // An intentionally small Markdown subset, not a CommonMark implementation.
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = Regex.Matches(markdown, @"[^\n]*(?:\n|$)").Cast<Match>().Where(x => x.Length > 0).ToList();
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var text = line.Value.TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(text)) continue;
            var kind = BlockKind.Paragraph;
            var level = 0;
            string? label = null;
            var prefix = 0;
            if (text.StartsWith("```"))
            {
                label = text[3..].Trim();
                var codeText = new StringBuilder();
                var indices = new List<int>();
                while (++lineIndex < lines.Count && !lines[lineIndex].Value.StartsWith("```"))
                {
                    var codeLine = lines[lineIndex];
                    var value = codeLine.Value.TrimEnd('\r', '\n');
                    if (codeText.Length > 0) { codeText.Append('\n'); indices.Add(codeLine.Index - 1); }
                    codeText.Append(value);
                    indices.AddRange(Enumerable.Range(codeLine.Index, value.Length));
                }
                blocks.Add(new("b" + blocks.Count, BlockKind.Code, codeText.ToString(), [new(codeText.ToString(), Code: true)], indices, Label: label));
                continue;
            }
            var heading = Regex.Match(text, @"^(#{1,3}) ");
            var ordered = Regex.Match(text, @"^(\d+)\. ");
            if (heading.Success) { kind = BlockKind.Heading; level = heading.Groups[1].Length; prefix = heading.Length; }
            else if (text.StartsWith("- ")) { kind = BlockKind.Bullet; prefix = 2; }
            else if (ordered.Success) { kind = BlockKind.Numbered; label = ordered.Groups[1].Value + "."; prefix = ordered.Length; }
            else if (text.StartsWith("> ")) { kind = BlockKind.Quote; prefix = 2; }
            var (visible, runs, map) = ParseInline(text[prefix..], line.Index + prefix);
            blocks.Add(new("b" + blocks.Count, kind, visible, runs, map, level, label));
        }
        return blocks;
    }

    public static SourceAnchor Anchor(string conversationId, string messageId, MarkdownBlock block, int start, int end)
    {
        var from = Math.Min(start, end);
        var to = Math.Max(start, end);
        if (from < 0 || to > block.Text.Length || from == to || string.IsNullOrWhiteSpace(block.Text[from..to]))
            throw new ArgumentException("请选择同一段内的有效文字。");
        return new(conversationId, messageId, block.Id, from, to, block.RawIndices[from], block.RawIndices[to - 1] + 1, block.Text[from..to]);
    }

    private static (string, List<TextRun>, List<int>) ParseInline(string text, int sourceOffset)
    {
        var visible = new StringBuilder();
        var runs = new List<TextRun>();
        var indices = new List<int>();
        var bold = false;
        var code = false;
        var segment = new StringBuilder();
        void Flush()
        {
            if (segment.Length == 0) return;
            runs.Add(new(segment.ToString(), bold, code));
            segment.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (!code && i + 1 < text.Length && text.AsSpan(i, 2).SequenceEqual("**"))
            { Flush(); bold = !bold; i++; continue; }
            if (text[i] == '`') { Flush(); code = !code; continue; }
            if (!code && text[i] == '[')
            {
                var link = Regex.Match(text[i..], @"^\[([^\]]+)\]\((https?://[^)]+)\)");
                if (link.Success)
                {
                    Flush();
                    var caption = link.Groups[1].Value;
                    visible.Append(caption);
                    indices.AddRange(Enumerable.Range(sourceOffset + i + 1, caption.Length));
                    runs.Add(new(caption, bold, false, link.Groups[2].Value));
                    i += link.Length - 1;
                    continue;
                }
            }
            segment.Append(text[i]); visible.Append(text[i]); indices.Add(sourceOffset + i);
        }
        Flush();
        return (visible.ToString(), runs, indices);
    }
}
