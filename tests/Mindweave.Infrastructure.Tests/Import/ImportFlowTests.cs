using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Export;
using Mindweave.Application.Import;
using Mindweave.Application.Resources;
using Mindweave.Application.Share;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Export;
using Mindweave.Infrastructure.Import;
using Mindweave.Infrastructure.Share;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Import;

/// <summary>
/// Importing a structure-only share through the real stores (docs/design/sharing-import.md §2, §9 scenarios 1 to 4): one data
/// folder saves the share, another brings it in.
/// </summary>
public sealed class ImportFlowTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 18, 11, 42, 30, 120, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-import-flow", Guid.NewGuid().ToString("N"));

    public ImportFlowTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Acceptance 1, the round trip: titles, parents, start times and order; no message anywhere.</summary>
    [Fact]
    public async Task A_share_saved_in_one_data_folder_arrives_in_another_with_its_titles_lines_and_order()
    {
        var path = Path.Combine(_dir, "share.html");
        ShareSource sent;
        await using (var sharer = await ChatHarness.StartAsync())
        {
            sharer.Client.Titles.FailNext(GenerationErrorKind.Unauthorized, 10);
            var root = await StartAsync(sharer, "How did trains begin?");
            await sharer.Conversations.RenameAsync(root.Conversation, "History of trains 火车", Ct);
            var branch = await BranchAsync(sharer, root.Conversation, root.Answer, "Standard gauge");
            await BranchAsync(sharer, branch.Conversation, branch.Answer, null);
            await BranchAsync(sharer, root.Conversation, root.Answer, "Steam");
            sent = await Shares(sharer).ReadAsync(sharer.Workspace, Ct);
            await Shares(sharer).SaveAsync(
                ExchangeDocument.Structure("How trains came to be", sent, ShareSelection.Exploration(sent.Parents, root.Conversation), Day, "0.7.0"),
                new ExportDestination("share.html", path, null), Ct);
        }

        await using var h = await ChatHarness.StartAsync();
        var read = await new SharedFileReader().ReadAsync(path, Ct);
        Assert.Null(read.Refusal);
        var result = await Imports(h).ImportAsync(read.Document!, ImportTarget.Existing(h.Workspace), Ct);

        Assert.Equal(ImportStatus.Imported, result.Status);
        var rows = await h.Conversations.ListAsync(h.Workspace, Ct);
        Assert.Equal(
            sent.Conversations.OrderBy(c => c.StartedAt).Select(c => (c.Id, c.Parent, c.Name, c.StartedAt)),
            rows.OrderBy(r => r.Conversation.CreatedAt).ThenBy(r => r.Conversation.Id.Value).Select(r => (r.Conversation.Id, r.Parent, r.Conversation.Title, r.Conversation.CreatedAt)));
        Assert.All(rows, r => Assert.True(r.IsUnexplored && r.Conversation.IsImported && !r.IsHidden && r.Quote is null));
        Assert.All(rows, r => Assert.Equal(r.Conversation.Title is null ? TitleSource.None : TitleSource.Auto, r.Conversation.TitleSource));
        Assert.Contains(rows, r => r.Conversation.Title is null);
        Assert.Equal(0, await h.CountAsync("message"));
        Assert.Equal(sent.Conversations.Single(c => c.Parent is null).Id, result.Opens);
    }

    [Fact]
    public async Task Every_ancestor_has_a_context_row_that_says_nothing_so_branches_count_as_branches()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, twig) = await ImportPathAsync(h);

        var chains = await new SqliteSummaryNetworkStore(h.Database).AncestorChainsAsync(h.Workspace, Ct);
        Assert.Equal([root], chains[branch]);
        Assert.Equal([root, branch], chains[twig]);
        Assert.Empty(await h.Branches.GetAncestorContextAsync(twig, Ct));
        Assert.Equal([root, branch, twig], (await h.Conversations.GetLineageAsync(twig, Ct)).Select(e => e.Conversation.Id));
        var source = await h.Conversations.GetSourceAsync(twig, Ct);
        Assert.Equal(BranchSource.WithoutMessage(twig, branch, source!.CreatedAt), source);
        Assert.Equal([branch], (await h.Conversations.ListBranchesAsync(root, Ct)).Select(b => b.Child));
    }

    /// <summary>Acceptance 3.</summary>
    [Fact]
    public async Task The_same_file_again_changes_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, _) = await ImportPathAsync(h);
        await h.Conversations.RenameAsync(branch, "My own name", Ct);
        await h.Conversations.ArchiveAsync(root, Ct);
        var before = await StateAsync(h);

        var again = await Imports(h).ImportAsync(TheShare(root, branch), ImportTarget.Existing(h.Workspace), Ct);
        var elsewhere = await Imports(h).ImportAsync(TheShare(root, branch), ImportTarget.New(NewWorkspace()), Ct);

        Assert.Equal((ImportStatus.NothingNew, ImportStatus.NothingNew), (again.Status, elsewhere.Status));
        Assert.Equal(before, await StateAsync(h));
        Assert.Single(await h.Workspaces.ListAsync(Ct));
    }

    [Fact]
    public async Task A_newer_share_adds_only_what_is_new_under_what_is_here()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, _) = await ImportPathAsync(h);
        await h.Conversations.RenameAsync(root, "Renamed", Ct);
        var added = Conversation(branch, "Added later", 50);

        var changed = new List<ConversationId>();
        h.Conversations.ConversationChanged += (_, id) => changed.Add(id);
        var result = await Imports(h).ImportAsync(Document([.. TheShare(root, branch).Conversations, added]), ImportTarget.Existing(h.Workspace), Ct);

        Assert.Equal([added.Id], result.Created);
        Assert.Equal([branch], result.Parents);
        Assert.Equal(added.Id, result.Opens);
        Assert.Equal("Renamed", (await h.Store.GetAsync(root, Ct))!.Title);
        Assert.Contains(added.Id, changed);
        Assert.Contains(branch, changed);
    }

    [Fact]
    public async Task The_users_own_share_imported_where_it_was_made_is_already_here_and_stays_the_users_own()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await StartAsync(h, "How did trains begin?");
        var source = await Shares(h).ReadAsync(h.Workspace, Ct);

        var result = await Imports(h).ImportAsync(ExchangeDocument.Structure("Mine", source, ShareSelection.Exploration(source.Parents, root.Conversation), Day, "0.7.0"), ImportTarget.Existing(h.Workspace), Ct);

        Assert.Equal(ImportStatus.NothingNew, result.Status);
        Assert.False((await h.Store.GetAsync(root.Conversation, Ct))!.IsImported);
        Assert.Equal(0, await h.CountAsync("import"));
    }

    [Fact]
    public async Task A_new_branch_of_the_users_own_conversation_hangs_off_it_and_uses_nothing_of_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await StartAsync(h, "How did trains begin?");
        var arrived = Conversation(root.Conversation, "From a friend", 5);

        var result = await Imports(h).ImportAsync(Document([arrived]), ImportTarget.Existing(h.Workspace), Ct);

        Assert.Equal([arrived.Id], result.Created);
        Assert.Empty(await h.Branches.GetAncestorContextAsync(arrived.Id, Ct));
        Assert.Equal([root.Conversation], (await new SqliteSummaryNetworkStore(h.Database).AncestorChainsAsync(h.Workspace, Ct))[arrived.Id]);
    }

    [Fact]
    public async Task What_lives_in_another_workspace_is_skipped_with_its_branches()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, twig) = await ImportPathAsync(h);
        var other = NewWorkspace();
        await h.Workspaces.AddAsync(other, Ct);
        var document = Document([.. TheShare(root, branch).Conversations, Conversation(twig, "Under it", 60), Conversation(null, "Another root", 70)]);

        var result = await Imports(h).ImportAsync(document, ImportTarget.Existing(other.Id), Ct);

        Assert.Equal("Another root", Assert.Single(await h.Conversations.ListAsync(other.Id, Ct)).Conversation.Title);
        Assert.Single(result.Created);
        Assert.Equal(3, (await h.Conversations.ListAsync(h.Workspace, Ct)).Count);
    }

    [Fact]
    public async Task A_new_workspace_is_made_with_the_import_and_not_without_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var workspace = NewWorkspace();
        var document = Document([Conversation(null, "Root", 0)]);

        await using (var failing = await ChatHarness.StartAsync())
        {
            failing.Database.BeforeCommitForTests = (_, _) => throw new InvalidOperationException("simulated failure");
            await Assert.ThrowsAnyAsync<Exception>(() => Imports(failing).ImportAsync(document, ImportTarget.New(workspace), Ct));
            failing.Database.BeforeCommitForTests = null;
            Assert.Single(await failing.Workspaces.ListAsync(Ct));
            Assert.Equal(0, await failing.CountAsync("conversation") + await failing.CountAsync("import"));
        }

        await Imports(h).ImportAsync(document, ImportTarget.New(workspace), Ct);
        Assert.Contains(workspace, await h.Workspaces.ListAsync(Ct));
        Assert.Single(await h.Conversations.ListAsync(workspace.Id, Ct));
    }

    [Fact]
    public async Task A_workspace_or_a_parent_deleted_meanwhile_refuses_the_whole_import()
    {
        await using var h = await ChatHarness.StartAsync();

        var gone = await Imports(h).ImportAsync(Document([Conversation(null, "Root", 0)]), ImportTarget.Existing(WorkspaceId.New()), Ct);
        var loose = await Imports(h).ImportAsync(Document([Conversation(null, "Root", 0), Conversation(ConversationId.New(), "Branch", 1)]), ImportTarget.Existing(h.Workspace), Ct);

        Assert.Equal((ImportStatus.WorkspaceMissing, ImportStatus.DoesNotFit), (gone.Status, loose.Status));
        Assert.Equal(0, await h.CountAsync("conversation") + await h.CountAsync("import"));
    }

    [Fact]
    public async Task A_name_arrives_on_one_line_and_an_empty_one_is_none()
    {
        await using var h = await ChatHarness.StartAsync();
        await Imports(h).ImportAsync(Document([Conversation(null, " Two\r\nlines ", 0), Conversation(null, " \r\n ", 1)]), ImportTarget.Existing(h.Workspace), Ct);

        Assert.Equal(new HashSet<string?> { "Two lines", null }, (await h.Conversations.ListAsync(h.Workspace, Ct)).Select(r => r.Conversation.Title).ToHashSet());
    }

    [Fact]
    public async Task A_received_conversation_names_the_share_and_when_it_came()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _, _) = await ImportPathAsync(h);
        var own = await StartAsync(h, "Mine?");

        var marks = await new SqliteImportStore(h.Database).MarksAsync(h.Workspace, Ct);
        Assert.Equal("How trains came to be", marks[root].Title);
        Assert.InRange(marks[root].At, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.DoesNotContain(own.Conversation, marks.Keys);
        Assert.Equal(1, await h.CountAsync("import"));
    }

    [Fact]
    public async Task The_network_shows_every_dot_with_its_lines_unexplored_and_where_it_came_from()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, twig) = await ImportPathAsync(h);
        var sent = await h.Conversations.AskAsync(branch, "How wide?", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        var network = await h.SummaryNetwork.ProjectAsync(h.Workspace, Ct);
        var nodes = network.Nodes.ToDictionary(n => n.Conversation);
        Assert.Equal((true, true, true, false), (nodes[root].IsUnexplored, nodes[branch].IsUnexplored, nodes[twig].IsUnexplored, nodes[sent.Conversation].IsUnexplored));
        Assert.Equal(("How trains came to be", (string?)null), (nodes[twig].Import!.Title, nodes[sent.Conversation].Import?.Title));
        Assert.Equal((root, "Trains"), (nodes[sent.Conversation].Root, nodes[root].DisplayName));
        Assert.Equal(
            new HashSet<(SummaryNodeId, SummaryNodeId)> { (nodes[root].Id, nodes[branch].Id), (nodes[branch].Id, nodes[twig].Id), (nodes[branch].Id, nodes[sent.Conversation].Id) },
            network.Edges.Select(e => (e.From, e.To)).ToHashSet());
    }

    /// <summary>Acceptance 2 (decision I4): asking starts the user's own branch, with no context; the received one stays empty.</summary>
    [Fact]
    public async Task Asking_in_an_empty_received_conversation_starts_the_users_own_branch_of_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, _) = await ImportPathAsync(h);
        h.Client.Titles.Replies.Enqueue("My title");
        h.Client.Reply("An ordinary answer.");

        var sent = await h.Conversations.AskAsync(branch, "How wide is standard gauge?", Ct);

        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        Assert.NotEqual(branch, sent.Conversation);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal("An ordinary answer.", answer.Text);
        await h.SettleAsync(sent.Conversation);

        var rows = (await h.Conversations.ListAsync(h.Workspace, Ct)).ToDictionary(r => r.Conversation.Id);
        var own = rows[sent.Conversation];
        Assert.Equal((branch, (string?)null, "My title", false, false), (own.Parent!.Value, own.Quote, own.Conversation.Title!, own.Conversation.IsImported, own.IsUnexplored));
        Assert.True(rows[branch].IsUnexplored);
        Assert.Empty(await h.Store.GetMessagesAsync(branch, Ct));
        Assert.Equal([root, branch], (await new SqliteSummaryNetworkStore(h.Database).AncestorChainsAsync(h.Workspace, Ct))[sent.Conversation]);
        Assert.Equal(["How wide is standard gauge?"], (await h.Store.GetMessagesAsync(sent.Conversation, Ct)).Where(m => m.Role == MessageRole.User).Select(m => m.Text));

        // Its own next question and a branch from its answer work as anywhere, with that context only.
        h.Client.Reply("A second answer.");
        var next = await h.Conversations.AskAsync(sent.Conversation, "And then?", Ct);
        Assert.Equal(sent.Conversation, next.Conversation);
        await h.WaitForAnswerAsync(next.Answer!.Value);
        var twig = (await h.Conversations.CreateBranchAsync(sent.Conversation, BranchFlowTests.Selection(answer, "ordinary"), Ct)).Child;
        Assert.Equal([sent.Conversation], (await h.Branches.GetAncestorContextAsync(twig, Ct)).Select(s => s.Conversation));
    }

    [Fact]
    public async Task An_attachment_on_the_received_conversations_draft_goes_with_the_question()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _, _) = await ImportPathAsync(h);
        var path = Path.Combine(_dir, "gauge.txt");
        await File.WriteAllTextAsync(path, "1435 mm", Ct);
        var file = (await h.ResourceService.AttachFileAsync(new DraftSlot(h.Workspace, root, "main"), path, Ct)).Resource!;
        h.Client.Reply("Seen.");

        var sent = await h.Conversations.AskAsync(root, "What is in it?", [file.Resource.Id], null, Ct);

        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var question = (await h.Store.GetMessagesAsync(sent.Conversation, Ct))[0];
        Assert.Equal(file.Resource.Id, Assert.Single(ContentBlocks.AttachmentsOf(question.Blocks)).Resource);
        Assert.Equal(0, await h.CountAsync("draft_attachment"));
    }

    [Fact]
    public async Task Asking_under_an_archived_path_is_refused_and_starts_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, _) = await ImportPathAsync(h);
        Assert.True((await h.Conversations.ArchiveAsync(root, Ct)).Archived);

        var sent = await h.Conversations.AskAsync(branch, "How wide?", Ct);

        Assert.Equal(SendRejection.ConversationArchived, sent.Rejection);
        Assert.Equal(3, await h.CountAsync("conversation"));
        Assert.Equal(0, await h.CountAsync("message"));
    }

    [Fact]
    public async Task No_message_can_be_stored_in_a_received_conversation()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _, _) = await ImportPathAsync(h);
        var own = await StartAsync(h, "Mine?");
        var question = new Message(MessageId.New(), root, 0, MessageRole.User, [new TextBlock("?")], MessageState.Complete, 0, null, null, null, Day, Day);
        var answer = new Message(MessageId.New(), root, 1, MessageRole.Assistant, [new TextBlock("")], MessageState.Pending, 0, null, null, null, Day, null);
        var model = (await h.Models.GetActiveSnapshotAsync(Ct))!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.AppendQuestionAsync(question, answer, new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Answer, root, answer.Id, model, [question.Id], Day), Ct));
        Assert.Empty(await h.Store.GetMessagesAsync(root, Ct));
        Assert.NotEmpty(await h.Store.GetMessagesAsync(own.Conversation, Ct));
    }

    [Fact]
    public async Task Archiving_restoring_and_deleting_work_on_received_conversations_and_their_parents()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, twig) = await ImportPathAsync(h);
        var sent = await h.Conversations.AskAsync(branch, "How wide?", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.SettleAsync(sent.Conversation);

        Assert.True((await h.Conversations.ArchiveAsync(root, Ct)).Archived);
        Assert.All(await h.Conversations.ListAsync(h.Workspace, Ct), r => Assert.True(r.IsHidden));
        Assert.Equal(4, (await h.Conversations.RestoreAsync(twig, Ct)).Count);
        Assert.All(await h.Conversations.ListAsync(h.Workspace, Ct), r => Assert.False(r.IsHidden));

        var scope = await h.ConversationDeletion.InspectAsync([branch], Ct);
        Assert.Equal(new HashSet<ConversationId> { branch, twig, sent.Conversation }, scope.Conversations.ToHashSet());
        Assert.Equal(root, scope.ParentOf[branch]);
        Assert.True((await h.ConversationDeletion.DeleteAsync(scope, Ct)).IsDeleted);
        Assert.Equal(1, await h.CountAsync("import"));
        Assert.True((await h.ConversationDeletion.DeleteAsync(await h.ConversationDeletion.InspectAsync([root], Ct), Ct)).IsDeleted);
        Assert.Equal(0, await h.CountAsync("conversation") + await h.CountAsync("import") + await h.CountAsync("import_conversation") + await h.CountAsync("branch_source") + await h.CountAsync("branch_context_range"));

        // What was deleted can be brought in again: a file never knows.
        Assert.Equal(ImportStatus.Imported, (await Imports(h).ImportAsync(TheShare(root, branch), ImportTarget.Existing(h.Workspace), Ct)).Status);
    }

    [Fact]
    public async Task A_workspace_with_received_conversations_can_be_deleted()
    {
        await using var h = await ChatHarness.StartAsync();
        var workspace = NewWorkspace();
        await Imports(h).ImportAsync(TheShare(ConversationId.New(), ConversationId.New()), ImportTarget.New(workspace), Ct);

        Assert.NotNull((await h.Workspaces.DeleteAsync(workspace.Id, Ct)).Next);
        Assert.Equal(0, await h.CountAsync("conversation") + await h.CountAsync("import") + await h.CountAsync("import_conversation"));
    }

    /// <summary>Shared back, the recipient's branch arrives as a branch of the sharer's own conversation (decision I4).</summary>
    [Fact]
    public async Task Received_conversations_and_the_users_branch_of_one_can_be_shared_again()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, twig) = await ImportPathAsync(h);
        var sent = await h.Conversations.AskAsync(branch, "How wide?", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        var source = await Shares(h).ReadAsync(h.Workspace, Ct);
        var selection = ShareSelection.Exploration(source.Parents, root);
        var structure = ExchangeDocument.Structure("Back", source, selection, Day, "0.7.0");
        Assert.Equal(new HashSet<ConversationId> { root, branch, twig, sent.Conversation }, structure.Conversations.Select(c => c.Id).ToHashSet());
        Assert.Equal(branch, structure.Conversations.Single(c => c.Id == sent.Conversation).Parent);
        Assert.Equal(0, structure.Conversations.Single(c => c.Id == branch).Questions);

        var content = await Share.ShareContentFlowTests.Contents(h).BuildAsync("Back", h.Workspace, source, selection, Day, "0.7.0", Ct);
        Assert.Null(content.Conversations.Single(c => c.Id == sent.Conversation).Content!.Source);
        Assert.Empty(content.Conversations.Single(c => c.Id == branch).Content!.Messages);
        Assert.Equal(2, content.Conversations.Single(c => c.Id == sent.Conversation).Content!.Messages.Count);
    }

    [Fact]
    public async Task Summarizing_the_users_branch_of_a_received_conversation_has_no_quoted_background()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _, _) = await ImportPathAsync(h);
        var sent = await h.Conversations.AskAsync(root, "How wide?", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        h.Client.Knowledge.Replies.Enqueue("Title: Gauge\nShort name: Gauge\n\nBody");

        Assert.True((await h.SummaryService.SummarizeAsync(sent.Conversation, Ct)).Accepted);
        await Until(async () => await h.SummaryService.ForConversationAsync(sent.Conversation, Ct) is not null);
    }

    [Fact]
    public async Task Holdings_name_the_workspace_of_what_exists()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _, _) = await ImportPathAsync(h);

        var held = await Imports(h).HoldingsAsync(Document([Conversation(root, "x", 0)]), Ct);

        Assert.Equal((root, h.Workspace), Assert.Single(held.Select(p => (p.Key, p.Value))));
    }

    private static async Task Until(Func<Task<bool>> met)
    {
        for (var i = 0; i < 250; i++)
        {
            if (await met())
            {
                return;
            }

            await Task.Delay(20, Ct);
        }

        Assert.Fail("Not met in time.");
    }

    private static Workspace NewWorkspace() => new(WorkspaceId.New(), WorkspaceName.FromStored("Trains"), Day);

    private static ExchangeConversation Conversation(ConversationId? parent, string? name, int minute) =>
        new(ConversationId.New(), parent, name, Day.AddMinutes(minute), 2, false);

    private static ExchangeDocument Document(IReadOnlyList<ExchangeConversation> conversations) =>
        new(ExchangeDocument.StructureKind, "How trains came to be", Day, "0.7.0", conversations, []);

    private static ExchangeDocument TheShare(ConversationId root, ConversationId branch) =>
        Document([new(root, null, "Trains", Day, 3, false), new(branch, root, "Standard gauge", Day.AddMinutes(9), 1, true)]);

    private static async Task<(ConversationId Root, ConversationId Branch, ConversationId Twig)> ImportPathAsync(ChatHarness h)
    {
        var (root, branch) = (ConversationId.New(), ConversationId.New());
        var twig = Conversation(branch, null, 20);
        var result = await Imports(h).ImportAsync(Document([twig, .. TheShare(root, branch).Conversations]), ImportTarget.Existing(h.Workspace), Ct);
        Assert.Equal(3, result.Created.Count);
        return (root, branch, twig.Id);
    }

    /// <summary>Every row that an import could touch.</summary>
    private static Task<string> StateAsync(ChatHarness h) => h.Database.ReadAsync(
        connection => string.Join('\n', new[] { "workspace", "conversation", "branch_source", "branch_context_range", "import", "import_conversation", "conversation_archive", "message" }
            .Select(table =>
            {
                using var command = connection.Command(null, $"SELECT * FROM {table} ORDER BY 1, 2;");
                using var reader = command.ExecuteReader();
                var rows = new List<string>();
                while (reader.Read())
                {
                    rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString())));
                }

                return table + ": " + string.Join('\n', rows);
            })),
        Ct);

    private static async Task<(ConversationId Conversation, Message Answer)> StartAsync(ChatHarness h, string question)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        return (sent.Conversation, await h.WaitForAnswerAsync(sent.Answer!.Value));
    }

    private static async Task<(ConversationId Conversation, Message Answer)> BranchAsync(ChatHarness h, ConversationId parent, Message answer, string? title)
    {
        var child = (await h.Conversations.CreateBranchAsync(parent, BranchFlowTests.Selection(answer, answer.Text[..4]), Ct)).Child;
        var sent = await h.Conversations.AskAsync(child, "And this?", Ct);
        var reply = await h.WaitForAnswerAsync(sent.Answer!.Value);
        if (title is not null)
        {
            await h.Conversations.RenameAsync(child, title, Ct);
        }

        return (child, reply);
    }

    private static ShareService Shares(ChatHarness h) =>
        new(h.Store, h.SummaryNodes, new SqliteSummaryNetworkStore(h.Database), new SqliteSummaryLinkStore(h.Database), new SharePageWriter(), new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance), NullLogger<ShareService>.Instance);

    private static ImportService Imports(ChatHarness h) =>
        new(new SqliteImportStore(h.Database), h.Conversations, h.Health, new SystemClock(), NullLogger<ImportService>.Instance);
}
