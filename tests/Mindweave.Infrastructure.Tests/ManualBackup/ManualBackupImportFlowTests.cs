using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Export;
using Mindweave.Application.Import;
using Mindweave.Application.ManualBackup;
using Mindweave.Application.Models;
using Mindweave.Application.Share;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Reading;
using Mindweave.Core.Runs;
using Mindweave.Core.Resources;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Export;
using Mindweave.Infrastructure.Import;
using Mindweave.Infrastructure.ManualBackup;
using Mindweave.Infrastructure.Share;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Infrastructure.Tests.Share;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.ManualBackup;

/// <summary>Importing a manual backup through the real stores and the package (docs/design/sharing-backup.md §8, scenarios 4 to 8 and 12).</summary>
public sealed class ManualBackupImportFlowTests : IDisposable
{
    private static readonly ModelSettingsInput ToolModel = new(
        "fake", "https://fake.example/v1", "backup-model-1", "sk-test",
        Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-backup-import", Guid.NewGuid().ToString("N"));

    public ManualBackupImportFlowTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    /// <summary>
    /// Scenario 4, the round trip: a folder with attachments, saved pages, runs, branches, summaries with history and links, and
    /// received conversations is saved and imported into an empty one; saved again there, its document is byte for byte the
    /// first one's. A branch still returns to its sentence, a citation opens its saved page, and the received one is read-only.
    /// </summary>
    [Fact]
    public async Task A_backup_imported_into_an_empty_folder_holds_everything_it_held()
    {
        await using var a = await ChatHarness.StartAsync(model: ToolModel);
        var made = await MakeAsync(a);
        var first = await SaveAsync(a, "a");

        await using var b = await ChatHarness.StartAsync(model: ToolModel);
        var read = await ReadAsync(b, first);
        Assert.Equal(0, read.Mismatched);
        var plan = await Importer(b).PlanAsync(read, Ct);
        var workspace = Assert.Single(plan.Workspaces);
        Assert.Equal((true, 3, 0, 0, 0), (workspace.Create, workspace.New, workspace.GainsMessages, workspace.Joined, workspace.AlreadyHere));
        Assert.False(plan.IsBlocked);

        var imported = await Importer(b).ImportAsync(read, Ct);

        Assert.Equal(ManualBackupImportStatus.Imported, imported.Status);
        Assert.Equal([a.Workspace], imported.Created.Select(w => w.Id));
        Assert.Equal(0, imported.MissingOriginals);
        var again = await SaveAsync(b, "b");
        Assert.Equal(Document(first, a.Workspace), Document(again, a.Workspace));
        Assert.Equal(Workspaces(first), Workspaces(again).Where(w => w.Id != b.Workspace.ToString()));

        // A branch returns to its sentence: its source as stored, and the same context.
        Assert.Equal(await a.Branches.GetSourceAsync(made.Branch, Ct), await b.Branches.GetSourceAsync(made.Branch, Ct));
        Assert.Equal(await RangesAsync(a, made.Branch), await RangesAsync(b, made.Branch));
        var source = await b.Branches.GetSourceAsync(made.Branch, Ct);
        Assert.Contains(await b.Store.GetMessagesAsync(made.Root, Ct), m => m.Id == source!.SourceMessage);

        // A citation opens its saved page, and the attachment its original.
        var blocks = (await b.Store.GetMessagesAsync(made.Root, Ct)).SelectMany(m => m.Blocks).ToList();
        var cited = blocks.OfType<CitationBlock>().First(c => c.Resource is not null);
        Assert.Contains("The saved text of the page", (await b.Resources.GetAsync(cited.Resource!.Value, Ct))!.Text, StringComparison.Ordinal);
        Assert.True(b.Blobs.Exists(made.File));
        using (var original = b.Blobs.OpenRead(made.File)!)
        using (var text = new StreamReader(original))
        {
            Assert.Equal("The original of the attached file", await text.ReadToEndAsync(Ct));
        }

        // The attachment's text can be searched again: its chunks were made anew.
        Assert.True(await CountAsync(b, "SELECT count(*) FROM resource_chunk;") > 0);

        // A received conversation stays received, and a question in it continues in one of the user's own.
        Assert.True((await b.Store.GetAsync(made.Received, Ct))!.IsImported);
        b.Client.Reply("A continuation's answer.");
        var asked = await b.Conversations.AskAsync(made.Received, "And after that?", Ct);
        Assert.True(asked.Accepted);
        Assert.NotEqual(made.Received, asked.Conversation);
        await b.WaitForAnswerAsync(asked.Answer!.Value);
    }

    /// <summary>Scenario 5: the same package again adds nothing and changes nothing.</summary>
    [Fact]
    public async Task The_same_backup_again_changes_nothing()
    {
        await using var a = await ChatHarness.StartAsync(model: ToolModel);
        await MakeAsync(a);
        var package = await SaveAsync(a, "a");
        await using var b = await ChatHarness.StartAsync(model: ToolModel);
        var read = await ReadAsync(b, package);
        Assert.Equal(ManualBackupImportStatus.Imported, (await Importer(b).ImportAsync(read, Ct)).Status);
        var before = await StateAsync(b);

        var plan = await Importer(b).PlanAsync(read, Ct);
        Assert.True(plan.IsEmpty);
        Assert.Equal(3, plan.Workspaces[0].AlreadyHere);
        Assert.Equal(ManualBackupImportStatus.NothingNew, (await Importer(b).ImportAsync(read, Ct)).Status);
        Assert.Equal(before, await StateAsync(b));

        // Nor does importing a folder's own backup into itself.
        var own = await ReadAsync(a, package);
        Assert.True((await Importer(a).PlanAsync(own, Ct)).IsEmpty);
    }

    /// <summary>Scenario 6: more questions, a new branch and a new attachment on the first device; a later backup adds only these.</summary>
    [Fact]
    public async Task A_later_backup_adds_only_what_is_new()
    {
        await using var a = await ChatHarness.StartAsync(model: ToolModel);
        var made = await MakeAsync(a);
        await using var b = await ChatHarness.StartAsync(model: ToolModel);
        await Importer(b).ImportAsync(await ReadAsync(b, await SaveAsync(a, "a")), Ct);

        var later = await AskAsync(a, made.Root, "A later question?", "A later answer.");
        var twig = (await a.Conversations.CreateBranchAsync(made.Root, BranchFlowTests.Selection(later, "later answer"), Ct)).Child;
        var path = Path.Combine(_dir, "later.txt");
        await File.WriteAllTextAsync(path, "A later attachment", Ct);
        var file = (await a.ResourceService.AttachFileAsync(new DraftSlot(a.Workspace, twig, ViewerSlotKey.Primary.ToString()), path, Ct)).Resource!;
        a.Client.Reply("Seen the later file.");
        var sent = await a.Conversations.AskAsync(twig, "And this file?", [file.Resource.Id], new SendOptions(WebSearchMode.Off), Ct);
        await a.WaitForAnswerAsync(sent.Answer!.Value);
        var newer = await SaveAsync(a, "a2");

        var read = await ReadAsync(b, newer);
        var plan = (await Importer(b).PlanAsync(read, Ct)).Workspaces.Single();
        Assert.Equal((false, 1, 1, 0, 2), (plan.Create, plan.New, plan.GainsMessages, plan.Joined, plan.AlreadyHere));
        var gained = plan.Conversations.Single(c => c.Fit == ManualBackupFit.Appends);
        Assert.Equal((made.Root, 2), (gained.Conversation.Id, gained.Arriving.Count));

        await Importer(b).ImportAsync(read, Ct);

        Assert.Equal(Document(newer, a.Workspace), Document(await SaveAsync(b, "b"), a.Workspace));
        Assert.True(b.Blobs.Exists(file.Resource.Hash));
    }

    /// <summary>
    /// Scenario 7: a title, the archived state and the current summary that differ keep this device's value; the
    /// versions this one lacks arrive as further saved versions.
    /// </summary>
    [Fact]
    public async Task What_differs_keeps_this_devices_value()
    {
        await using var a = await ChatHarness.StartAsync(model: ToolModel);
        var made = await MakeAsync(a);
        await using var b = await ChatHarness.StartAsync(model: ToolModel);
        await Importer(b).ImportAsync(await ReadAsync(b, await SaveAsync(a, "a")), Ct);

        // There: renamed, the branch restored from the archive, and a third summary version made current.
        await a.Conversations.RenameAsync(made.Root, "The title there", Ct);
        await a.Archive.RestoreAsync(made.Branch, Ct);
        await SummarizeAsync(a, made.Root, "Title: The third title\nShort name: Third\n\nThe third body");
        var node = (await a.SummaryService.ForConversationAsync(made.Root, Ct))!.Node;
        await a.SummaryService.AdoptAsync(node.Id, node.Pending!.Value, Ct);

        // Here: another title, the root archived, and the older version current.
        await b.Conversations.RenameAsync(made.Root, "The title here", Ct);
        await b.Archive.ArchiveAsync(made.Root, DateTimeOffset.UtcNow, Ct);
        var older = (await b.SummaryNodes.GetAsync(node.Id, Ct))!.Saved[0].Id;
        await b.SummaryService.SetCurrentAsync(node.Id, older, Ct);
        var archived = await ArchivedAsync(b);
        var name = (await b.SummaryNodes.GetAsync(node.Id, Ct))!.Node.DisplayName;

        var imported = await Importer(b).ImportAsync(await ReadAsync(b, await SaveAsync(a, "a2")), Ct);

        Assert.Equal(ManualBackupImportStatus.Imported, imported.Status);
        Assert.Equal("The title here", (await b.Store.GetAsync(made.Root, Ct))!.Title);
        Assert.Equal(archived, await ArchivedAsync(b));
        var detail = (await b.SummaryNodes.GetAsync(node.Id, Ct))!;
        Assert.Equal(older, detail.Node.Current);
        Assert.Equal(name, detail.Node.DisplayName);
        Assert.Equal(3, detail.Saved.Count);
        Assert.Contains(detail.Saved, v => v.Body == "The third body");
    }

    /// <summary>Scenario 8: a conversation deleted here and present in the backup comes back, with its branch and its summary.</summary>
    [Fact]
    public async Task A_conversation_deleted_here_comes_back()
    {
        await using var a = await ChatHarness.StartAsync(model: ToolModel);
        var made = await MakeAsync(a);
        var package = await SaveAsync(a, "a");
        await using var b = await ChatHarness.StartAsync(model: ToolModel);
        await Importer(b).ImportAsync(await ReadAsync(b, package), Ct);
        var scope = await b.ConversationDeletion.InspectAsync([made.Root], Ct);
        await b.ConversationDeletion.DeleteAsync(scope, Ct);
        Assert.Null(await b.Store.GetAsync(made.Root, Ct));

        var imported = await Importer(b).ImportAsync(await ReadAsync(b, package), Ct);

        Assert.Equal(ManualBackupImportStatus.Imported, imported.Status);
        Assert.Equal(Document(package, a.Workspace), Document(await SaveAsync(b, "b"), a.Workspace));
    }

    /// <summary>Scenario 12 and §6: an original that does not match its hash is treated as missing; the rest arrives, and the dialog can say so.</summary>
    [Fact]
    public async Task An_original_that_does_not_match_its_hash_arrives_as_missing()
    {
        await using var a = await ChatHarness.StartAsync(model: ToolModel);
        var made = await MakeAsync(a);
        var package = await SaveAsync(a, "a");
        Rewrite(package, ManualBackupPackage.OriginalsFolder + made.File.Hex, _ => Encoding.UTF8.GetBytes("Not what its name says"));

        await using var b = await ChatHarness.StartAsync(model: ToolModel);
        var read = await ReadAsync(b, package);
        Assert.Equal(1, read.Mismatched);
        Assert.DoesNotContain(made.File, read.Originals);

        var imported = await Importer(b).ImportAsync(read, Ct);

        Assert.Equal((ManualBackupImportStatus.Imported, 1), (imported.Status, imported.MissingOriginals));
        Assert.False(b.Blobs.Exists(made.File));
        var attachment = (await b.Store.GetMessagesAsync(made.Root, Ct)).SelectMany(m => m.Blocks).OfType<AttachmentBlock>().Single();
        Assert.Equal("gauge-table.txt", attachment.Name);
        Assert.False(await b.ResourceService.HasOriginalAsync((await b.Resources.GetAsync(attachment.Resource, Ct))!.Resource));
    }

    /// <summary>Scenario 12 and §6: a damaged package, another kind of file and a newer format are refused, and nothing changes.</summary>
    [Fact]
    public async Task A_damaged_package_another_file_and_a_newer_format_are_refused()
    {
        await using var a = await ChatHarness.StartAsync(model: ToolModel);
        await MakeAsync(a);
        var package = await SaveAsync(a, "a");
        await using var b = await ChatHarness.StartAsync(model: ToolModel);
        var before = await StateAsync(b);

        var newer = Copy(package, "newer");
        Rewrite(newer, ManualBackupPackage.ManifestEntry, bytes => Edited(bytes, json => json["version"] = 2));
        var damaged = Copy(package, "damaged");
        Rewrite(damaged, ManualBackupPackage.DocumentEntry(a.Workspace), bytes => bytes[..(bytes.Length / 2)]);
        var planted = Copy(package, "planted");
        Rewrite(planted, ManualBackupPackage.DocumentEntry(a.Workspace), bytes => Edited(bytes, json => json["conversations"]![0]!["messages"]![0]!["id"] = Guid.Empty.ToString()));
        var truncated = Path.Combine(_dir, "truncated.mwbackup");
        await File.WriteAllBytesAsync(truncated, (await File.ReadAllBytesAsync(package, Ct))[..200], Ct);
        var page = Path.Combine(_dir, "page.mwbackup");
        await File.WriteAllTextAsync(page, "<!doctype html><title>Not a package</title>", Ct);
        var zip = Path.Combine(_dir, "other.mwbackup");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }

        Assert.Equal(ImportRefusal.NewerFormat, (await Importer(b).ReadAsync(newer, Ct)).Refusal);
        Assert.Equal(ImportRefusal.Damaged, (await Importer(b).ReadAsync(damaged, Ct)).Refusal);
        Assert.Equal(ImportRefusal.Damaged, (await Importer(b).ReadAsync(planted, Ct)).Refusal);
        Assert.Equal(ImportRefusal.NotAShare, (await Importer(b).ReadAsync(truncated, Ct)).Refusal);
        Assert.Equal(ImportRefusal.NotAShare, (await Importer(b).ReadAsync(page, Ct)).Refusal);
        Assert.Equal(ImportRefusal.NotAShare, (await Importer(b).ReadAsync(zip, Ct)).Refusal);
        Assert.Equal(before, await StateAsync(b));
    }

