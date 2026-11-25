using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LiveMarkdown.Avalonia;
using Mindweave.Application.Exchange;
using Mindweave.Application.Share;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Markdown;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// A shared page reads an answer as the app does (docs/design/sharing.md §6.2; architecture §16.3): each sample is drawn by
/// the app and for the page, and shows the same text, block by block. The places where the two differ on purpose have
/// tests of their own.
/// </summary>
public class ShareMarkdownTests
{
    private static readonly ShareMarkdown Markdown = new();

    public static TheoryData<string> Samples { get; } =
    [
        "Plain text, *italic*, **bold**, ~~struck~~, `code`, x^2^, H~2~O, ++under++ and ==marked==.",
        "的**“火箭号”**在1829年，**蒸汽动力**与**轨道运输**；**《机车》**。",
        "火车的诞生是**蒸汽动力（1804）**，以及*斜体*。",
        "# One\n\n## Two\n\n### Three 标题\n\ntext",
        "- one\n- two\n  - inner **bold**\n  - inner\n- three\n\n1. first\n2. second\n\n3. loose\n\n   with a paragraph",
        "- [ ] to do\n- [x] done",
        "| 年份 | 事件 |\n| --- | :---: |\n| 1804 | **特里维西克** |\n| 1829 | `Rocket` $v$ |",
        "a | b\n--- | ---\n1 | 2",
        "Inline $a^2+b^2$, \\(c^2\\) and $$d$$ in a sentence.",
        "$$x = \\frac{1}{2}$$",
        "Before\n\n\\[\nE = mc^2\n\\]\n\nafter\n\n$$\ny = 1\n$$\n\nend",
        "\\[ a \\]",
        "Costs $5 and $6, `$x$` is code, \\$y\\$ is not a formula.",
        "> quoted\n> more\n>\n> > deeper",
        "```csharp\nvar x = \"<b>\";\n\nif (a < b) { }\n```\n\n    indented code",
        "See [the link](https://example.org/a?b=1), <https://example.org/auto> and https://example.org/bare.",
        "![alt text](https://example.org/p.png) and ![](https://example.org/q.png)",
        "Line one\nline two  \nline three\\\nline four",
        "Entities &amp; &lt;b&gt; &copy; and **a < b & c > d**.",
        "One\n\n---\n\nTwo",
        "Cited[1], twice[2][3], not this one[9], [1](https://example.org/x), `[1]`.",
        "Unclosed **bold and *more* [ bracket ] and a lone * star_",
        "Term[^1]\n\n[^1]: The note.",
        "> [!NOTE]\n> An alert.",
        "\"\"quoted\"\" and a:b:c {.class} text",
        "混排 English 与中文，**bold**中文，$E=mc^2$结束。",
    ];

    [AvaloniaTheory]
    [MemberData(nameof(Samples))]
    public void The_page_shows_the_same_text_as_the_app_block_by_block(string markdown)
    {
        var app = AppBlocks(markdown);
        var page = Blocks(Markdown.Draw(markdown, new HashSet<int> { 1, 2, 3 }, []));

        Assert.Equal(app, page);
    }

    [Fact]
    public void Emphasis_tables_and_formulas_become_the_pages_elements()
    {
        Assert.Equal("p(的strong(“火箭号”)在)", Text("的**“火箭号”**在"));
        Assert.Equal("table(tr(th(p(a))th[right](p(b)))tr(td(p(1))td[right](p(2))))", Text("|a|b|\n|-|-:|\n|1|2|"));
        Assert.Equal("p(math[a]math[b]math[c])", Text("$a$\\(b\\)$$c$$"));
        Assert.Equal("mathblock[x]", Text("$$x$$"));
        Assert.Equal("mathblock[x\ny]", Text("\\[\nx\ny\n\\]"));
        Assert.Equal("pre(a\n\nb)", Text("```cs\na\n\nb\n```"));
        Assert.Equal("ul(li[task](p(task[x] done))li(p(x)))", Text("- [x] done\n- x"));
        Assert.Equal("p(a[https://e.org/](t)cite[1]([1])[2])", Text("[t](https://e.org/)[1][2]", 1));
    }

