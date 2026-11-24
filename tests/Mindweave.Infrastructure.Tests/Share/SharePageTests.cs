using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mindweave.Application.Share;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Tests.Share;

/// <summary>The one HTML file a share is (docs/design/sharing.md S2, S8, §6.2): the exchange data and the page that draws from it.</summary>
public sealed partial class SharePageTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 18, 11, 42, 30, 120, TimeSpan.Zero);

    private static readonly SharedConversation Root = new(ConversationId.New(), null, "History of trains", Day, 3, true);
    private static readonly SharedConversation Branch = new(ConversationId.New(), Root.Id, null, Day.AddMinutes(9), 1, false);

    private static ShareDocument Document(string title = "Trains") =>
        new(ShareDocument.StructureKind, title, Day.AddDays(7), "0.7.0", [Root, Branch], [new ShareLink(Root.Id, Branch.Id)]);

    [Fact]
    public void The_data_block_holds_the_exchange_document_and_nothing_else()
    {
        using var data = Data(SharePage.Format(Document()));
        var root = data.RootElement;

        Assert.Equal(["format", "version", "kind", "title", "made_at", "app_version", "conversations", "links"], Names(root));
        Assert.Equal("mindweave-export", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("structure", root.GetProperty("kind").GetString());
        Assert.Equal("Trains", root.GetProperty("title").GetString());
        Assert.Equal("2026-11-25T11:42:30.120Z", root.GetProperty("made_at").GetString());
        Assert.Equal("0.7.0", root.GetProperty("app_version").GetString());

        var conversations = root.GetProperty("conversations").EnumerateArray().ToList();
        Assert.Equal(["id", "name", "started_at", "questions", "has_summary"], Names(conversations[0]));
        Assert.Equal(["id", "parent", "started_at", "questions", "has_summary"], Names(conversations[1]));
        Assert.Equal(Root.Id.ToString(), conversations[1].GetProperty("parent").GetString());
        Assert.Equal("2026-11-18T11:42:30.120Z", conversations[0].GetProperty("started_at").GetString());
        Assert.Equal(3, conversations[0].GetProperty("questions").GetInt32());
        Assert.True(conversations[0].GetProperty("has_summary").GetBoolean());

        var link = Assert.Single(root.GetProperty("links").EnumerateArray());
        Assert.Equal(["a", "b"], Names(link));
        Assert.Equal((Root.Id.ToString(), Branch.Id.ToString()), (link.GetProperty("a").GetString(), link.GetProperty("b").GetString()));
    }

    [Fact]
    public void A_title_that_looks_like_markup_stays_text_and_cannot_end_the_data_block()
    {
        const string title = "</script><script>alert(1)</script><!-- & \"x\" {{data}}";
        var html = SharePage.Format(Document(title));

        using var data = Data(html);
        Assert.Equal(title, data.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Chinese_title_can_be_found_in_the_files_bytes()
    {
        var bytes = SharePage.Encode(Document("期末复习：概率论"));

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("期末复习：概率论", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    /// <summary>S2: nothing is fetched. The page forbids itself to, and names no address to fetch from.</summary>
    [Fact]
    public void It_loads_nothing_from_the_network()
    {
        var html = SharePage.Format(Document());

        Assert.Contains("http-equiv=\"Content-Security-Policy\"", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        var addresses = Address().Matches(html).Select(m => m.Value).Distinct().ToList();
        Assert.Equal(["http://www.w3.org/2000/svg"], addresses); // The name of SVG's elements; nothing loads it.
        Assert.DoesNotMatch("(?i)<(link[^>]*stylesheet|img|iframe|object|embed|audio|video|base|form)\\b|@import|url\\(|\\bfetch\\(|XMLHttpRequest|import\\(", html);
    }

    /// <summary>S9: its own words are carried in both languages, each with every word the other has.</summary>
    [Fact]
    public void Its_own_words_are_in_English_and_Chinese()
    {
        var html = SharePage.Format(Document());
        using var text = JsonDocument.Parse(TextBlock().Match(html).Groups[1].Value);
        var english = text.RootElement.GetProperty("en");
        var chinese = text.RootElement.GetProperty("zh");

        Assert.Equal(Names(english), Names(chinese));
        Assert.Equal("Untitled conversation", english.GetProperty("untitled").GetString());
        Assert.All(english.EnumerateObject(), p => Assert.DoesNotMatch("[\\u4e00-\\u9fff]", p.Value.ToString()));
    }

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];

    private static JsonDocument Data(string html) => JsonDocument.Parse(DataBlock().Match(html).Groups[1].Value);

    [GeneratedRegex("<script type=\"application/json\" id=\"mindweave-export\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex DataBlock();

    [GeneratedRegex("<script type=\"application/json\" id=\"page-text\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex TextBlock();

    [GeneratedRegex("(?i)(https?:)?//[a-z0-9.-]+\\.[a-z]{2,}[^\"'\\s<]*")]
    private static partial Regex Address();
}
