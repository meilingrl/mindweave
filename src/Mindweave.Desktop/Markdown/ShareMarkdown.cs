using System.Text;
using System.Text.Json;
using LiveMarkdown.Avalonia;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Mindweave.Application.Share;
using Mindweave.Core.Branches;
using CodeBlock = Markdig.Syntax.CodeBlock;
using CodeInline = Markdig.Syntax.Inlines.CodeInline;

namespace Mindweave.Desktop.Markdown;

/// <summary>
/// Draws an answer or a summary for a shared page (architecture §16.3): the document comes from the pipeline the app draws
/// with, and each node becomes what the app shows for it, so both read alike. What the app does not show is not drawn
/// either, with one exception: HTML in an answer is shown as its text (requirements §10.7).
/// </summary>
public sealed class ShareMarkdown : IShareMarkdown
{
    public IReadOnlyList<ShareNode> Draw(string markdown, IReadOnlySet<int> citations, IReadOnlyList<ShareMark> marks)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(citations);
        ArgumentNullException.ThrowIfNull(marks);
        MarkdownSetup.EnsureRegistered();
        var document = Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline);
        CitationLinks.Apply(document, citations);
        var drawing = new Drawing(markdown);
        var nodes = drawing.Blocks(document);
        drawing.Mark(marks);
        return Finished(nodes);
    }

    public int? OffsetOf(BranchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source is { AnchorJson: { } anchor, AnchorFormatVersion: { } version, RenderProfile: { } profile }
            ? AnchorOf(version, profile, anchor)?.Segments[0].ExactRawStart
            : null;
    }

    /// <summary>The stored anchor, when this app can read it.</summary>
    private static SelectionAnchor? AnchorOf(int version, string profile, string json)
    {
        if (version != SourceAnchors.FormatVersion || !SourceAnchors.CompatibleProfiles.Contains(profile))
        {
            return null;
        }

        try
        {
            return SourceAnchors.Deserialize(json) is { Segments.Count: > 0 } anchor ? anchor : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The drawn nodes as they leave: neighbouring texts joined, and neighbouring marks of one branch.</summary>
    private static List<ShareNode> Finished(IEnumerable<Node> nodes)
    {
        var finished = new List<ShareNode>();
        foreach (var node in nodes)
        {
            var next = node.Kind == ShareNode.TextKind ? ShareNode.OfText(node.Value!) : new ShareNode(node.Kind, Finished(node.Parts), node.Value);
            var last = finished.Count > 0 ? finished[^1] : null;
            if (last is not null && last.Kind == next.Kind && next.Kind == ShareNode.TextKind)
            {
                finished[^1] = ShareNode.OfText(last.Value + next.Value);
            }
            else if (last is { Kind: "mark" } && next.Kind == "mark" && last.Value == next.Value)
            {
                finished[^1] = last with { Parts = [.. last.Parts, .. next.Parts] };
            }
            else
            {
                finished.Add(next);
            }
        }

        return finished;
    }

    /// <summary>A node while it is being drawn; marking splits its texts.</summary>
    private sealed class Node(string kind, string? value = null)
    {
        public string Kind { get; } = kind;

        public string? Value { get; } = value;

        public List<Node> Parts { get; } = [];
    }

    /// <summary>
    /// Something shown in a text block: a text, a formula, a line break. <paramref name="Readable"/> is how a quote has it
    /// (<see cref="SourceAnchors.QuoteOf"/>) and <paramref name="At"/> its start in all of them; <paramref name="LayoutAt"/>
    /// is its start in its block's displayed text, with <paramref name="Breaks"/> line breaks before it there.
    /// </summary>
    private sealed record Shown(Node Parent, Node Node, string Readable, int At, int LayoutAt, int Breaks, TextBlockPlace Block);

    /// <summary>A text block the app would show, as an anchor names it.</summary>
    private sealed record TextBlockPlace(SourceSpan Span, int Ordinal);

    private sealed class Drawing(string markdown)
    {
        private readonly StringBuilder _readable = new();
        private readonly List<Shown> _shown = [];
        private readonly Dictionary<SourceSpan, int> _ordinals = [];
        private TextBlockPlace _block = new(default, 0);
        private int _layout;
        private int _breaks;

        public List<Node> Blocks(IEnumerable<Block> blocks) => [.. blocks.Select(Of).OfType<Node>()];

        /// <summary>
        /// Marks each branch's sentence where it can be told for certain: where the quote stands once, or, when it stands
        /// more than once, at the stored place (§6.6, point 3). Where sentences overlap, the shorter one is on top.
        /// </summary>
        public void Mark(IReadOnlyList<ShareMark> marks)
        {
            var text = _readable.ToString();
            var owners = new string?[text.Length];
            foreach (var (mark, quote) in marks.Reverse().Select(m => (m, Quote: WithPlainBreaks(m.Source.Quote))).OrderByDescending(m => m.Quote.Length))
            {
                if (PlaceOf(mark, quote, text) is { } at)
                {
                    Array.Fill(owners, mark.Branch.ToString(), at, quote.Length);
                }
            }

            foreach (var shown in _shown.Where(s => s.Readable.Length > 0))
            {
                var parts = Split(shown, owners);
                var index = shown.Parent.Parts.IndexOf(shown.Node);
                shown.Parent.Parts.RemoveAt(index);
                shown.Parent.Parts.InsertRange(index, parts);
            }
        }

        private static IEnumerable<Node> Split(Shown shown, string?[] owners)
        {
            Node Owned(Node node, string? owner)
            {
                if (owner is null || node.Kind == "br")
                {
                    return node;
                }

                var mark = new Node("mark", owner);
                mark.Parts.Add(node);
                return mark;
            }

            if (shown.Node.Kind != ShareNode.TextKind)
            {
                yield return Owned(shown.Node, owners[shown.At]);
                yield break;
            }

            for (var start = 0; start < shown.Readable.Length;)
            {
                var owner = owners[shown.At + start];
                var end = start + 1;
                while (end < shown.Readable.Length && owners[shown.At + end] == owner)
                {
                    end++;
                }

                yield return Owned(new Node(ShareNode.TextKind, shown.Readable[start..end]), owner);
                start = end;
            }
        }

        /// <summary>
        /// The displayed text breaks lines as the platform does, so a quote captured on Windows has <c>\r\n</c>; here every
        /// break is one <c>\n</c>.
        /// </summary>
        private static string WithPlainBreaks(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

        private int? PlaceOf(ShareMark mark, string quote, string text)
        {
            var places = new List<int>();
            for (var at = quote.Length == 0 ? -1 : text.IndexOf(quote, StringComparison.Ordinal); at >= 0; at = text.IndexOf(quote, at + 1, StringComparison.Ordinal))
            {
                places.Add(at);
            }

            if (places.Count <= 1)
            {
                return places.Count == 1 ? places[0] : null;
            }

            if (mark.Source.Position is { Offset: var offset })
            {
                return Only(places, (shown, layout) => shown.Block.Span.Start + layout == offset);
            }

            if (mark.Source.Anchor is { } stored && AnchorOf(stored.Version, stored.Profile, stored.Data)?.Segments[0] is { } first)
            {
                var start = first.Start + first.Quote.Length - first.Quote.TrimStart().Length;
                return Only(places, (shown, layout) =>
                    shown.Block.Span.Start == first.SourceStart && shown.Block.Span.End == first.SourceEnd && shown.Block.Ordinal == first.Ordinal && layout == start);
            }

            return null;
        }

        /// <summary>
        /// The one place that is it. A stored place counts a line break as the platform it was stored on does, one character
        /// or two; when the two ways lead to different places, none is certain.
        /// </summary>
        private int? Only(List<int> places, Func<Shown, int, bool> isIt)
        {
            var found = new List<int>();
            foreach (var place in places)
            {
                if (_shown.LastOrDefault(s => s.At <= place && s.Readable.Length > 0) is not { } shown)
                {
                    continue;
                }

                var within = place - shown.At;
                var layout = shown.LayoutAt + within;
                var breaks = shown.Breaks + shown.Readable.AsSpan(0, within).Count('\n');
                if (isIt(shown, layout) || isIt(shown, layout + breaks))
                {
                    found.Add(place);
                }
            }

            return found.Count == 1 ? found[0] : null;
        }

        private Node? Of(Block block) => block switch
        {
            MathBlock math => Formula(math),
            CodeBlock code => Code(code),
            HeadingBlock heading => Text("h" + Math.Clamp(heading.Level, 1, 6), heading),
            ParagraphBlock paragraph => Text("p", paragraph),
            ListBlock list => Container(list.IsOrdered ? "ol" : "ul", null, list),
            ListItemBlock item => Container("li", item.Descendants<TaskList>().Any(t => t.Parent?.ParentBlock?.Parent == item) ? "task" : null, item),
            QuoteBlock quote => Container("blockquote", null, quote),
            Table table => Of(table),
            ThematicBreakBlock => new Node("hr"),
            HtmlBlock html => Html(html),
            _ => null, // The app shows nothing for it.
        };

        private Node Container(string kind, string? value, ContainerBlock block)
        {
            var node = new Node(kind, value);
            node.Parts.AddRange(Blocks(block));
            return node;
        }

        private Node Of(Table table)
        {
            var node = new Node("table");
            foreach (var row in table.OfType<TableRow>())
            {
                var line = new Node("tr");
                foreach (var (cell, column) in row.OfType<TableCell>().Select((c, i) => (c, i)))
                {
                    var alignment = column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment : null;
                    line.Parts.Add(Container(row.IsHeader ? "th" : "td", alignment switch { TableColumnAlign.Center => "center", TableColumnAlign.Right => "right", _ => null }, cell));
                }

                node.Parts.Add(line);
            }

            return node;
        }

        private Node Formula(MathBlock math)
        {
            // Not a text block of the app's, but a quote running across it has its source.
            StartBlock(math.Span);
            _readable.Append(Source(math.Span).Trim());
            return new Node(ShareNode.MathBlockKind, LinesOf(math).Trim('\n'));
        }

        private Node Code(CodeBlock code)
        {
            var node = new Node("pre");
            StartBlock(code.Span);
            Show(node, new Node(ShareNode.TextKind, LinesOf(code)));
            return node;
        }

        /// <summary>A block's lines; the group's own text joins those of a <c>\[…\]</c> formula without their line breaks.</summary>
        private static string LinesOf(LeafBlock block) => string.Join('\n', block.Lines.Lines?.Take(block.Lines.Count).Select(l => l.Slice.ToString()) ?? []);

        private static Node Html(HtmlBlock html)
        {
            var node = new Node("p");
            node.Parts.Add(new Node(ShareNode.TextKind, LinesOf(html).Trim('\n')));
            return node;
        }

        private Node? Text(string kind, LeafBlock block)
        {
            if (block.Inline is null)
            {
                return null;
            }

            var node = new Node(kind);
            StartBlock(block.Span);
            Inlines(node, block.Inline);
            return node;
        }

        private void StartBlock(SourceSpan span)
        {
            if (_readable.Length > 0)
            {
                _readable.Append('\n');
            }

            var ordinal = _ordinals.GetValueOrDefault(span);
            _ordinals[span] = ordinal + 1;
            _block = new TextBlockPlace(span, ordinal);
            _layout = 0;
            _breaks = 0;
        }

        private void Inlines(Node into, ContainerInline inlines)
        {
            foreach (var inline in inlines)
            {
                Add(into, inline);
            }
        }

        private void Add(Node into, Inline inline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Show(into, new Node(ShareNode.TextKind, literal.Content.ToString()));
                    break;
                case CodeInline code:
                    Show(Within(into, "code"), new Node(ShareNode.TextKind, code.Content));
                    break;
                case LineBreakInline:
                    Show(into, new Node("br"), "\n", 1);
                    break;
                case HtmlEntityInline entity:
                    Show(into, new Node(ShareNode.TextKind, entity.Transcoded.ToString()));
                    break;
                case HtmlInline html:
                    // Not in the app's text, so no quote has it.
                    Show(into, new Node(ShareNode.TextKind, html.Tag), string.Empty, 0);
                    break;
                case MathInline math:
                    Show(into, new Node(ShareNode.MathKind, math.Content.ToString()), Source(math.Span), 1);
                    break;
                case TaskList task:
                    Show(into, new Node("task", task.Checked ? "x" : null), string.Empty, 1);
                    break;
                case AutolinkInline auto:
                    Show(Link(into, auto.IsEmail ? "mailto:" + auto.Url : auto.Url), new Node(ShareNode.TextKind, auto.Url));
                    break;
                case LinkInline link:
                    Inlines(Link(into, link.Url), link);
                    break;
                case EmphasisInline emphasis:
                    Inlines(KindOf(emphasis) is { } kind ? Within(into, kind) : into, emphasis);
                    break;
                case DelimiterInline delimiter:
                    Show(into, new Node(ShareNode.TextKind, delimiter.ToLiteral()));
                    break;
                case ContainerInline container:
                    Inlines(into, container);
                    break;
            }
        }

        private static Node Within(Node into, string kind, string? value = null)
        {
            var node = new Node(kind, value);
            into.Parts.Add(node);
            return node;
        }

        /// <summary>A link to a page or an address to write to; any other address is not followed, so its text stands alone.</summary>
        private static Node Link(Node into, string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address))
            {
                return into;
            }

            return CitationLinks.NumberOf(address) is { } number ? Within(into, "cite", number.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : address.Scheme is "http" or "https" or "mailto" ? Within(into, "a", address.AbsoluteUri)
                : into;
        }

        private static string? KindOf(EmphasisInline emphasis) => (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
        {
            ('*' or '_', 2) => "strong",
            ('*' or '_', 1) => "em",
            ('~', 2) => "del",
            ('~', 1) => "sub",
            ('^', 1) => "sup",
            ('+', 2) => "ins",
            ('=', 2) => "hl",
            _ => null,
        };

        private void Show(Node into, Node text) => Show(into, text, text.Value!, text.Value!.Length);

        private void Show(Node into, Node node, string readable, int layoutLength)
        {
            into.Parts.Add(node);
            _shown.Add(new Shown(into, node, readable, _readable.Length, _layout, _breaks, _block));
            _readable.Append(readable);
            _layout += layoutLength;
            _breaks += node.Kind is ShareNode.TextKind or "br" ? readable.Count(c => c == '\n') : 0;
        }

        private string Source(SourceSpan span) => span.Start >= 0 && span.End >= span.Start && span.End < markdown.Length ? WithPlainBreaks(markdown[span.Start..(span.End + 1)]) : string.Empty;
    }
}