    /// <summary>Remote images stay links, as in the app; an address that is not one of the web's is no link at all.</summary>
    [Fact]
    public void An_image_is_a_link_and_an_address_that_could_run_is_text()
    {
        Assert.Equal("p(a[https://e.org/p.png](alt))", Text("![alt](https://e.org/p.png)"));
        Assert.Equal("p(x)", Text("[x](javascript:alert(1))"));
        Assert.Equal("p(x)", Text("[x](data:text/html,hi)"));
        Assert.Equal("p(x)", Text("[x](file:///etc/passwd)"));
        Assert.Equal("p(x)", Text("[x](relative/path)"));
    }

    /// <summary>Requirements §10.7: HTML in an answer is shown as text. (The app leaves it out of what it shows.)</summary>
    [Fact]
    public void Html_in_an_answer_is_its_text()
    {
        Assert.Equal("p(a <b>b</b> <img src=x onerror=alert(1)>)", Text("a <b>b</b> <img src=x onerror=alert(1)>"));
        Assert.Equal("p(<script>\nalert(1)\n</script>)", Text("<script>\nalert(1)\n</script>"));
        Assert.Equal("p(<!-- c -->)", Text("<!-- c -->"));
    }

    [Fact]
    public void Only_kinds_the_page_knows_are_drawn()
    {
        var known = new HashSet<string>("text p h1 h2 h3 h4 h5 h6 ul ol li blockquote pre code em strong del ins sub sup hl table tr th td br hr task a cite mark math mathblock div".Split(' '));
        foreach (var row in Samples)
        {
            Assert.Subset(known, All(Markdown.Draw(row.Data, new HashSet<int> { 1 }, [])).Select(n => n.Kind).ToHashSet());
        }
    }

    [Fact]
    public void The_sentence_a_branch_left_from_is_marked_through_emphasis_and_code()
    {
        Assert.Equal("p(a strong(mark[0](b))mark[0]( and )code(mark[0](c)) d)", Marked("a **b** and `c` d", "b and c"));
    }

    [Fact]
    public void Each_branch_has_its_own_mark_and_a_mark_can_span_blocks()
    {
        Assert.Equal("p(mark[0](one) two mark[1](three))", Marked("one two three", "one", "three"));
        Assert.Equal("p(one mark[0](two))p(mark[0](three) four)", Marked("one two\n\nthree four", "two\nthree"));
    }

    [Fact]
    public void A_quote_with_formulas_is_found_by_their_source()
    {
        Assert.Equal("p(so mark[0](when math[x] rises) it)", Marked("so when $x$ rises it", "when $x$ rises"));
    }

    [Fact]
    public void A_quote_that_is_not_there_or_is_there_twice_is_not_marked()
    {
        Assert.Equal("p(one two)", Marked("one two", "three"));
        Assert.Equal("p(rail and rail)", Marked("rail and rail", "rail"));
    }

    /// <summary>§6.6, point 3: the place tells two occurrences apart.</summary>
    [AvaloniaFact]
    public void Of_two_occurrences_the_one_at_the_stored_place_is_marked()
    {
        const string markdown = "rail and rail\n\n- a **rail** here\n- rail";
        var offset = new ExchangeBranchSource(MessageId.New(), "rail", new ExchangePosition(0, 9), null);
        Assert.Equal("p(rail and mark[0](rail))ul(li(p(a strong(rail) here))li(p(rail)))", Drawn(markdown, offset));

        // Without an exact offset, the stored anchor's block and place in it.
        var captured = Capture(markdown, block: 1, start: 2, length: 4);
        Assert.Equal("rail", SourceAnchors.QuoteOf(captured));
        var anchored = new ExchangeBranchSource(MessageId.New(), "rail", null, new ExchangeAnchor(SourceAnchors.FormatVersion, SourceAnchors.RenderProfile, SourceAnchors.Serialize(captured)));
        Assert.Equal("p(rail and rail)ul(li(p(a strong(mark[0](rail)) here))li(p(rail)))", Drawn(markdown, anchored));

        // An anchor of a profile this app cannot read tells nothing.
        Assert.Equal("p(rail and rail)ul(li(p(a strong(rail) here))li(p(rail)))", Drawn(markdown, anchored with { Anchor = anchored.Anchor! with { Profile = "other" } }));
    }

