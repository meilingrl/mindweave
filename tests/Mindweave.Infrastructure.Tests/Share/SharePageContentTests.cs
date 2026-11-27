using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mindweave.Application.Exchange;
using Mindweave.Application.Share;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Share;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Share;

/// <summary>The file of a share with content (docs/design/sharing.md §6.1, §6.2): its data, what the page draws from, and what it may run or load.</summary>
public sealed partial class SharePageContentTests
{
    private static readonly ConversationId RootId = ConversationId.New();
    private static readonly DateTimeOffset Day = new(2026, 11, 18, 11, 42, 30, 120, TimeSpan.Zero);
    private static readonly ExchangeMessage Question = new(MessageId.New(), 0, MessageRole.User, Day, MessageState.Complete, "Why?") { Attachments = ["gauge.pdf", null] };

    private static readonly ExchangeMessage Answer = new(MessageId.New(), 4, MessageRole.Assistant, Day, MessageState.Stopped, "Because **rails**[1].")
    {
        Activity = new ExchangeActivity(0, 0, 2, 1, 3),
        Citations = [new(1, CitationKind.Web, "Page", new Uri("https://example.org/a")), new(2, CitationKind.Summary, "Rails", Summary: RootId), new(3, CitationKind.Attachment)],
    };

    private static readonly ExchangeMessage Earlier = new(MessageId.New(), 1, MessageRole.Assistant, Day, MessageState.Complete, "Earlier.") { ReplacedBy = Answer.Id };

    private static ExchangeDocument Document(string answer = "Because **rails**[1].")
    {
        var root = new ExchangeConversation(RootId, null, "History", Day, 1, true)
        {
            Content = new ExchangeContent([Question, Earlier, Answer with { Text = answer }], null, new ExchangeSummary(SummaryVersionId.New(), "Rails", "Body *text*", Day, 4)),
        };
        var branch = new ExchangeConversation(ConversationId.New(), RootId, "Branch", Day.AddMinutes(9), 0, false)
        {
            Content = new ExchangeContent([], new ExchangeBranchSource(Answer.Id, "rails", new ExchangePosition(0, 10), new ExchangeAnchor(1, "p", "{\"v\":1}")), null),
        };
        return new(ExchangeDocument.ContentKind, "Trains", Day.AddDays(7), "0.7.0", [root, branch], [new ExchangeLink(root.Id, branch.Id) { Note = "note" }]);
    }

    private static string Format(ExchangeDocument document) => SharePage.Format(document, new PlainMarkdown());

    [Fact]
    public void The_data_block_holds_the_messages_the_branch_source_and_the_summary()
    {
        using var data = JsonDocument.Parse(Block(Format(Document()), "mindweave-export"));
        var conversations = data.RootElement.GetProperty("conversations");
        Assert.Equal("content", data.RootElement.GetProperty("kind").GetString());

        var root = conversations[0];
        Assert.Equal(["id", "name", "started_at", "questions", "has_summary", "summary", "messages"], Names(root));
        Assert.Equal(["id", "title", "body", "at", "to_seq"], Names(root.GetProperty("summary")));
        var messages = root.GetProperty("messages");
        Assert.Equal(["id", "seq", "role", "at", "state", "text", "attachments"], Names(messages[0]));
        Assert.Equal([0, 1, 4], messages.EnumerateArray().Select(m => m.GetProperty("seq").GetInt32()));
        Assert.Equal(4, root.GetProperty("summary").GetProperty("to_seq").GetInt32());
        Assert.Equal("""["gauge.pdf",null]""", messages[0].GetProperty("attachments").GetRawText());
        Assert.Equal(["id", "seq", "role", "at", "state", "text", "replaced_by"], Names(messages[1]));
        Assert.Equal(Answer.Id.ToString(), messages[1].GetProperty("replaced_by").GetString());
        Assert.Equal(["id", "seq", "role", "at", "state", "text", "activity", "citations"], Names(messages[2]));
        Assert.Equal(("answer", "stopped", "Because **rails**[1]."), (messages[2].GetProperty("role").GetString(), messages[2].GetProperty("state").GetString(), messages[2].GetProperty("text").GetString()));
        Assert.Equal("""{"attachment_searches":0,"summary_searches":0,"web_searches":2,"pages_read":1,"other_calls":3}""", messages[2].GetProperty("activity").GetRawText());
        Assert.Equal(
            ["""{"number":1,"kind":"web","title":"Page","url":"https://example.org/a"}""", $$"""{"number":2,"kind":"summary","title":"Rails","conversation":"{{RootId}}"}""", """{"number":3,"kind":"attachment"}"""],
            messages[2].GetProperty("citations").EnumerateArray().Select(c => c.GetRawText()));

        var source = conversations[1].GetProperty("source");
        Assert.Equal(["message", "quote", "position", "anchor"], Names(source));
        Assert.Equal("""{"block":0,"offset":10}""", source.GetProperty("position").GetRawText());
        Assert.Equal(["version", "profile", "data"], Names(source.GetProperty("anchor")));
        Assert.Equal("note", data.RootElement.GetProperty("links")[0].GetProperty("note").GetString());
    }

