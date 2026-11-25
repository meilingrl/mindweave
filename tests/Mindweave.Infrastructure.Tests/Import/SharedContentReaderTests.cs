using System.Text;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Import;
using Mindweave.Infrastructure.Share;
using Mindweave.Infrastructure.Tests.Share;

namespace Mindweave.Infrastructure.Tests.Import;

/// <summary>Reading a share with content back: what the page's writer writes, and only what can be true (docs/design/sharing-import.md §1, §7).</summary>
public sealed class SharedContentReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-import-content-reader", Guid.NewGuid().ToString("N"));

    public SharedContentReaderTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Fixed identities: the theory's data names them, and it is made in another run than the one that reads them.
    private static MessageId Id(int number) => MessageId.Parse($"0190f000-0000-7000-8000-00000000020{number}");

    private static readonly ExchangeMessage Asked = Shared.Question(0, "Why </script> & “how” 为什么?", "plan <1>.pdf", null) with { Id = Id(1) };

    private static readonly ExchangeMessage Told = Shared.Answer(3, "So [1][2].", MessageState.Interrupted) with
    {
        Id = Id(2),
        Activity = new(1, 2, 3, 4, 5),
        Citations = [new(1, CitationKind.Web, "A page", new Uri("https://example.org/a?b=c&d=%20e")), new(2, CitationKind.Summary, "Steam"), new(3, CitationKind.Attachment), new(4, CitationKind.Material)],
    };

    private static readonly ExchangeMessage Earlier = Shared.Answer(1, "Earlier.", MessageState.Failed) with { Id = Id(3), ReplacedBy = Told.Id };

    private static readonly ExchangeConversation Root = Shared.Conversation(null, "Trains", null, Asked, Earlier, Told).With(Shared.Summary("Trains", 7));
    private static readonly ExchangeConversation Branch = Shared.Conversation(Root, null, new(Earlier.Id, "Earlier", new(0, 12), new(1, "livemarkdown-2.5.1/cjk-emphasis-1", "{\"v\":1}")), Shared.Question(0, "Branch?"), Shared.Answer(1, "Yes.", MessageState.Stopped));
    private static readonly ExchangeConversation Continued = Shared.Conversation(Root, "Mine", new(Told.Id, null, null, null), Shared.Question(0, "More?"));
    private static readonly ExchangeConversation Empty = Shared.Conversation(Branch, "Empty", null);

    private static ExchangeDocument Document => Shared.Document(Root, Branch, Continued, Empty) with { Links = [new(Root.Id, Branch.Id) { Note = "关联" }] };

    private static string Html => SharePage.Format(Document, new PlainMarkdown());

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_share_with_content_is_read_as_it_was_written()
    {
        var read = await ReadAsync(Html);

        Assert.Null(read.Refusal);
        Assert.Equal(ExchangeDocument.ContentKind, read.Document!.Kind);
        Assert.True(read.Document.SameContents(Document));
    }

    [Fact]
    public async Task Fields_this_version_does_not_know_are_passed_over()
    {
        Assert.True((await ReadAsync(Changed("\"role\":\"answer\"", "\"mood\":{\"x\":[1]},\"role\":\"answer\""))).Document!.SameContents(Document));
    }

    public static TheoryData<string, string> Damage => new()
    {
        { "\"messages\":[", "\"messages\":[7," },
        { "\"messages\":[", "\"said\":[" },
        { $"\"id\":\"{Told.Id}\"", "\"id\":\"1\"" },
        { $"\"id\":\"{Told.Id}\"", "\"id\":\"00000000-0000-0000-0000-000000000000\"" },
        { $"\"id\":\"{Told.Id}\"", $"\"id\":\"{Asked.Id}\"" },
        { "\"seq\":3", "\"seq\":1" },
        { "\"seq\":3", "\"seq\":-3" },
        { "\"seq\":3", "\"seq\":2147483647" },
        { "\"seq\":3", "\"seq\":\"3\"" },
        { "\"role\":\"answer\"", "\"role\":\"system\"" },
        { "\"state\":\"interrupted\"", "\"state\":\"streaming\"" },
        { "\"state\":\"interrupted\"", "\"state\":\"cancelled\"" },
        { "\"state\":\"failed\"", "\"state\":null" },
        { "\"text\":\"So [1][2].\"", "\"text\":\" \"" },
        { "\"text\":\"So [1][2].\"", "\"text\":null" },
        { "\"text\":\"So [1][2].\"", "\"text\":\"\\ud800\"" },
        { "\"attachments\":[", "\"attachments\":[7," },
        { "\"web_searches\":3", "\"web_searches\":-1" },
        { "\"web_searches\":3", "\"searches\":3" },
        { "\"number\":1", "\"number\":0" },
        { "\"number\":1", "\"number\":1000" },
        { "\"number\":1", "\"number\":2" },
        { "\"kind\":\"web\"", "\"kind\":\"file\"" },
        { "\"url\":\"https:", "\"url\":\"file:" },
        { "\"url\":\"https:", "\"url\":\"javascript:" },
        { "\"url\":\"https://example.org/a", "\"url\":\"a" },
        { $"\"replaced_by\":\"{Told.Id}\"", $"\"replaced_by\":\"{Asked.Id}\"" },
        { $"\"message\":\"{Earlier.Id}\"", $"\"message\":\"{Asked.Id}\"" },
        { $"\"message\":\"{Earlier.Id}\"", "\"message\":\"0190f000-0000-7000-8000-000000000999\"" },
        { "\"quote\":\"Earlier\"", "\"quote\":\" \"" },
        { "\"quote\":\"Earlier\"", "\"quote\":7" },
        { "\"offset\":12", "\"offset\":-1" },
        { "\"block\":0", "\"block\":\"0\"" },
        { "\"to_seq\":7", "\"to_seq\":-1" },
        { "\"to_seq\":7", "\"upto\":7" },
        { "\"body\":\"The text of Trains\"", "\"body\":\"\"" },
        { "\"title\":\"Trains\"", "\"title\":\"\\u0007\"" },
        { "\"note\":\"关联\"", "\"note\":\"more than sixteen characters\"" },
        { "\"at\":\"2026", "\"at\":\"1026" },
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public async Task Damaged_contents_are_refused(string was, string becomes)
    {
        Assert.Equal(ImportRefusal.Damaged, (await ReadAsync(Changed(was, becomes))).Refusal);
    }

    [Fact]
    public async Task What_is_over_the_apps_limits_is_damaged()
    {
        ExchangeDocument With(ExchangeConversation conversation) => Shared.Document(Root, conversation);
        foreach (var document in new[]
        {
            With(Branch with { Content = Branch.Content! with { Source = Shared.From(Told, new string('x', BranchRules.MaxQuoteLength + 1)) } }),
            With(Branch with { Content = Branch.Content! with { Source = Branch.Content.Source! with { Anchor = new(1, "p", new string('1', BranchRules.MaxAnchorLength + 1)) } } }),
            With(Branch.And(Shared.Question(2, new string('x', Application.Conversations.ConversationService.MaxQuestionLength + 1)))),
            With(Branch.And(Shared.Question(2, "?", [.. Enumerable.Repeat("a", Application.Resources.ResourceLimits.MaxAttachmentsPerMessage + 1)]))),
            With(Branch.And(Shared.Question(2, "?"), Shared.Answer(3, new string('x', ImportLimits.MaxTextLength + 1)))),
        })
        {
            Assert.Equal(ImportRefusal.Damaged, (await ReadAsync(SharePage.Format(document, new PlainMarkdown()))).Refusal);
        }
    }

    /// <summary>A NUL would end a text for the database's own checks; Markdown reads it as U+FFFD.</summary>
    [Fact]
    public async Task A_nul_character_is_replaced()
    {
        var read = await ReadAsync(Changed("\"text\":\"So [1][2].\"", "\"text\":\"\\u0000So\""));
        Assert.Equal("\uFFFDSo", read.Document!.Conversations[0].Content!.Messages[^1].Text);
    }

    private static string Changed(string was, string becomes)
    {
        Assert.Contains(was, Html, StringComparison.Ordinal);
        return Html.Replace(was, becomes, StringComparison.Ordinal);
    }

    private async Task<SharedFileReading> ReadAsync(string html)
    {
        var path = Path.Combine(_dir, "share.html");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(html), Ct);
        return await new SharedFileReader().ReadAsync(path, Ct);
    }
}
