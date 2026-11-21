using System.Text;
using Mindweave.Application.Languages;
using Mindweave.Application.Resources;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Resources;

/// <summary>
/// Resources through the real service, SQLite and blob folder (docs/design/resources.md): originals stored once by hash
/// and written atomically; messages that refer to them read name, type and text in the same snapshot; a resource belongs
/// to one workspace and conversation and goes with them; an original is removed only when neither the database nor a kept
/// backup refers to it, so a backup made before a delete restores the attachments too.
/// </summary>
public sealed class ResourceStoreTests
{
    private static readonly ModelSnapshot Model =
        new("m", "https://fake.example/v1", ChatProtocol.ChatCompletions, "fake-model", "ref", null, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions));

    /// <summary>A real 1×1 PNG: images are decoded to check them (v16).</summary>
    private static readonly byte[] PngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_paste_is_stored_as_an_original_named_by_its_hash_and_a_row_with_its_text()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);

        var created = await h.ResourceService.CreateFromPasteAsync(h.Workspace, null, "第一行\r\n第二行", Ct);

        Assert.True(created.IsCreated, created.Status.ToString());
        var resource = created.Resource!.Resource;
        var bytes = Encoding.UTF8.GetBytes("第一行\r\n第二行");
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), resource.Hash.Hex);
        Assert.Equal((MediaTypes.PlainText, (long)bytes.Length, ResourceOrigin.Paste, (string?)null, ResourceProvenance.User, (ConversationId?)null),
            (resource.MediaType, resource.Size, resource.Origin, resource.Name, resource.Provenance, resource.ConversationId));
        Assert.Equal("第一行\n第二行", created.Resource.Text);

        // The original keeps its bytes exactly, in the fan-out folder; nothing is left in the temporary folder.
        var file = Path.Combine(h.Paths.ResourceDirectory, resource.Hash.Hex[..2], resource.Hash.Hex);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Paths.ResourceDirectory, "tmp")));

        var read = await h.ResourceService.GetAsync(resource.Id, Ct);
        Assert.Equal(created.Resource, read);
        await using var original = await h.ResourceService.OpenOriginalAsync(resource.Id, Ct);
        using var copy = new MemoryStream();
        await original!.CopyToAsync(copy, Ct);
        Assert.Equal(bytes, copy.ToArray());

        Assert.Equal(ResourceCreateStatus.Empty, (await h.ResourceService.CreateFromPasteAsync(h.Workspace, null, " \n ", Ct)).Status);
    }

    [Fact]
    public async Task The_same_content_is_stored_once_whatever_its_name_or_origin()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var path = Path.Combine(h.DataDirectory, "笔记.md");
        await File.WriteAllTextAsync(path, "same", Ct);

        var pasted = await h.ResourceService.CreateFromPasteAsync(h.Workspace, null, "same", Ct);
        var filed = await h.ResourceService.CreateFromFileAsync(h.Workspace, null, path, Ct);

        Assert.NotEqual(pasted.Resource!.Resource.Id, filed.Resource!.Resource.Id);
        Assert.Equal(pasted.Resource.Resource.Hash, filed.Resource.Resource.Hash);
        Assert.Equal(("笔记.md", MediaTypes.Markdown), (filed.Resource.Resource.Name, filed.Resource.Resource.MediaType));
        Assert.Single(Directory.EnumerateFiles(h.Paths.ResourceDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Files_are_read_by_kind_text_in_its_encoding_images_by_their_bytes_and_the_rest_refused()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        async Task<ResourceCreateResult> FileAsync(string name, byte[] content)
        {
            var path = Path.Combine(h.DataDirectory, name);
            await File.WriteAllBytesAsync(path, content, Ct);
            return await h.ResourceService.CreateFromFileAsync(h.Workspace, null, path, Ct);
        }

        var bom = await FileAsync("a.md", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# 标题\r\n正文\r")]);
        Assert.Equal("# 标题\n正文\n", bom.Resource!.Text);

        var gbk = CodePagesEncodingProvider.Instance.GetEncoding(54936)!.GetBytes("旧的中文文本文件");
        Assert.Equal("旧的中文文本文件", (await FileAsync("old.txt", gbk)).Resource!.Text);

        var utf16 = await FileAsync("u.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("小端")]);
        Assert.Equal("小端", utf16.Resource!.Text);

        var code = await FileAsync("Program.cs", Encoding.UTF8.GetBytes("class A { }"));
        Assert.Equal(("text/x-csharp", "class A { }"), (code.Resource!.Resource.MediaType, code.Resource.Text));

        // Images: stored as originals with no text; the bytes decide the type.
        var png = await FileAsync("pic.png", PngBytes);
        Assert.Equal((MediaTypes.Png, (string?)null, true), (png.Resource!.Resource.MediaType, png.Resource.Text, png.Resource.Resource.IsImage));
        Assert.Equal(MediaTypes.Png, (await FileAsync("misnamed.jpg", [.. PngBytes, 9])).Resource!.Resource.MediaType);
        Assert.Equal(ResourceCreateStatus.NotReadable, (await FileAsync("fake.png", Encoding.UTF8.GetBytes("not an image"))).Status);

        Assert.Equal(ResourceCreateStatus.NotReadable, (await FileAsync("paper.pdf", Encoding.ASCII.GetBytes("%PDF-1.7"))).Status);
        Assert.Equal(ResourceCreateStatus.NotReadable, (await FileAsync("notes.docx", [0x50, 0x4B, 3, 4])).Status);
        Assert.Equal(ResourceCreateStatus.Unsupported, (await FileAsync("slides.pptx", [0x50, 0x4B, 3, 4])).Status);
        Assert.Equal(ResourceCreateStatus.NotReadable, (await FileAsync("binary.txt", [0x41, 0x00, 0x42])).Status);
        Assert.Equal(ResourceCreateStatus.Empty, (await FileAsync("empty.txt", [])).Status);
        Assert.Equal(ResourceCreateStatus.FileUnavailable, (await h.ResourceService.CreateFromFileAsync(h.Workspace, null, Path.Combine(h.DataDirectory, "gone.txt"), Ct)).Status);

        var large = Path.Combine(h.DataDirectory, "large.txt");
        await using (var stream = File.Create(large))
        {
            stream.SetLength(ResourceLimits.MaxOriginalBytes + 1);
        }

        Assert.Equal(ResourceCreateStatus.TooLarge, (await h.ResourceService.CreateFromFileAsync(h.Workspace, null, large, Ct)).Status);
        Assert.Equal(6, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM resource;"), Ct));
    }

    [Fact]
    public async Task A_message_refers_to_resources_and_reads_their_name_type_and_text_back()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var doc = await PasteAsync(h, h.Workspace, "文档内容");
        var image = await FileAsync(h, "图.png", PngBytes);

        // Only the reference counts when writing; the descriptive fields are the resource's own on reading.
        var (conversation, question) = await AskAsync(h, h.Workspace, new TextBlock("看看这些"), new AttachmentBlock(doc, "ignored", "x/y", 0, null), new AttachmentBlock(image, null, "x/y", 0, null));

        var message = (await h.Store.GetMessagesAsync(conversation, Ct)).Single(m => m.Id == question);
        Assert.Equal(3, message.Blocks.Count);
        var a = Assert.IsType<AttachmentBlock>(message.Blocks[1]);
        Assert.Equal((doc, (string?)null, MediaTypes.PlainText, 12L, "文档内容"), (a.Resource, a.Name, a.MediaType, a.Size, a.Text));
        var i = Assert.IsType<AttachmentBlock>(message.Blocks[2]);
        Assert.Equal(("图.png", MediaTypes.Png, (string?)null), (i.Name, i.MediaType, i.Text));
        Assert.Equal("看看这些\n\n[附件：粘贴的文字]\n\n[附件：图.png]", ContentBlocks.TextOf(message.Blocks, ModelPhrases.Current)); // architecture §15.3: content goes in the resource layer
        Assert.Equal("看看这些", message.Text);
        Assert.Equal("看看这些\n\n[附件：粘贴的文字]\n\n[附件：图.png]", ContentBlocks.BriefOf(message.Blocks, ModelPhrases.Current));

        // The block row's text is the display name, so the sidebar excerpt of a question that starts with one reads as a name.
        Assert.Equal("粘贴的文字,图.png", await h.Database.ReadAsync(c => c.Scalar<string>(null, "SELECT group_concat(text) FROM (SELECT text FROM message_block WHERE kind = 'attachment' ORDER BY ordinal);"), Ct));

        // Both resources now belong to the conversation.
        Assert.Equal(conversation, (await h.ResourceService.GetAsync(doc, Ct))!.Resource.ConversationId);
        Assert.Equal(conversation, (await h.ResourceService.GetAsync(image, Ct))!.Resource.ConversationId);
    }

    [Fact]
    public async Task A_resource_is_attached_only_within_its_workspace_and_to_one_conversation()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var other = (await h.WorkspaceService.CreateAsync(WorkspaceName.FromStored("另一个"), Ct)).Id;
        var foreign = await PasteAsync(h, other, "别处的");
        var mine = await PasteAsync(h, h.Workspace, "这里的");
        var before = await CountAsync(h, "conversation");

        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(h, h.Workspace, new AttachmentBlock(foreign, null, "", 0, null)));
        await AskAsync(h, h.Workspace, new AttachmentBlock(mine, null, "", 0, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(h, h.Workspace, new AttachmentBlock(mine, null, "", 0, null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(h, h.Workspace, new AttachmentBlock(ResourceId.New(), null, "", 0, null)));

        // Every refused write was rolled back whole: one conversation more, the foreign resource still unattached.
        Assert.Equal(before + 1, await CountAsync(h, "conversation"));
        Assert.Null((await h.ResourceService.GetAsync(foreign, Ct))!.Resource.ConversationId);
    }

    [Fact]
    public async Task A_message_and_its_attachments_are_read_in_one_snapshot()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var doc = await PasteAsync(h, h.Workspace, "旧文字");
        var (conversation, _) = await AskAsync(h, h.Workspace, new AttachmentBlock(doc, null, "", 0, null));

        // A write that changes the resource commits between two reads of one ReadAsync: both see the earlier state.
        var (first, second) = await h.Database.ReadAsync(
            c =>
            {
                var first = Describe(SqliteConversationStore.GetMessages(c, null, conversation)[0]);
                h.Database.WriteAsync((w, t) => w.Execute(t, "UPDATE resource_derivative SET text = '新文字'; UPDATE resource SET name = 'x.txt';"), Ct).GetAwaiter().GetResult();
                return (first, Describe(SqliteConversationStore.GetMessages(c, null, conversation)[0]));
            },
            Ct);

        Assert.Equal(first, second);
        Assert.Contains("旧文字", first, StringComparison.Ordinal);
        var after = Describe((await h.Store.GetMessagesAsync(conversation, Ct))[0]);
        Assert.Contains("x.txt", after, StringComparison.Ordinal);
        Assert.Contains("新文字", after, StringComparison.Ordinal);
    }

    private static string Describe(Message message) => string.Join('|', message.Blocks.OfType<AttachmentBlock>().Select(a => a.NameIn(ModelPhrases.Current) + ":" + a.Text));

    [Fact]
    public async Task Deleting_a_conversation_takes_its_resources_and_removes_originals_nothing_else_refers_to()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var sharedA = await PasteAsync(h, h.Workspace, "两处都有");
        var image = await FileAsync(h, "a.png", PngBytes);
        var sharedB = await PasteAsync(h, h.Workspace, "两处都有");
        var (a, _) = await AskAsync(h, h.Workspace, new AttachmentBlock(sharedA, null, "", 0, null), new AttachmentBlock(image, null, "", 0, null));
        var (b, _) = await AskAsync(h, h.Workspace, new AttachmentBlock(sharedB, null, "", 0, null));
        var sharedHash = (await h.ResourceService.GetAsync(sharedA, Ct))!.Resource.Hash;
        var imageHash = (await h.ResourceService.GetAsync(image, Ct))!.Resource.Hash;

        var outcome = await h.DeleteAsync(a);

        Assert.True(outcome.IsDeleted);

        // Attachments make it a delete that is backed up first; the backup keeps copies of the originals it removes (Q4).
        Assert.Equal(2, outcome.Scope.Attachments);
        Assert.NotNull(outcome.BackupPath);
        Assert.True(File.Exists(Path.Combine(outcome.BackupPath + ".resources", imageHash.Hex)));
        Assert.Null(await h.ResourceService.GetAsync(sharedA, Ct));
        Assert.Null(await h.ResourceService.GetAsync(image, Ct));
        Assert.Equal(1, await CountAsync(h, "resource_derivative")); // B's
        Assert.False(h.Blobs.Exists(imageHash));
        Assert.True(h.Blobs.Exists(sharedHash));

        Assert.True((await h.DeleteAsync(b)).IsDeleted);
        Assert.False(h.Blobs.Exists(sharedHash));
        Assert.Equal(0, await CountAsync(h, "resource"));
    }

    [Fact]
    public async Task A_backup_made_before_deleting_a_workspace_restores_its_attachments_with_their_originals()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var doc = await FileAsync(h, "a.png", PngBytes);
        var unsent = await PasteAsync(h, h.Workspace, "还没发送");
        var (conversation, question) = await AskAsync(h, h.Workspace, new AttachmentBlock(doc, null, "", 0, null));
        var hashes = new[] { (await h.ResourceService.GetAsync(doc, Ct))!.Resource.Hash, (await h.ResourceService.GetAsync(unsent, Ct))!.Resource.Hash };

        var outcome = await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct);

        Assert.Equal(Application.Workspaces.WorkspaceDeleteStatus.Deleted, outcome.Status);
        Assert.Equal(0, await CountAsync(h, "resource"));
        Assert.All(hashes, hash => Assert.False(h.Blobs.Exists(hash))); // gone from the data's originals…
        Assert.All(hashes, hash => Assert.True(File.Exists(Path.Combine(outcome.BackupPath + ".resources", hash.Hex)))); // …kept with the backup

        var restored = await h.Database.RestoreAsync(h.Database.ListBackups().Single(b => b.Path == outcome.BackupPath), Ct);
        Assert.True(restored.IsReady, restored.Status.ToString());
        var message = (await h.Store.GetMessagesAsync(conversation, Ct)).Single(m => m.Id == question);
        Assert.Equal(doc, Assert.IsType<AttachmentBlock>(message.Blocks[0]).Resource);
        await using var original = await h.ResourceService.OpenOriginalAsync(doc, Ct);
        using var copy = new MemoryStream();
        await original!.CopyToAsync(copy, Ct);
        Assert.Equal(PngBytes, copy.ToArray());
        Assert.Equal("还没发送", (await h.ResourceService.GetAsync(unsent, Ct))!.Text);
    }

    [Fact]
    public async Task Deleting_a_backup_removes_the_originals_it_kept()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var doc = await PasteAsync(h, h.Workspace, "只在备份里");
        await AskAsync(h, h.Workspace, new AttachmentBlock(doc, null, "", 0, null));
        var hash = (await h.ResourceService.GetAsync(doc, Ct))!.Resource.Hash;
        var outcome = await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct);
        Assert.False(h.Blobs.Exists(hash));
        var kept = Path.Combine(outcome.BackupPath! + ".resources", hash.Hex);
        Assert.True(File.Exists(kept));
        Assert.True(h.Database.ListBackups().Single(b => b.Path == outcome.BackupPath).SizeBytes > new FileInfo(outcome.BackupPath!).Length);

        h.Database.Delete(h.Database.ListBackups().Single(b => b.Path == outcome.BackupPath));
        Assert.False(Directory.Exists(outcome.BackupPath + ".resources"));
    }

    [Fact]
    public async Task A_sweep_never_removes_an_original_being_written_or_just_committed()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);

        // Written, row not yet stored: pinned.
        var lease = await h.Blobs.WriteAsync(Encoding.UTF8.GetBytes("in flight"), Ct);
        Assert.Equal(new BlobSweepResult(0, 1), await h.Blobs.SweepAsync(Ct));
        lease.Dispose();
        lease.Dispose(); // once only
        Assert.Equal(new BlobSweepResult(1, 0), await h.Blobs.SweepAsync(Ct));

        // A resource committed after the sweep read its references, before it looked at the files: kept.
        ResourceId? late = null;
        h.Blobs.AfterReferencesReadForTests = () => late = h.ResourceService.CreateFromPasteAsync(h.Workspace, null, "迟到的", Ct).GetAwaiter().GetResult().Resource!.Resource.Id;
        Assert.Equal(new BlobSweepResult(0, 1), await h.Blobs.SweepAsync(Ct));
        h.Blobs.AfterReferencesReadForTests = null;
        Assert.True(h.Blobs.Exists((await h.ResourceService.GetAsync(late!.Value, Ct))!.Resource.Hash));
        Assert.Equal(new BlobSweepResult(0, 1), await h.Blobs.SweepAsync(Ct));

        // Unattached and deleted: the original goes at once.
        Assert.Equal(ResourceDeleteStatus.Deleted, await h.ResourceService.DeleteUnattachedAsync(late.Value, Ct));
        Assert.Equal(ResourceDeleteStatus.Missing, await h.ResourceService.DeleteUnattachedAsync(late.Value, Ct));
        Assert.Empty(Directory.EnumerateFiles(h.Paths.ResourceDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_attached_resource_is_not_deleted_on_its_own()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var doc = await PasteAsync(h, h.Workspace, "已发送");
        await AskAsync(h, h.Workspace, new AttachmentBlock(doc, null, "", 0, null));

        Assert.Equal(ResourceDeleteStatus.Attached, await h.ResourceService.DeleteUnattachedAsync(doc, Ct));
        Assert.NotNull(await h.ResourceService.GetAsync(doc, Ct));
    }

    [Fact]
    public async Task A_sweep_removes_stale_temporary_files_and_deletes_nothing_while_a_kept_database_cannot_be_read()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var lease = await h.Blobs.WriteAsync(Encoding.UTF8.GetBytes("orphan"), Ct);
        lease.Dispose();
        var temporary = Path.Combine(h.Paths.ResourceDirectory, "tmp");
        var stale = Path.Combine(temporary, "stale");
        var fresh = Path.Combine(temporary, "fresh");
        await File.WriteAllTextAsync(stale, "x", Ct);
        await File.WriteAllTextAsync(fresh, "x", Ct);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        var stranger = Path.Combine(h.Paths.ResourceDirectory, "README");
        await File.WriteAllTextAsync(stranger, "not ours", Ct);

        // A damaged database a restore kept aside: unsure what it refers to, so nothing at all is removed.
        var damaged = h.Paths.DatabasePath + ".replaced-20261117T000000000Z";
        await File.WriteAllTextAsync(damaged, "not a database", Ct);
        Assert.True((await h.Blobs.SweepAsync(Ct)).Skipped);
        Assert.True(h.Blobs.Exists(lease.Hash));
        Assert.True(File.Exists(stale));

        File.Delete(damaged);
        Assert.Equal(new BlobSweepResult(1, 0), await h.Blobs.SweepAsync(Ct));
        Assert.False(h.Blobs.Exists(lease.Hash));
        Assert.Equal((false, true, true), (File.Exists(stale), File.Exists(fresh), File.Exists(stranger)));
    }

    [Fact]
    public async Task A_sweep_on_a_closed_database_deletes_nothing()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var lease = await h.Blobs.WriteAsync(Encoding.UTF8.GetBytes("orphan"), Ct);
        lease.Dispose();
        await h.Database.CloseAsync();

        Assert.True((await h.Blobs.SweepAsync(Ct)).Skipped);
        Assert.True(h.Blobs.Exists(lease.Hash));
    }

    private static async Task<ResourceId> PasteAsync(ChatHarness h, WorkspaceId workspace, string text)
    {
        var created = await h.ResourceService.CreateFromPasteAsync(workspace, null, text, Ct);
        Assert.True(created.IsCreated, created.Status.ToString());
        return created.Resource!.Resource.Id;
    }

    private static async Task<ResourceId> FileAsync(ChatHarness h, string name, byte[] content)
    {
        var path = Path.Combine(h.DataDirectory, name);
        await File.WriteAllBytesAsync(path, content, Ct);
        var created = await h.ResourceService.CreateFromFileAsync(h.Workspace, null, path, Ct);
        Assert.True(created.IsCreated, created.Status.ToString());
        return created.Resource!.Resource.Id;
    }

    /// <summary>A new conversation whose question is <paramref name="blocks"/>, answered, so nothing in it still runs.</summary>
    private static async Task<(ConversationId Conversation, MessageId Question)> AskAsync(ChatHarness h, WorkspaceId workspace, params ContentBlock[] blocks)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation(ConversationId.New(), workspace, null, TitleSource.None, now, now);
        var question = new Message(MessageId.New(), conversation.Id, 0, MessageRole.User, blocks, MessageState.Complete, 0, null, null, null, now, now);
        var answer = new Message(MessageId.New(), conversation.Id, 1, MessageRole.Assistant, ContentBlocks.FromText(string.Empty), MessageState.Pending, 0, null, null, null, now, null);
        var task = new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Answer, conversation.Id, answer.Id, Model, [question.Id], now);
        await h.Store.CreateAsync(conversation, question, answer, task, null, Ct);
        Assert.True(await h.Store.FinishAnswerAsync(task.Id, answer.Id, ContentBlocks.FromText("答"), 1, MessageState.Complete, null, null, now, Ct));
        return (conversation.Id, question.Id);
    }

    private static Task<long> CountAsync(ChatHarness h, string table) =>
        h.Database.ReadAsync(c => c.Scalar<long>(null, $"SELECT count(*) FROM {table};"), Ct);
}
