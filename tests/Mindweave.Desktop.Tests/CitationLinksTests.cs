using LiveMarkdown.Avalonia;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Mindweave.Desktop.Markdown;

namespace Mindweave.Desktop.Tests;

/// <summary>出处 in a parsed answer (requirements §10.4.5): only numbers with a 出处 become links, over the same source and text.</summary>
public class CitationLinksTests
{
    private static MarkdownDocument Parse(string markdown)
    {
        MarkdownSetup.EnsureRegistered();
        return Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline);
    }

    [Fact]
    public void Cited_numbers_become_links_and_others_stay_text()
    {
        const string text = "瓦特取得了专利[1]。铁路通车[2][3]。没有来源[7]。见 [链接](https://a.example) 与 `[1]`。";
        var document = Parse(text);
        CitationLinks.Apply(document, new HashSet<int> { 1, 2, 3 });

        var links = document.Descendants<LinkInline>().Select(l => l.Url).ToList();
        Assert.Equal(["mindweave-citation:1", "mindweave-citation:2", "mindweave-citation:3", "https://a.example"], links);
        var cited = document.Descendants<LinkInline>().First();
        Assert.Equal("[1]", text[cited.Span.Start..(cited.Span.End + 1)]);
        Assert.Equal("[1]", ((LiteralInline)cited.FirstChild!).Content.ToString());
        Assert.Equal(text.Replace("[链接](https://a.example)", "链接", StringComparison.Ordinal).Replace("`[1]`", string.Empty, StringComparison.Ordinal),
            string.Concat(document.Descendants<LiteralInline>().Select(l => l.Content.ToString())));
        Assert.Equal(1, CitationLinks.NumberOf(new Uri(links[0]!)));
        Assert.Null(CitationLinks.NumberOf(new Uri("https://a.example")));
    }

    [Fact]
    public void Without_citations_nothing_changes()
    {
        var document = Parse("只有文字[1]。");
        CitationLinks.Apply(document, new HashSet<int>());
        Assert.Empty(document.Descendants<LinkInline>());
    }
}
