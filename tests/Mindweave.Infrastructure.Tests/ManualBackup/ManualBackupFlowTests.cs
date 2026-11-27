using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Export;
using Mindweave.Application.ManualBackup;
using Mindweave.Application.Models;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Share;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Reading;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Export;
using Mindweave.Infrastructure.ManualBackup;
using Mindweave.Infrastructure.Share;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Infrastructure.Tests.Share;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.ManualBackup;

/// <summary>Saving a manual backup through the real stores and the file (docs/design/sharing-backup.md §8, scenarios 1 to 3).</summary>
public sealed partial class ManualBackupFlowTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The connection's name, address and key are planted; the model's name is in the copy, as text.</summary>
    private static readonly ModelSettingsInput ToolModel = new(
        "PLANTEDCONNECTION", "https://planted-service.example/v1", "backup-model-1", "sk-PLANTEDKEY",
        Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-manual-backup", Guid.NewGuid().ToString("N"));

    public ManualBackupFlowTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Target => Path.Combine(_dir, "Mindweave 2026-11-27.mwbackup");

    /// <summary>
    /// Scenarios 1 and 3, and the marker test: what the copy holds is in it, and a marker planted in a key, a connection
    /// address, an MCP server's setting, a draft, a recommendation, a summary's candidate and an unticked workspace is
    /// nowhere in what it holds.
    /// </summary>
    [Fact]
    public async Task The_package_holds_the_ticked_workspaces_in_full_and_nothing_that_stays_home()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        await h.WebSearch.SetKeyAsync("tvly-PLANTEDSEARCHKEY-1234567890", Ct);
        await h.Mcp.AddAsync(FakeMcp.Command("PLANTEDSERVER"), Ct);

        // A question with a file, answered with a web search and a page read.
        h.Search.Returns(new SearchHit("Cited page", new Uri("https://example.org/cited"), "A snippet of the cited page"));
        h.Pages.Page("https://example.org/cited", "Cited page", "The saved text of the page");
        h.Client.CallTool(WebSearchTool.ToolName, JsonSerializer.Serialize(new { query = "how trains came to be" }));
        h.Client.CallTool(FetchPageTool.ToolName, JsonSerializer.Serialize(new { url = "https://example.org/cited" }));
        h.Client.Reply("The standing answer, from the web[1].");
        var path = Path.Combine(_dir, "gauge-table.txt");
        await File.WriteAllTextAsync(path, "The original of the attached file", Ct);
        var file = (await h.ResourceService.AttachFileAsync(DraftSlot.NewExploration(h.Workspace), path, Ct)).Resource!;
        h.Client.Titles.Replies.Enqueue("Root title");
        var sent = await h.Conversations.StartAsync(h.Workspace, "The question asked?", [file.Resource.Id], null, new SendOptions(WebSearchMode.Auto), Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        var root = sent.Conversation;
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value, timeoutMs: 20_000);
        await ChatHarness.UntilAsync(async () => (await h.Store.GetAsync(root, Ct))?.Title == "Root title");

        // An answer tried again: both attempts travel.
        var replaced = await AskAsync(h, root, "A second question?", "The first attempt, replaced");
        h.Client.Reply("The second attempt, which stands.");
        var retried = await h.Conversations.RetryAsync(root, replaced.Id, Ct);
        await h.WaitForAnswerAsync(retried.Answer!.Value);

        // A branch, archived.
        var branch = (await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(answer, "standing answer"), Ct)).Child;
        await AskAsync(h, branch, "The branch question?", "The branch answer.");
        await h.Archive.ArchiveAsync(branch, Now, Ct);

        // A summary with an older version, the current one and a candidate.
        await SummarizeAsync(h, root, "Title: The older title\nShort name: Older\n\nThe older body of the summary");
        var node = (await h.SummaryService.ForConversationAsync(root, Ct))!.Node;
        await SummarizeAsync(h, root, "Title: The current title\nShort name: Trains\n\nThe current body");
        await h.SummaryService.AdoptAsync(node.Id, (await h.SummaryService.ForConversationAsync(root, Ct))!.Node.Pending!.Value, Ct);
        await SummarizeAsync(h, root, "Title: PLANTEDCANDIDATE title\nShort name: PLANTEDCANDIDATENAME\n\nPLANTEDCANDIDATE body");

        // What stays home: a draft, a recommendation, and a whole workspace left unticked.
        await h.ReadingStore.SaveDraftAsync(root, ViewerSlotKey.Primary, "PLANTEDDRAFT text", Now, Ct);
        await h.RecommendationStore.AddBatchAsync(h.Workspace, RecommendationKind.Question, new RecommendationBatch(Guid.CreateVersion7(), Now, [new NewRecommendation("PLANTEDRECOMMENDATION?", null, null)]), false, Ct);
        var other = await h.WorkspaceService.CreateAsync(WorkspaceName.FromStored("PLANTEDUNTICKEDWORKSPACE"), Ct);
        h.Client.Reply("PLANTEDUNTICKED answer");
        var elsewhere = await h.Conversations.StartAsync(other.Id, "PLANTEDUNTICKED question?", Ct);
        await h.WaitForAnswerAsync(elsewhere.Answer!.Value);

        var saved = await Backups(h).SaveAsync([h.Workspace], new ExportDestination("backup", Target, null), null, Ct);

        Assert.Equal((1, 2, 0), (saved.Workspaces, saved.Conversations, saved.MissingOriginals));
        var entries = Entries(Target);
        var all = string.Join("\n", entries.Select(e => e.Key + "\n" + Encoding.UTF8.GetString(e.Value)));

        // What the copy holds.
        foreach (var kept in new[]
        {
            "The question asked?", "The standing answer, from the web[1].", "The first attempt, replaced", "The second attempt, which stands.",
            "The branch question?", "The branch answer.", "standing answer", "The older body of the summary", "The current body",
            "The original of the attached file", "The saved text of the page", "A snippet of the cited page", "gauge-table.txt",
            "https://example.org/cited", "backup-model-1", "how trains came to be",
        })
        {
            Assert.Contains(kept, all, StringComparison.Ordinal);
        }

        Assert.Equal(Encoding.UTF8.GetBytes("The original of the attached file"), entries[ManualBackupPackage.OriginalsFolder + file.Resource.Hash.Hex]);

        // What it does not.
        Assert.DoesNotContain("PLANTED", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("planted-service.example", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(other.Id.ToString(), all, StringComparison.Ordinal);

        // Stored values, as stored.
        using var document = JsonDocument.Parse(entries[ManualBackupPackage.DocumentEntry(h.Workspace)]);
        var conversations = document.RootElement.GetProperty("conversations").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
        Assert.Equal("device", document.RootElement.GetProperty("kind").GetString());
        Assert.True(conversations[branch.ToString()].GetProperty("archived").GetBoolean());
        Assert.False(conversations[root.ToString()].TryGetProperty("archived", out _));
        var messages = conversations[root.ToString()].GetProperty("messages").EnumerateArray().ToList();
        Assert.Contains(messages, m => m.TryGetProperty("supersedes", out var s) && s.GetString() == replaced.Id.ToString());
        Assert.Contains(messages, m => m.TryGetProperty("run", out _));
        var history = conversations[root.ToString()].GetProperty("summary_history");
        Assert.Equal(2, history.GetProperty("versions").GetArrayLength());
        Assert.Equal(node.Id.ToString(), history.GetProperty("node").GetString());
        Assert.All(document.RootElement.GetProperty("resources").EnumerateArray(), r => Assert.Equal("present", r.GetProperty("original").GetString()));
    }

    /// <summary>Scenario 1: a copy with one workspace unticked holds nothing of it, and one of all holds both.</summary>
    [Fact]
    public async Task A_copy_of_all_workspaces_holds_each_and_an_unticked_one_holds_nothing_of_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var first = await h.Conversations.StartAsync(h.Workspace, "First workspace question?", Ct);
        await h.WaitForAnswerAsync(first.Answer!.Value);
        var other = await h.WorkspaceService.CreateAsync(WorkspaceName.FromStored("Second workspace"), Ct);
        var second = await h.Conversations.StartAsync(other.Id, "Second workspace question?", Ct);
        await h.WaitForAnswerAsync(second.Answer!.Value);

        var listed = await Backups(h).ListAsync(Ct);
        Assert.Equal(new[] { h.Workspace, other.Id }.ToHashSet(), listed.Select(w => w.Id).ToHashSet());
        Assert.All(listed, w => Assert.Equal(1, w.Conversations));
        Assert.All(listed, w => Assert.True(w.Bytes > 0));

        await Backups(h).SaveAsync([.. listed.Select(w => w.Id)], new ExportDestination("backup", Target, null), null, Ct);
        var both = string.Join("\n", Entries(Target).Select(e => e.Key + Encoding.UTF8.GetString(e.Value)));
        Assert.Contains("First workspace question?", both, StringComparison.Ordinal);
        Assert.Contains("Second workspace question?", both, StringComparison.Ordinal);

        await Backups(h).SaveAsync([other.Id], new ExportDestination("backup", Target, null), null, Ct);
        var one = string.Join("\n", Entries(Target).Select(e => e.Key + Encoding.UTF8.GetString(e.Value)));
        Assert.Contains("Second workspace question?", one, StringComparison.Ordinal);
        Assert.DoesNotContain("First workspace question?", one, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Workspace.ToString(), one, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Conversation.ToString(), one, StringComparison.Ordinal);
    }

    /// <summary>§4: only the expected entries, each original under its own hash and matching it; the pages load nothing (scenario 2).</summary>
    [Fact]
    public async Task The_package_has_only_its_known_entries_and_every_original_matches_its_hash()
    {
        await using var h = await ChatHarness.StartAsync();
        var image = Path.Combine(_dir, "table.csv");
        await File.WriteAllTextAsync(image, "year,gauge\n1825,1435\n", Ct);
        var text = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(text, "Some notes", Ct);
        var slot = DraftSlot.NewExploration(h.Workspace);
        var attached = new[]
        {
            (await h.ResourceService.AttachFileAsync(slot, image, Ct)).Resource!.Resource.Id,
            (await h.ResourceService.AttachFileAsync(slot, text, Ct)).Resource!.Resource.Id,
        };
        var sent = await h.Conversations.StartAsync(h.Workspace, "Two files?", attached, null, new SendOptions(WebSearchMode.Off), Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var other = await h.WorkspaceService.CreateAsync(WorkspaceName.FromStored("Empty"), Ct);

        var saved = await Backups(h).SaveAsync([h.Workspace, other.Id], new ExportDestination("backup", Target, null), null, Ct);

        Assert.Equal(2, saved.Originals);
        var entries = Entries(Target);
        Assert.All(entries.Keys, name => Assert.Matches(EntryName(), name));
        Assert.Equal(
            new[] { ManualBackupPackage.ManifestEntry, ManualBackupPackage.IndexEntry, ManualBackupPackage.DocumentEntry(h.Workspace), ManualBackupPackage.PageEntry(h.Workspace), ManualBackupPackage.DocumentEntry(other.Id), ManualBackupPackage.PageEntry(other.Id) }.Order(),
            entries.Keys.Where(k => !k.StartsWith(ManualBackupPackage.OriginalsFolder, StringComparison.Ordinal)).Order());
        var originals = entries.Where(e => e.Key.StartsWith(ManualBackupPackage.OriginalsFolder, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, originals.Count);
        Assert.All(originals, o => Assert.Equal(o.Key[ManualBackupPackage.OriginalsFolder.Length..], Convert.ToHexStringLower(SHA256.HashData(o.Value))));

        using var manifest = JsonDocument.Parse(entries[ManualBackupPackage.ManifestEntry]);
        Assert.Equal(("mindweave-export", 1, "device"), (manifest.RootElement.GetProperty("format").GetString(), manifest.RootElement.GetProperty("version").GetInt32(), manifest.RootElement.GetProperty("kind").GetString()));
        Assert.Equal(2, manifest.RootElement.GetProperty("workspaces").GetArrayLength());

        // Unpacked, every page reads with the network off: nothing in it loads from anywhere, and the entry page leads to each.
        var index = Encoding.UTF8.GetString(entries[ManualBackupPackage.IndexEntry]);
        foreach (var workspace in new[] { h.Workspace, other.Id })
        {
            Assert.Contains("href=\"" + ManualBackupPackage.PageEntry(workspace) + "\"", index, StringComparison.Ordinal);
            var page = Encoding.UTF8.GetString(entries[ManualBackupPackage.PageEntry(workspace)]);
            Assert.Contains("default-src 'none'", page, StringComparison.Ordinal);
            Assert.DoesNotMatch(LoadsFromAnywhere(), page);
        }

        Assert.Contains("Two files?", Encoding.UTF8.GetString(entries[ManualBackupPackage.PageEntry(h.Workspace)]), StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", index, StringComparison.Ordinal);
        Assert.DoesNotMatch(LoadsFromAnywhere(), index);
    }

    /// <summary>§6: an original missing on this device is recorded as such, and the save goes on.</summary>
    [Fact]
    public async Task A_missing_original_is_recorded_as_missing_and_does_not_fail_the_save()
    {
        await using var h = await ChatHarness.StartAsync();
        var path = Path.Combine(_dir, "gone.txt");
        await File.WriteAllTextAsync(path, "An original that will be lost", Ct);
        var file = (await h.ResourceService.AttachFileAsync(DraftSlot.NewExploration(h.Workspace), path, Ct)).Resource!;
        var sent = await h.Conversations.StartAsync(h.Workspace, "A file?", [file.Resource.Id], null, new SendOptions(WebSearchMode.Off), Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        File.Delete(h.Blobs.PathOf(file.Resource.Hash));

        var saved = await Backups(h).SaveAsync([h.Workspace], new ExportDestination("backup", Target, null), null, Ct);

        Assert.Equal((0, 1), (saved.Originals, saved.MissingOriginals));
        var entries = Entries(Target);
        Assert.DoesNotContain(entries.Keys, k => k.StartsWith(ManualBackupPackage.OriginalsFolder, StringComparison.Ordinal));
        using var document = JsonDocument.Parse(entries[ManualBackupPackage.DocumentEntry(h.Workspace)]);
        var resource = Assert.Single(document.RootElement.GetProperty("resources").EnumerateArray());
        Assert.Equal(("missing", "gone.txt", "An original that will be lost"), (resource.GetProperty("original").GetString(), resource.GetProperty("name").GetString(), resource.GetProperty("text").GetString()));
    }

    /// <summary>§5: a cancelled save leaves no file, not even a temporary one, and an earlier file of that name stays as it was.</summary>
    [Fact]
    public async Task A_cancelled_save_leaves_no_file()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "A question?", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var other = await h.WorkspaceService.CreateAsync(WorkspaceName.FromStored("Second"), Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Backups(h).SaveAsync(
            [h.Workspace, other.Id], new ExportDestination("backup", Target, null), new AtOnce<ManualBackupProgress>(p => { if (p.Done == 1) { cancel.Cancel(); } }), cancel.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));

        await File.WriteAllTextAsync(Target, "an earlier file", Ct);
        using var again = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Backups(h).SaveAsync(
            [h.Workspace, other.Id], new ExportDestination("backup", Target, null), new AtOnce<ManualBackupProgress>(p => { if (p.Done == 1) { again.Cancel(); } }), again.Token));
        Assert.Equal(["an earlier file"], [await File.ReadAllTextAsync(Target, Ct)]);
        Assert.Single(Directory.EnumerateFileSystemEntries(_dir));
    }

    /// <summary>§6: a save that fails halfway (here, reading the second workspace) leaves no file.</summary>
    [Fact]
    public async Task A_failed_save_leaves_no_file()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "A question?", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var package = new ManualBackupPackage(h.Blobs, new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance), new SharePageWriter(new PlainMarkdown()));
        var part = new ManualBackupPart(
            new ExchangeDocument(ExchangeDocument.DeviceKind, "W", Now, "0.7.0", [], []) { Workspace = new ExchangeWorkspace(h.Workspace, "W", Now) },
            new ExchangeDocument(ExchangeDocument.ContentKind, "W", Now, "0.7.0", [], []));
        var reads = 0;

        var failure = await Assert.ThrowsAsync<ExportWriteException>(() => package.WriteAsync(
            Now, "0.7.0", [h.Workspace, h.Workspace],
            (_, _) => ++reads == 1 ? Task.FromResult<ManualBackupPart?>(part) : throw new IOException("The disk is full."),
            new ExportDestination("backup", Target, null), null, Ct));

        Assert.Equal((2, ExportFailure.Failed), (reads, failure.Failure));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }

    /// <summary>An answer still being written is not in the copy, nor its question.</summary>
    [Fact]
    public async Task An_answer_still_being_written_is_left_out_with_its_question()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "A finished question?", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var held = h.Client.Hold("PLANTEDPARTIAL text so far");
        var asked = await h.Conversations.AskAsync(sent.Conversation, "A question still being answered?", Ct);
        await held.Started.Task.WaitAsync(Ct);

        await Backups(h).SaveAsync([h.Workspace], new ExportDestination("backup", Target, null), null, Ct);
        held.Release.SetResult();

        var all = string.Join("\n", Entries(Target).Select(e => Encoding.UTF8.GetString(e.Value)));
        Assert.Contains("A finished question?", all, StringComparison.Ordinal);
        Assert.DoesNotContain("A question still being answered?", all, StringComparison.Ordinal);
        Assert.DoesNotContain("PLANTEDPARTIAL", all, StringComparison.Ordinal);
        Assert.DoesNotContain(asked.Answer!.Value.ToString(), all, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal static ManualBackupService Backups(ChatHarness h)
    {
        var markdown = new PlainMarkdown();
        var pages = new SharePageWriter(markdown);
        var files = new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance);
        var links = new SqliteSummaryLinkStore(h.Database);
        var shares = new ShareService(h.Store, h.SummaryNodes, new SqliteSummaryNetworkStore(h.Database), links, pages, files, NullLogger<ShareService>.Instance);
        return new ManualBackupService(
            h.Workspaces, h.Store, h.Branches, h.SummaryNodes, links, h.Resources, h.Runs, new SqliteImportStore(h.Database),
            new SqliteManualBackupStore(h.Database), shares, ShareContentFlowTests.Contents(h), new ManualBackupPackage(h.Blobs, files, pages),
            new AppEnvironment("dev", "0.7.0", h.DataDirectory, h.DatabasePath, h.BackupDirectory, h.DataDirectory, 19), new FixedTime(Now),
            NullLogger<ManualBackupService>.Instance);
    }

    internal static Dictionary<string, byte[]> Entries(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        });
    }

    private static async Task<Message> AskAsync(ChatHarness h, ConversationId id, string question, string reply)
    {
        h.Client.Reply(reply);
        return await h.AskAsync(id, question);
    }

    private static async Task SummarizeAsync(ChatHarness h, ConversationId id, string reply)
    {
        h.Client.Knowledge.Replies.Enqueue(reply);
        var before = await h.SummaryService.LatestTaskAsync(id, Ct);
        Assert.True((await h.SummaryService.SummarizeAsync(id, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(id, Ct) is { State: GenerationTaskState.Completed } done && done.Id != before?.Id);
    }

    [GeneratedRegex(@"^(manifest\.json|index\.html|workspaces/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/(document\.json|index\.html)|originals/[0-9a-f]{64})$")]
    private static partial Regex EntryName();

    /// <summary>A script, style, image, frame or font that a browser would fetch by itself.</summary>
    [GeneratedRegex(@"<(script|img|iframe|link|source|video|audio)[^>]*\s(src|href)\s*=\s*""?(https?:|//)", RegexOptions.IgnoreCase)]
    private static partial Regex LoadsFromAnywhere();

    /// <summary>Reports at once, on the thread that reports: <see cref="Progress{T}"/> would post it later.</summary>
    private sealed class AtOnce<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