    /// <summary>§6: while an answer is being written in a conversation that would gain messages, the import waits; nothing is written.</summary>
    [Fact]
    public async Task An_answer_being_written_where_messages_would_arrive_holds_the_import()
    {
        await using var a = await ChatHarness.StartAsync(model: ToolModel);
        var made = await MakeAsync(a);
        await using var b = await ChatHarness.StartAsync(model: ToolModel);
        await Importer(b).ImportAsync(await ReadAsync(b, await SaveAsync(a, "a")), Ct);
        await AskAsync(a, made.Root, "A question there?", "An answer there.");
        var newer = await ReadAsync(b, await SaveAsync(a, "a2"));
        var held = b.Client.Hold("Being written");
        var asked = await b.Conversations.AskAsync(made.Root, "A question here?", Ct);
        await held.Started.Task.WaitAsync(Ct);
        var before = await ReadRowsAsync(b, "SELECT id, conversation_id, seq, supersedes_id FROM message ORDER BY 1;");

        Assert.True((await Importer(b).PlanAsync(newer, Ct)).IsBlocked);
        Assert.Equal(ManualBackupImportStatus.AnswerRunning, (await Importer(b).ImportAsync(newer, Ct)).Status);
        Assert.Equal(before, await ReadRowsAsync(b, "SELECT id, conversation_id, seq, supersedes_id FROM message ORDER BY 1;"));

        held.Release.SetResult();
        await b.WaitForAnswerAsync(asked.Answer!.Value);
        Assert.False((await Importer(b).PlanAsync(newer, Ct)).IsBlocked);
    }

