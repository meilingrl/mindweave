using System.Diagnostics;
using System.Text;
using Mindweave.Infrastructure.Tools;

namespace Mindweave.Infrastructure.Tests.Tools;

/// <summary>HTML to readable text (decision W13).</summary>
public class HtmlTextTests
{
    private static readonly Uri Page = new("https://example.com/articles/steam.html");

    private static string LongParagraph(string word) => string.Join(' ', Enumerable.Repeat(word, 60));

    [Fact]
    public void The_title_comes_from_title_then_og_title_then_h1_then_the_host()
    {
        Assert.Equal("Steam & power", HtmlText.Extract("<title>\n Steam &amp; power </title><h1>Heading</h1>", Page).Title);
        Assert.Equal("OG title", HtmlText.Extract("<title> </title><meta property=\"og:title\" content=\"OG title\"><h1>Heading</h1>", Page).Title);
        Assert.Equal("The heading", HtmlText.Extract("<body><header><h1>The <em>heading</em></h1></header><p>x</p></body>", Page).Title);
        Assert.Equal("example.com", HtmlText.Extract("<p>no title at all</p>", Page).Title);
    }

    [Fact]
    public void Scripts_styles_navigation_and_forms_are_dropped()
    {
        var html = """
            <html><head><title>T</title><style>p { color: red }</style><script>var x = "<p>not text</p>";</script></head>
            <body>
            <nav><a href="/">Home</a> Menu</nav>
            <header>Site header</header>
            <p>Body text.</p>
            <aside>Related</aside>
            <form><input name=q><button>Search</button><select><option>One</option></select></form>
            <noscript>Enable scripts</noscript>
            <template><p>hidden</p></template>
            <svg><text>vector</text></svg>
            <iframe src="x">frame</iframe>
            <!-- a comment <p>commented</p> -->
            <footer>Copyright</footer>
            </body></html>
            """;

        var (_, text, _) = HtmlText.Extract(html, Page);

        Assert.Equal("Body text.", text);
    }