    [Fact]
    public void What_the_page_draws_is_of_the_shared_answers_and_summaries_only()
    {
        var document = Document();
        using var drawn = JsonDocument.Parse(Block(Format(document), "page-drawn"));

        Assert.Equal(
            new HashSet<string> { Earlier.Id.ToString(), Answer.Id.ToString(), document.Conversations[0].Content!.Summary!.Id.ToString() },
            Names(drawn.RootElement).ToHashSet());
        Assert.Equal("""[["p",null,"Earlier."]]""", drawn.RootElement.GetProperty(Earlier.Id.ToString()).GetRawText());
    }

    /// <summary>§6.2: nothing in an answer runs. It stays inside the data, and only the file's own scripts may run at all.</summary>
    [Fact]
    public void An_answer_with_a_script_or_an_event_handler_cannot_run()
    {
        const string evil = "</script><script>alert(1)</script> <img src=x onerror=alert(2)> {{data}} {{drawn}} <!--";
        var html = Format(Document(evil));

        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        using var data = JsonDocument.Parse(Block(html, "mindweave-export"));
        Assert.Equal(evil, data.RootElement.GetProperty("conversations")[0].GetProperty("messages")[2].GetProperty("text").GetString());

        // Even markup that got into the page could not run: the policy names each of the file's scripts by its hash.
        var policy = Policy().Match(html).Groups[1].Value;
        var directive = Regex.Match(policy, "script-src ([^;]*)").Groups[1].Value;
        Assert.DoesNotContain("unsafe", directive, StringComparison.Ordinal);
        var scripts = Scripts().Matches(html).Select(m => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value))) + "'").ToList();
        Assert.NotEmpty(scripts);
        Assert.Equal(scripts.ToHashSet(), directive.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet());

        // And the page never turns text into markup.
        Assert.DoesNotMatch("innerHTML|outerHTML|insertAdjacentHTML|document\\.write|createContextualFragment|DOMParser|\\beval\\(|new Function|setHTML", OwnScript(html));
    }

    [Theory]
    [InlineData("No formula.", false)]
    [InlineData("$$x^2$$", true)]
    public void The_formula_library_is_in_the_file_only_when_there_is_a_formula(string answer, bool carried)
    {
        var html = Format(Document(answer));

        Assert.Equal(carried, html.Contains("renderToString", StringComparison.Ordinal));
        Assert.Equal(carried, html.Contains("font/woff2", StringComparison.Ordinal));
        Assert.Equal(carried, Policy().Match(html).Groups[1].Value.Contains("font-src data:", StringComparison.Ordinal));
        Assert.Equal(carried, html.Contains("Khan Academy", StringComparison.Ordinal)); // Its licence asks for its notice in every copy.
    }

    /// <summary>S2: nothing is fetched, formulas and fonts included.</summary>
    [Theory]
    [InlineData("No formula.")]
    [InlineData("$$x^2$$")]
    public void It_loads_nothing_from_the_network(string answer)
    {
        var html = Format(Document(answer) with { Links = [] });

        var policy = Policy().Match(html).Groups[1].Value;
        Assert.StartsWith("default-src 'none'; ", policy, StringComparison.Ordinal);
        Assert.DoesNotMatch("http|\\*|'self'|blob:|connect-src", policy);
        Assert.All(Regex.Matches(html, "url\\(([^)]{0,30})").Select(m => m.Groups[1].Value), u => Assert.StartsWith("data:font/woff2;base64,", u, StringComparison.Ordinal));
        Assert.DoesNotMatch("(?i)<(link[^>]*stylesheet|img|iframe|object|embed|audio|video|base|form)\\b|@import", Scripts().Replace(html, string.Empty));
        Assert.DoesNotMatch("\\bfetch\\(|XMLHttpRequest|import\\(|WebSocket|sendBeacon", OwnScript(html));
    }

    [Fact]
    public void Its_own_words_are_in_English_and_Chinese()
    {
        using var text = JsonDocument.Parse(Block(Format(Document()), "page-text"));

        Assert.Equal(Names(text.RootElement.GetProperty("en")), Names(text.RootElement.GetProperty("zh")));
        Assert.All(text.RootElement.GetProperty("en").EnumerateObject(), p => Assert.DoesNotMatch("[\\u4e00-\\u9fff]", p.Value.ToString()));
    }

    /// <summary>A structure-only share stays what it was: no part of the with-content page is in it.</summary>
    [Fact]
    public void A_structure_only_file_has_nothing_of_the_content_page()
    {
        var structure = Document() with { Kind = ExchangeDocument.StructureKind, Conversations = [.. Document().Conversations.Select(c => c with { Content = null })], Links = [] };
        var html = SharePage.Format(structure);

        Assert.DoesNotContain("page-drawn", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", html, StringComparison.Ordinal);
        Assert.DoesNotMatch("(?i)katex|woff2|font-src|sha256|citation|attachment|incomplete", html);
    }

    private static string OwnScript(string html) => Scripts().Matches(html)[^1].Groups[1].Value;

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];

    private static string Block(string html, string id) => Regex.Match(html, "<script type=\"application/json\" id=\"" + id + "\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;

    [GeneratedRegex("<script>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex Scripts();

    [GeneratedRegex("http-equiv=\"Content-Security-Policy\" content=\"([^\"]*)\"")]
    private static partial Regex Policy();
}