    /// <summary>
    /// A summary being written in a conversation that would be joined was asked for up to a number the joining would move
    /// (review 2026-11-27): the import waits for it; nothing is written.
    /// </summary>
    [Fact]
    public async Task A_summary_being_written_where_the_line_would_be_joined_holds_the_import()
    {
        await using var a = await ChatHarness.StartAsync(model: ToolModel);
        var made = await MakeAsync(a);
        await using var b = await ChatHarness.StartAsync(model: ToolModel);
        await Importer(b).ImportAsync(await ReadAsync(b, await SaveAsync(a, "a")), Ct);
        await AskAsync(a, made.Root, "Asked there?", "Answered there.");
        b.Client.Reply("Answered here.");
        await b.WaitForAnswerAsync((await b.Conversations.AskAsync(made.Root, "Asked here?", Ct)).Answer!.Value);
        var newer = await ReadAsync(b, await SaveAsync(a, "a2"));
        var gate = b.Client.Knowledge.Hold();
        Assert.True((await b.SummaryService.SummarizeAsync(made.Root, Ct)).Accepted);
        var before = await ReadRowsAsync(b, "SELECT id, conversation_id, seq, supersedes_id FROM message ORDER BY 1;");

        Assert.True((await Importer(b).PlanAsync(newer, Ct)).IsBlocked);
        Assert.Equal(ManualBackupImportStatus.AnswerRunning, (await Importer(b).ImportAsync(newer, Ct)).Status);
        Assert.Equal(before, await ReadRowsAsync(b, "SELECT id, conversation_id, seq, supersedes_id FROM message ORDER BY 1;"));

        gate.TrySetResult();
        await ChatHarness.UntilAsync(async () => await b.SummaryService.LatestTaskAsync(made.Root, Ct) is { State: GenerationTaskState.Completed });
        Assert.Equal(ManualBackupImportStatus.Imported, (await Importer(b).ImportAsync(newer, Ct)).Status);
    }

