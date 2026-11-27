using System.Text;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Import;
using Mindweave.Infrastructure.Share;

namespace Mindweave.Infrastructure.Tests.Import;

/// <summary>Reading a shared file back (docs/design/sharing-import.md §1, §7): only its data block, and only what can be true.</summary>
public sealed class SharedFileReaderTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 18, 11, 42, 30, 120, TimeSpan.Zero);

    private static readonly ExchangeConversation Root = new(ConversationId.Parse("0190f000-0000-7000-8000-000000000101"), null, "Trains </script> & “gauges” 火车", Day, 3, true);
    private static readonly ExchangeConversation Branch = new(ConversationId.Parse("0190f000-0000-7000-8000-000000000102"), Root.Id, null, Day.AddMinutes(9), 1, true);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-import-reader", Guid.NewGuid().ToString("N"));

    public SharedFileReaderTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ExchangeDocument Document => new(ExchangeDocument.StructureKind, "How trains <came> to be", Day.AddDays(7), "0.7.0", [Root, Branch], [new ExchangeLink(Root.Id, Branch.Id)]);

    [Fact]
    public async Task A_structure_only_share_is_read_as_it_was_written()
    {
        var read = await ReadAsync(SharePage.Encode(Document));

        Assert.Null(read.Refusal);
        Assert.Equal((Document.Kind, Document.Title, Document.MadeAt, Document.AppVersion), (read.Document!.Kind, read.Document.Title, read.Document.MadeAt, read.Document.AppVersion));
        Assert.True(read.Document.SameContents(Document));
    }

    /// <summary>Mail and version control can change a file's line ends, and some editors add a byte order mark: the data block is one line.</summary>
    [Fact]
    public async Task Line_ends_and_a_byte_order_mark_do_not_matter()
    {
        var html = SharePage.Format(Document);
        var windows = html.ReplaceLineEndings("\r\n");
        Assert.NotEqual(html, windows);

        foreach (var variant in new[] { windows, windows.Replace("{\"format\"", "\r\n{\r\n\"format\"", StringComparison.Ordinal) })
        {
            var read = await ReadAsync([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(variant)]);
            Assert.Null(read.Refusal);
            Assert.True(read.Document!.SameContents(Document));
        }
    }

    /// <summary>The page's own script names the block too, and a title can hold the same words: only the element counts.</summary>
    [Fact]
    public async Task Only_the_data_block_is_read()
    {
        var planted = Document with { Title = "<script type=\"application/json\" id=\"mindweave-export\">{}</script>" };
        var read = await ReadAsync(SharePage.Encode(planted));

        Assert.Equal(planted.Title, read.Document?.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<!doctype html><title>A page</title>")]
    [InlineData("<script type=\"application/json\" id=\"page-text\">{}</script>")]
    [InlineData("<script type=\"application/json\" id=\"mindweave-export\">{\"format\":\"other\",\"version\":1}</script>")]
    public async Task A_file_of_another_kind_is_not_a_share(string text)
    {
        Assert.Equal(ImportRefusal.NotAShare, await RefusalAsync(text));
    }

    [Fact]
    public async Task A_picture_or_an_archive_is_not_a_share()
    {
        Assert.Equal(ImportRefusal.NotAShare, (await ReadAsync([0x89, 0x50, 0x4e, 0x47, 0, 0xff, 0xfe, 0xc0])).Refusal);
        Assert.Equal(ImportRefusal.NotAShare, (await ReadAsync([0x50, 0x4b, 3, 4, 0xff])).Refusal);
    }

    [Fact]
    public async Task A_newer_format_is_refused_whatever_else_is_in_it()
    {
        Assert.Equal(ImportRefusal.NewerFormat, await RefusalAsync(Changed("\"version\":1", "\"version\":2")));
        Assert.Equal(ImportRefusal.NewerFormat, await RefusalAsync("<script id=\"mindweave-export\">{\"format\":\"mindweave-export\",\"version\":99,\"kind\":[1]}</script>"));
    }

    [Fact]
    public async Task A_device_copy_is_refused_by_name()
    {
        Assert.Equal(ImportRefusal.DeviceCopy, await RefusalAsync(Changed("\"kind\":\"structure\"", "\"kind\":\"device\"")));
    }

    [Fact]
    public async Task A_share_that_says_it_has_content_and_has_none_is_damaged()
    {
        Assert.Equal(ImportRefusal.Damaged, await RefusalAsync(Changed("\"kind\":\"structure\"", "\"kind\":\"content\"")));
    }

    [Fact]
    public async Task Fields_this_version_does_not_know_are_passed_over()
    {
        var read = await ReadAsync(Encoding.UTF8.GetBytes(Changed("\"questions\":3", "\"later\":{\"x\":[1]},\"questions\":3")));
        Assert.True(read.Document!.SameContents(Document));
    }

    public static TheoryData<string, string> Damage => new()
    {
        { "\"conversations\":[", "\"conversations\":[[" },
        { "\"conversations\":[", "\"conversations\":[1," },
        { "\"conversations\":[", "\"others\":[" },
        { "\"links\":[", "\"others\":[" },
        { "\"version\":1", "\"version\":\"1\"" },
        { "\"version\":1", "\"version\":0" },
        { "\"version\":1", "\"version\":1.5" },
        { "\"kind\":\"structure\"", "\"kind\":\"something\"" },
        { "\"kind\":\"structure\"", "\"kind\":7" },
        { "\"title\":\"How", "\"title\":\"\\ud800" },
        { "\"title\":\"How", "\"heading\":\"How" },
        { "\"made_at\":\"2026", "\"made_at\":\"20x6" },
        { "\"started_at\":\"2026-11-18T11:42:30.120Z\"", "\"started_at\":\"yesterday\"" },
        { "\"started_at\":\"2026-11-18T11:42:30.120Z\"", "\"started_at\":\"0001-01-01T00:00:00.000Z\"" },
        { "\"started_at\":\"2026-11-18T11:42:30.120Z\"", "\"started_at\":\"9999-12-31T00:00:00.000Z\"" },
        { "\"questions\":3", "\"questions\":-1" },
        { "\"questions\":3", "\"questions\":99999999999" },
        { "\"has_summary\":true", "\"has_summary\":\"yes\"" },
        { $"\"id\":\"{Root.Id}\"", "\"id\":\"1\"" },
        { $"\"id\":\"{Root.Id}\"", "\"id\":\"00000000-0000-0000-0000-000000000000\"" },
        { $"\"id\":\"{Root.Id}\"", $"\"id\":\"{Branch.Id}\"" }, // twice in the file, and its own parent's name gone
        { $"\"parent\":\"{Root.Id}\"", $"\"parent\":\"{Branch.Id}\"" }, // its own parent
        { $"\"parent\":\"{Root.Id}\"", "\"parent\":\"up\"" },
        { $"\"a\":\"{Root.Id}\"", "\"a\":\"x\"" },
        { $"\"a\":\"{Root.Id}\"", "\"a\":\"0190f000-0000-7000-8000-000000000999\"" }, // a link to what is not in the file
        { "\"parent\"", "\"messages\":[],\"parent\"" }, // message content in a structure-only share
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public async Task Damaged_contents_are_refused(string was, string becomes)
    {
        Assert.Equal(ImportRefusal.Damaged, await RefusalAsync(Changed(was, becomes)));
    }

    [Fact]
    public async Task A_file_cut_short_is_damaged()
    {
        var html = SharePage.Format(Document);
        Assert.Equal(ImportRefusal.Damaged, await RefusalAsync(html[..(html.IndexOf("\"conversations\"", StringComparison.Ordinal) + 30)]));
    }

    [Fact]
    public async Task A_name_the_app_could_not_have_made_is_damaged()
    {
        Assert.Equal(ImportRefusal.Damaged, (await ReadAsync(SharePage.Encode(Document with { Conversations = [Root with { Name = new string('x', ImportLimits.MaxNameLength + 1) }, Branch] }))).Refusal);
    }

    /// <summary>The share's own title is typed and has no limit; only what is kept of it is cut.</summary>
    [Fact]
    public async Task A_very_long_title_is_no_damage()
    {
        Assert.Null((await ReadAsync(SharePage.Encode(Document with { Title = new string('x', 5 * ImportLimits.MaxNameLength) }))).Refusal);
    }

    [Fact]
    public async Task A_file_full_of_the_blocks_name_is_read_through_once()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("id=\"mindweave-export\"", 250_000)));
        var path = Path.Combine(_dir, "many.html");
        await File.WriteAllBytesAsync(path, bytes, Ct);

        // Off the test's thread: a scan that starts over at every name would take most of a minute.
        var read = await Task.Run(() => new SharedFileReader().ReadAsync(path, Ct), Ct).WaitAsync(TimeSpan.FromMilliseconds(Mindweave.Testing.TestWaits.Ms(10_000)), Ct);

        Assert.Equal(ImportRefusal.NotAShare, read.Refusal);
    }

    [Fact]
    public async Task Two_conversations_that_are_each_others_parents_are_damaged()
    {
        Assert.Equal(ImportRefusal.Damaged, (await ReadAsync(SharePage.Encode(Document with { Conversations = [Root with { Parent = Branch.Id }, Branch] }))).Refusal);
    }

    [Fact]
    public async Task A_share_without_a_conversation_is_damaged()
    {
        Assert.Equal(ImportRefusal.Damaged, (await ReadAsync(SharePage.Encode(Document with { Conversations = [], Links = [] }))).Refusal);
    }

    [Fact]
    public async Task Branches_nested_beyond_any_real_path_are_damaged()
    {
        ExchangeDocument Chain(int length)
        {
            var chain = new List<ExchangeConversation> { Root };
            while (chain.Count < length)
            {
                chain.Add(new ExchangeConversation(ConversationId.New(), chain[^1].Id, null, Day, 0, false));
            }

            chain.Reverse();
            return Document with { Conversations = chain, Links = [] };
        }

        Assert.Null((await ReadAsync(SharePage.Encode(Chain(ImportLimits.MaxDepth)))).Refusal);
        Assert.Equal(ImportRefusal.Damaged, (await ReadAsync(SharePage.Encode(Chain(ImportLimits.MaxDepth + 1)))).Refusal);
    }

    [Fact]
    public async Task A_file_with_too_many_conversations_or_bytes_is_refused_unread()
    {
        var many = Enumerable.Range(0, ImportLimits.MaxConversations + 1).Select(_ => new ExchangeConversation(ConversationId.New(), null, null, Day, 0, false)).ToList();
        Assert.Equal(ImportRefusal.TooLarge, (await ReadAsync(SharePage.Encode(Document with { Conversations = many, Links = [] }))).Refusal);

        var path = Path.Combine(_dir, "large.html");
        using (var file = File.Create(path))
        {
            file.SetLength(ImportLimits.MaxFileBytes + 1);
        }

        Assert.Equal(ImportRefusal.TooLarge, (await new SharedFileReader().ReadAsync(path, Ct)).Refusal);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_says_so()
    {
        Assert.Equal(ImportRefusal.Unreadable, (await new SharedFileReader().ReadAsync(Path.Combine(_dir, "gone.html"), Ct)).Refusal);
        Assert.Equal(ImportRefusal.Unreadable, (await new SharedFileReader().ReadAsync(_dir, Ct)).Refusal);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Changed(string was, string becomes)
    {
        var html = SharePage.Format(Document);
        var at = html.IndexOf(was, StringComparison.Ordinal);
        Assert.True(at >= 0, was);
        return html[..at] + becomes + html[(at + was.Length)..];
    }

    private async Task<ImportRefusal?> RefusalAsync(string text)
    {
        var read = await ReadAsync(Encoding.UTF8.GetBytes(text));
        Assert.Equal(read.Refusal is null, read.Document is not null);
        return read.Refusal;
    }

    private async Task<SharedFileReading> ReadAsync(byte[] bytes)
    {
        var path = Path.Combine(_dir, "share.html");
        await File.WriteAllBytesAsync(path, bytes, Ct);
        return await new SharedFileReader().ReadAsync(path, Ct);
    }
}
