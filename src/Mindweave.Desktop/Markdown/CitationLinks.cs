using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Mindweave.Core.Conversations;

namespace Mindweave.Desktop.Markdown;

/// <summary>
/// Makes an answer's cited [n] clickable (requirements §10.4.5): in a parsed document, each [n] whose number has a 出处 becomes a
/// link to <c>mindweave-citation:n</c> showing the same "[n]" over the same source span, so the shown text and source anchors
/// are unchanged. A number with no 出处 stays plain text. The answer's Markdown itself is never rewritten.
/// </summary>
public static class CitationLinks
{
    public const string Scheme = "mindweave-citation";

    /// <summary>The 出处 number a clicked link names, or null for any other link.</summary>
    public static int? NumberOf(Uri? link) =>
        link is { IsAbsoluteUri: true } && link.Scheme == Scheme && int.TryParse(link.AbsolutePath, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    public static void Apply(MarkdownDocument document, IReadOnlySet<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(numbers);
        if (numbers.Count == 0)
        {
            return;
        }

        // A paragraph's own inline container is not among the document's descendants; nested ones (emphasis) are.
        var containers = document.Descendants<LeafBlock>().Select(l => l.Inline).OfType<ContainerInline>()
            .Concat(document.Descendants<ContainerInline>().Where(c => c is not (LinkInline or LinkDelimiterInline)))
            .Distinct()
            .ToList();
        foreach (var container in containers)
        {
            Rewrite(container, numbers);
        }
    }

    private static void Rewrite(ContainerInline container, IReadOnlySet<int> numbers)
    {
        // Runs of adjacent plain text: Markdig splits "[1]" into "[" and "1]", and may leave an unmatched "[" as an empty
        // link delimiter (in "[2][3]"). Each run is read as one text, so a marker split across nodes is still found. The
        // children are rebuilt in order with AppendChild (inserting before a container's first child loses nodes).
        var children = new List<Inline>();
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            children.Add(child);
        }

        // An unmatched "[" (in "[2][3]") holds the rest of the line as its children: read as "[" and those children.
        var flat = new List<Inline>();
        var flattened = false;
        Flatten(children, flat, ref flattened);

        var rebuilt = new List<Inline>();
        var run = new List<(Inline Node, string Text)>();
        var changed = false;
        foreach (var child in flat)
        {
            if (TextOf(child) is { } text)
            {
                run.Add((child, text));
                continue;
            }

            changed |= Flush(run, numbers, rebuilt);
            rebuilt.Add(child);
        }

        changed |= Flush(run, numbers, rebuilt);
        if (!changed)
        {
            return;
        }

        foreach (var node in flat)
        {
            node.Remove();
        }

        foreach (var child in children)
        {
            child.Remove();
        }

        foreach (var node in rebuilt)
        {
            container.AppendChild(node);
        }
    }

    private static void Flatten(List<Inline> nodes, List<Inline> flat, ref bool flattened)
    {
        foreach (var node in nodes)
        {
            if (node is LinkDelimiterInline delimiter)
            {
                flattened = true;
                flat.Add(new LiteralInline("[") { Span = new SourceSpan(delimiter.Span.Start, delimiter.Span.Start), Line = delimiter.Line, Column = delimiter.Column });
                var inner = new List<Inline>();
                for (var child = delimiter.FirstChild; child is not null; child = child.NextSibling)
                {
                    inner.Add(child);
                }

                Flatten(inner, flat, ref flattened);
            }
            else
            {
                flat.Add(node);
            }
        }
    }

    private static string? TextOf(Inline inline) => inline is LiteralInline literal ? literal.Content.ToString() : null;

    /// <summary>Adds the run to <paramref name="rebuilt"/>, its cited markers as links; true when it changed. Empties the run.</summary>
    private static bool Flush(List<(Inline Node, string Text)> run, IReadOnlySet<int> numbers, List<Inline> rebuilt)
    {
        if (run.Count == 0)
        {
            return false;
        }

        var text = string.Concat(run.Select(r => r.Text));
        var markers = CitationMarkers.Find(text).Where(m => numbers.Contains(m.Number)).ToList();
        if (markers.Count == 0)
        {
            rebuilt.AddRange(run.Select(r => r.Node));
            run.Clear();
            return false;
        }

        // Source positions only where Markdig gave them (block spans are what anchors use; the shown text is unchanged).
        var first = run[0].Node;
        var start = first.Span.Start;
        var line = first.Line;
        var column = first.Column;
        var at = 0;
        foreach (var marker in markers)
        {
            if (marker.Index > at)
            {
                rebuilt.Add(Literal(text[at..marker.Index], start + at, line, column + at));
            }

            var link = new LinkInline($"{Scheme}:{marker.Number}", string.Empty)
            {
                Span = new SourceSpan(start + marker.Index, start + marker.Index + marker.Length - 1),
                Line = line,
                Column = column + marker.Index,
                IsClosed = true,
            };
            link.AppendChild(Literal(text.Substring(marker.Index, marker.Length), start + marker.Index, line, column + marker.Index));
            rebuilt.Add(link);
            at = marker.Index + marker.Length;
        }

        if (at < text.Length)
        {
            rebuilt.Add(Literal(text[at..], start + at, line, column + at));
        }

        run.Clear();
        return true;
    }

    private static LiteralInline Literal(string text, int start, int line, int column) =>
        new(text) { Span = new SourceSpan(start, start + text.Length - 1), Line = line, Column = column };
}