    /// <summary>What <see cref="MakeAsync"/> made in a folder.</summary>
    internal sealed record Made(ConversationId Root, ConversationId Branch, ConversationId Received, ContentHash File);

    /// <summary>
    /// A root with an attached file, answered with a web search and a page read; an answer tried again; a branch from the
    /// first answer, archived; a summary with two saved versions, one read, and a summary of the branch linked to it; and a
    /// conversation received from another folder's share.
    /// </summary>
    internal async Task<Made> MakeAsync(ChatHarness h)
    {
        await h.WebSearch.SetKeyAsync("tvly-test-search-key-1234567890", Ct);
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

        var replaced = await AskAsync(h, root, "A second question?", "The first attempt, replaced");
        h.Client.Reply("The second attempt, which stands.");
        var retried = await h.Conversations.RetryAsync(root, replaced.Id, Ct);
        await h.WaitForAnswerAsync(retried.Answer!.Value);

        var branch = (await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(answer, "standing answer"), Ct)).Child;
        await AskAsync(h, branch, "The branch question?", "The branch answer.");
        await h.TitleEndedAsync(branch);

        await SummarizeAsync(h, root, "Title: The older title\nShort name: Older\n\nThe older body of the summary");
        var node = (await h.SummaryService.ForConversationAsync(root, Ct))!.Node;
        await SummarizeAsync(h, root, "Title: The current title\nShort name: Trains\n\nThe current body");
        await h.SummaryService.AdoptAsync(node.Id, (await h.SummaryService.ForConversationAsync(root, Ct))!.Node.Pending!.Value, Ct);
        await h.SummaryService.SetReadAsync(node.Id, (await h.SummaryNodes.GetAsync(node.Id, Ct))!.Saved[0].Id, true, Ct);
        await SummarizeAsync(h, branch, "Title: The branch summary\n\nOf the branch");
        await h.Links.CreateAsync(node.Id, (await h.SummaryService.ForConversationAsync(branch, Ct))!.Node.Id, "both", Ct);
        await h.Archive.ArchiveAsync(branch, DateTimeOffset.UtcNow, Ct);