    [AvaloniaFact]
    public void The_offset_is_the_exact_place_in_the_Markdown_when_the_anchor_has_it()
    {
        Assert.Equal(9, Markdown.OffsetOf(Branch(Capture("rail and rail", block: 0, start: 9, length: 4))));
        Assert.Null(Markdown.OffsetOf(Branch(Capture("a **rail** here", block: 0, start: 2, length: 4))));
        Assert.Null(Markdown.OffsetOf(Branch(Capture("rail and rail", 0, 9, 4)) with { AnchorJson = "not json" }));
        Assert.Null(Markdown.OffsetOf(Branch(Capture("rail and rail", 0, 9, 4)) with { RenderProfile = "other" }));
    }

    private static BranchSource Branch(SelectionAnchor anchor) =>
        new(ConversationId.New(), ConversationId.New(), MessageId.New(), 0, "rail", SourceAnchors.Serialize(anchor), SourceAnchors.FormatVersion, SourceAnchors.RenderProfile, DateTimeOffset.UnixEpoch);

    private static string Marked(string markdown, params string[] quotes) =>
        Drawn(markdown, [.. quotes.Select(q => new ExchangeBranchSource(MessageId.New(), q, null, null))]);

    private static string Drawn(string markdown, params ExchangeBranchSource[] sources)
    {
        var marks = sources.Select(s => new ShareMark(ConversationId.New(), s)).ToList();
        var text = string.Concat(Markdown.Draw(markdown, new HashSet<int>(), marks).Select(Show));
        return marks.Select((m, i) => (Id: m.Branch.ToString(), i)).Aggregate(text, (t, m) => t.Replace(m.Id, m.i.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    private static string Text(string markdown, params int[] citations) => string.Concat(Markdown.Draw(markdown, citations.ToHashSet(), []).Select(Show));

    private static string Show(ShareNode node) => node.Kind == ShareNode.TextKind
        ? node.Value!
        : node.Kind + (string.IsNullOrEmpty(node.Value) ? "" : "[" + node.Value + "]") + (node.Parts.Count == 0 ? "" : "(" + string.Concat(node.Parts.Select(Show)) + ")");

    private static IEnumerable<ShareNode> All(IEnumerable<ShareNode> nodes) => nodes.SelectMany(n => All(n.Parts).Prepend(n));

    /// <summary>The page's text blocks in order, a formula in a line shown as the app's layout shows it.</summary>
    private static List<string> Blocks(IReadOnlyList<ShareNode> nodes)
    {
        var blocks = new List<string>();
        foreach (var node in nodes)
        {
            if (node.Kind is "p" or "pre" || (node.Kind.Length == 2 && node.Kind[0] == 'h' && char.IsAsciiDigit(node.Kind[1])))
            {
                blocks.Add(Flat(node));
            }
            else if (node.Kind is not "mathblock" and not "hr")
            {
                blocks.AddRange(Blocks(node.Parts));
            }
        }

        return blocks;
    }

    private static string Flat(ShareNode node) => node.Kind switch
    {
        ShareNode.TextKind => node.Value!,
        "br" => "\n",
        "math" or "task" => "￼",
        _ => string.Concat(node.Parts.Select(Flat)),
    };

    private static List<string> AppBlocks(string markdown)
    {
        var renderer = Render(markdown, new HashSet<int> { 1, 2, 3 });
        return [.. SourceAnchors.Blocks(renderer).Select(b => b.LayoutText)];
    }

    private static SelectionAnchor Capture(string markdown, int block, int start, int length)
    {
        var renderer = Render(markdown, new HashSet<int>());
        var at = SourceAnchors.Blocks(renderer)[block];
        at.SelectionStart = start;
        at.SelectionEnd = start + length;
        return SourceAnchors.Capture(markdown, renderer);
    }

    private static MarkdownRenderer Render(string markdown, IReadOnlySet<int> citations)
    {
        MarkdownSetup.EnsureRegistered();
        var document = Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline);
        CitationLinks.Apply(document, citations);
        var renderer = new MarkdownRenderer();
        var window = new Window { Content = new Border { Padding = new Thickness(20), Child = renderer }, Width = 800, Height = 600 };
        window.Show();
        renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(document);
        window.UpdateLayout();
        return renderer;
    }
}