    [Fact]
    public void The_longest_article_is_preferred_when_it_has_enough_text()
    {
        var html = $"<body><div>Sidebar junk</div><article><p>{LongParagraph("short")}</p></article>"
            + $"<article><h2>Main story</h2><p>{LongParagraph("longer")}</p></article><div>More junk</div></body>";

        var (_, text, _) = HtmlText.Extract(html, Page);

        Assert.StartsWith("## Main story\n\nlonger longer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("junk", text, StringComparison.Ordinal);
        Assert.DoesNotContain("short", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_is_used_when_there_is_no_article_and_a_tiny_article_is_ignored()
    {
        var withMain = $"<body><div>Outside</div><main><p>{LongParagraph("inside")}</p></main></body>";
        Assert.DoesNotContain("Outside", HtmlText.Extract(withMain, Page).Text, StringComparison.Ordinal);

        var tiny = "<body><p>Whole page text.</p><article>Tiny</article></body>";
        Assert.Equal("Whole page text.\n\nTiny", HtmlText.Extract(tiny, Page).Text);
    }

    [Fact]
    public void Blocks_become_lines_headings_and_list_items()
    {
        var html = "<h1>Title</h1><p>First   paragraph\n spans lines.</p><p>Second<br>line</p>"
            + "<ul><li>one</li><li>two <b>bold</b></li><li></li></ul><div>a</div><div>b</div>"
            + "<table><tr><td>c1</td><td>c2</td></tr><tr><td>d1</td><td>d2</td></tr></table>"
            + "<pre>code\n  indented</pre><h3>Sub</h3>";

        var (_, text, _) = HtmlText.Extract(html, Page);

        Assert.Equal(
            "# Title\n\nFirst paragraph spans lines.\n\nSecond\nline\n\n- one\n- two bold\n\na\nb\n\nc1 c2\nd1 d2\n\ncode\n  indented\n\n### Sub",
            text);
    }

    [Fact]
    public void Entities_are_decoded_and_whitespace_collapsed()
    {
        var (_, text, _) = HtmlText.Extract("<p>A&nbsp;&amp;&nbsp;B &lt;tag&gt; &#x4e2d;&#25991; &quot;q&quot;</p>", Page);

        Assert.Equal("A & B <tag> 中文 \"q\"", text);
    }

    [Fact]
    public void Links_come_from_the_whole_page_resolved_deduplicated_and_without_fragments()
    {
        var html = """
            <nav><a href="/home">Home</a></nav>
            <p><a href="other.html#part">Other</a> <a href="other.html">Again</a> <A HREF='https://x.org/p?a=1&amp;b=2'>X</A></p>
            <a href="#top">Top</a><a href="mailto:a@b.c">Mail</a><a href="javascript:void(0)">JS</a><a href="ftp://f.org/">FTP</a>
            <a href="  //cdn.example.net/lib  ">Protocol-relative</a><a>No href</a>
            """;

        var (_, _, links) = HtmlText.Extract(html, Page);

        Assert.Equal(
            ["https://example.com/home", "https://example.com/articles/other.html", "https://x.org/p?a=1&b=2", "https://cdn.example.net/lib"],
            links.Select(l => l.ToString()));
    }

    [Fact]
    public void A_base_element_sets_where_links_are_resolved_from()
    {
        var (_, _, links) = HtmlText.Extract("<head><base href=\"https://mirror.example.org/docs/\"></head><a href=\"page\">p</a>", Page);

        Assert.Equal([new Uri("https://mirror.example.org/docs/page")], links);
    }

    [Fact]
    public void At_most_500_links_are_kept()
    {
        var html = string.Concat(Enumerable.Range(0, 700).Select(i => $"<a href=\"/p{i}\">{i}</a>"));

        Assert.Equal(HtmlText.MaxLinks, HtmlText.Extract(html, Page).Links.Count);
    }

    [Fact]
    public void Malformed_html_is_read_as_well_as_it_can_be()
    {
        var html = "<P CLASS=\"a>b\">Upper <B>case</b> and a quoted &gt; in an attribute"
            + "<div data-x='1 > 0'>Inside div</div>"
            + "<p>Unclosed paragraph <span>unclosed span"
            + "<nav>Unclosed nav keeps its text"
            + "<p>3 < 4 and a stray > sign</p>"
            + "<div class=\"unterminated";

        var (_, text, _) = HtmlText.Extract(html, Page);

        Assert.Contains("Upper case and a quoted > in an attribute", text, StringComparison.Ordinal);
        Assert.Contains("Inside div", text, StringComparison.Ordinal);
        Assert.Contains("unclosed span", text, StringComparison.Ordinal);
        Assert.Contains("Unclosed nav keeps its text", text, StringComparison.Ordinal);
        Assert.Contains("3 < 4 and a stray > sign", text, StringComparison.Ordinal);
        Assert.DoesNotContain("data-x", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unclosed_script_hides_only_what_follows_it()
    {
        Assert.Equal("Before", HtmlText.Extract("<p>Before</p><script>if (a < b) { document.write('<p>x</p>')", Page).Text);
    }

    [Fact]
    public void Huge_and_hostile_inputs_finish_quickly()
    {
        var hostile = new StringBuilder();
        hostile.Append(string.Concat(Enumerable.Repeat("<div class=\"", 20_000)));
        hostile.Append(string.Concat(Enumerable.Repeat("<<<<<a href='", 20_000)));
        hostile.Append(string.Concat(Enumerable.Repeat("<!-", 20_000)));
        var big = string.Concat(Enumerable.Repeat("<div><p>Paragraph text &amp; more <a href=\"/x\">link</a></p></div>\n", 40_000));
        var watch = Stopwatch.StartNew();

        HtmlText.Extract(hostile.ToString(), Page);
        var (_, text, links) = HtmlText.Extract(big, Page);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Contains("Paragraph text & more link", text, StringComparison.Ordinal);
        Assert.Single(links);
    }
}