        // A conversation received from someone else's share.
        await using var sharer = await ChatHarness.StartAsync();
        sharer.Client.Titles.FailNext(GenerationErrorKind.Unauthorized, 20);
        sharer.Client.Reply("Shared answer.");
        var shared = await sharer.Conversations.StartAsync(sharer.Workspace, "A shared question?", Ct);
        await sharer.WaitForAnswerAsync(shared.Answer!.Value);
        var source = await new ShareService(sharer.Store, sharer.SummaryNodes, new SqliteSummaryNetworkStore(sharer.Database), new SqliteSummaryLinkStore(sharer.Database), new SharePageWriter(new PlainMarkdown()), new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance), NullLogger<ShareService>.Instance)
            .ReadAsync(sharer.Workspace, Ct);
        var share = await ShareContentFlowTests.Contents(sharer).BuildAsync("Someone's share", sharer.Workspace, source, ShareSelection.Exploration(source.Parents, shared.Conversation), SharedParts.Day, "0.7.0", Ct);
        var received = await new ImportService(new SqliteImportStore(h.Database), h.Conversations, h.Reading, h.Health, new SystemClock(), NullLogger<ImportService>.Instance, h.SummaryService, h.Links)
            .ImportAsync(share, ImportTarget.Existing(h.Workspace), Ct);
        Assert.Equal(ImportStatus.Imported, received.Status);
        return new Made(root, branch, shared.Conversation, file.Resource.Hash);
    }

    internal async Task<string> SaveAsync(ChatHarness h, string name)
    {
        var path = Path.Combine(_dir, name + ".mwbackup");
        var all = (await h.Workspaces.ListAsync(Ct)).Select(w => w.Id).ToList();
        await ManualBackupFlowTests.Backups(h).SaveAsync(all, new ExportDestination("backup", path, null), null, Ct);
        return path;
    }

    internal static async Task<ManualBackupFile> ReadAsync(ChatHarness h, string path)
    {
        var read = await Importer(h).ReadAsync(path, Ct);
        Assert.Null(read.Refusal);
        return read.File!;
    }

    internal static ManualBackupImportService Importer(ChatHarness h) =>
        new(new ManualBackupReader(), new SqliteManualBackupImportStore(h.Database), h.Blobs, h.Conversations, h.Reading, h.Health, new SystemClock(),
            NullLogger<ManualBackupImportService>.Instance, h.SummaryService, h.Links);

    /// <summary>One workspace's document as the package holds it.</summary>
    internal static string Document(string package, WorkspaceId workspace) => Encoding.UTF8.GetString(ManualBackupFlowTests.Entries(package)[ManualBackupPackage.DocumentEntry(workspace)]);

    private static List<(string Id, string Name)> Workspaces(string package)
    {
        using var manifest = JsonDocument.Parse(ManualBackupFlowTests.Entries(package)[ManualBackupPackage.ManifestEntry]);
        return [.. manifest.RootElement.GetProperty("workspaces").EnumerateArray().Select(w => (w.GetProperty("id").GetString()!, w.GetProperty("name").GetString()!))];
    }

    internal static async Task<Message> AskAsync(ChatHarness h, ConversationId id, string question, string reply)
    {
        h.Client.Reply(reply);
        var sent = await h.Conversations.AskAsync(id, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.TitleEndedAsync(sent.Conversation);
        return answer;
    }

    internal static async Task SummarizeAsync(ChatHarness h, ConversationId id, string reply)
    {
        h.Client.Knowledge.Replies.Enqueue(reply);
        var before = await h.SummaryService.LatestTaskAsync(id, Ct);
        Assert.True((await h.SummaryService.SummarizeAsync(id, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(id, Ct) is { State: GenerationTaskState.Completed } done && done.Id != before?.Id);
    }

    internal static Task<IReadOnlyList<(ConversationId, int)>> RangesAsync(ChatHarness h, ConversationId id) => h.Database.ReadAsync<IReadOnlyList<(ConversationId, int)>>(
        connection => [.. SqliteBranchStore.ReadRanges(connection, null, id).Select(r => (r.Ancestor, r.MaxSeq))], Ct);

    private static Task<long> CountAsync(ChatHarness h, string sql) => h.Database.ReadAsync(connection => connection.Scalar<long>(null, sql), Ct);

    private static Task<string> ArchivedAsync(ChatHarness h) => ReadRowsAsync(h, "SELECT conversation_id FROM conversation_archive ORDER BY 1;");

    /// <summary>Every row an import could touch.</summary>
    internal static async Task<string> StateAsync(ChatHarness h, params string[] tables)
    {
        var all = tables.Length > 0 ? tables :
        [
            "workspace", "conversation", "branch_source", "branch_context_range", "import", "import_conversation", "conversation_archive", "message", "message_block",
            "summary_node", "summary_version", "summary_read_state", "summary_link", "resource", "resource_derivative", "resource_chunk", "generation_task", "run_step",
        ];
        var state = new List<string>();
        foreach (var table in all)
        {
            state.Add(table + "\n" + await ReadRowsAsync(h, $"SELECT * FROM {table} ORDER BY 1, 2;"));
        }

        return string.Join('\n', state);
    }

    private static Task<string> ReadRowsAsync(ChatHarness h, string sql) => h.Database.ReadAsync(
        connection =>
        {
            using var command = connection.Command(null, sql);
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString())));
            }

            return string.Join('\n', rows);
        },
        Ct);

    private string Copy(string package, string name)
    {
        var path = Path.Combine(_dir, name + ".mwbackup");
        File.Copy(package, path);
        return path;
    }

    private static void Rewrite(string package, string entry, Func<byte[], byte[]> change)
    {
        using var zip = ZipFile.Open(package, ZipArchiveMode.Update);
        var old = zip.GetEntry(entry)!;
        byte[] bytes;
        using (var stream = old.Open())
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        }

        old.Delete();
        using var target = zip.CreateEntry(entry).Open();
        target.Write(change(bytes));
    }

    private static byte[] Edited(byte[] json, Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(json)!;
        edit(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }
}
