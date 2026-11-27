using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Export;
using Mindweave.Application.Import;
using Mindweave.Application.Models;
using Mindweave.Application.Share;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Runs;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Export;
using Mindweave.Infrastructure.Import;
using Mindweave.Infrastructure.Share;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Infrastructure.Tests.Share;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Import;

/// <summary>Importing a share with content through the real stores, and continuing from it (docs/design/sharing-import.md §9, scenarios 6 to 13).</summary>
public sealed class ImportContentFlowTests : IDisposable
{
    private static readonly ModelSettingsInput ToolModel = new(
        "fake", "https://fake.example/v1", "fake-model", "sk-test",
        Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-import-content", Guid.NewGuid().ToString("N"));

    public ImportContentFlowTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Acceptance 6 and 8, the round trip: what one data folder shares arrives in another as it was; the sharer goes on and
    /// shares again, and only the new parts arrive.
    /// </summary>
    [Fact]
    public async Task A_share_arrives_as_it_was_said_and_a_newer_one_adds_only_what_is_new()
    {
        await using var sharer = await ChatHarness.StartAsync();
        sharer.Client.Titles.FailNext(GenerationErrorKind.Unauthorized, 20);
        var root = await StartAsync(sharer, "How did trains begin?", "With steam engines on rails.");
        var replaced = await AskAsync(sharer, root.Conversation, "And then?", "First they were slow.");
        var fromReplaced = await BranchAsync(sharer, root.Conversation, replaced, "slow", "How slow?", "Walking pace.");
        sharer.Client.Reply("Then came electricity.");
        var retried = await sharer.Conversations.RetryAsync(root.Conversation, replaced.Id, Ct);
        await sharer.WaitForAnswerAsync(retried.Answer!.Value);
        var branch = await BranchAsync(sharer, root.Conversation, root.Answer, "steam engines", "Who built one?", "Newcomen did.");
        var twig = await BranchAsync(sharer, branch.Conversation, branch.Answer, "Newcomen", "When?", "In 1712.");
        await SummarizeAsync(sharer, root.Conversation, "Title: Trains\n\nFrom steam on.");
        await SummarizeAsync(sharer, twig.Conversation, "Title: Newcomen\n\nAn ironmonger.");
        await sharer.Links.CreateAsync(await NodeAsync(sharer, root.Conversation), await NodeAsync(sharer, twig.Conversation), "the start", Ct);
        var first = await ShareAsync(sharer, root.Conversation);

        await using var h = await ChatHarness.StartAsync();
        var result = await ImportAsync(h, first);

        Assert.Equal((ImportStatus.Imported, 4, root.Conversation), (result.Status, result.Created.Count, result.Opens!.Value));
        var again = await ShareAsync(h, root.Conversation);
        Assert.Equal(first.Links.Select(l => (StoredContents.Pair(l.A, l.B), l.Note)), again.Links.Select(l => (StoredContents.Pair(l.A, l.B), l.Note)));
        Assert.Equal(first.Conversations.Select(c => c with { Content = null }), again.Conversations.Select(c => c with { Content = null }));
        Assert.Equal(first.Conversations.Select(c => c.Content!.Source), again.Conversations.Select(c => c.Content!.Source));
        Assert.Equal(first.Conversations.Select(c => c.Content!.Summary), again.Conversations.Select(c => c.Content!.Summary));
        Assert.Equal(first.Conversations.SelectMany(c => c.Content!.Messages), again.Conversations.SelectMany(c => c.Content!.Messages));
        Assert.All(await h.Conversations.ListAsync(h.Workspace, Ct), r => Assert.True(r.Conversation.IsImported && !r.IsUnexplored));
        Assert.Equal([replaced.Id], (await h.Store.GetMessagesAsync(root.Conversation, Ct)).Where(m => m.Supersedes is not null).Select(m => m.Supersedes!.Value));

        // Context as it was there: up to the message each branch left from, with the attempt that stood for each question.
        Assert.Equal(
            [(root.Conversation, null, 1), (branch.Conversation, "steam engines", 1)],
            (await h.Branches.GetAncestorContextAsync(twig.Conversation, Ct)).Select(s => (s.Conversation, s.Quote, s.Messages[^1].Seq)));
        Assert.Equal(replaced.Id, (await h.Branches.GetAncestorContextAsync(fromReplaced.Conversation, Ct))[0].Messages[^1].Id);

        // Summaries are current, unread and joined.
        var summary = (await h.SummaryNodes.ForConversationAsync(root.Conversation, Ct))!;
        Assert.Equal(("Trains", "Trains", true, false), (summary.Node.DisplayName, summary.Current.Title, summary.Markers.Unread, summary.Markers.HasLaterContent));
        Assert.Equal("the start", Assert.Single(await new SqliteSummaryLinkStore(h.Database).ListAsync(h.Workspace, Ct)).Note);

        // The same again, then the sharer goes on.
        var before = await StateAsync(h);
        Assert.Equal(ImportStatus.NothingNew, (await ImportAsync(h, first)).Status);
        Assert.Equal(before, await StateAsync(h));
        await AskAsync(sharer, root.Conversation, "And today?", "Faster than ever.");
        var later = await BranchAsync(sharer, twig.Conversation, twig.Answer, "1712", "Where?", "In Dudley.");
        await SummarizeAsync(sharer, root.Conversation, "Title: Trains so far\n\nFrom steam to today.");
        await sharer.SummaryService.AdoptAsync(await NodeAsync(sharer, root.Conversation), (await sharer.SummaryService.ForConversationAsync(root.Conversation, Ct))!.Node.Pending!.Value, Ct);
        var second = await ShareAsync(sharer, root.Conversation);

        var added = await ImportAsync(h, second);
        Assert.Equal([later.Conversation], added.Created);
        Assert.Equal([root.Conversation], added.Gained);
        Assert.Equal([root.Conversation], added.Summarized);
        Assert.Equal("And today?", (await h.Store.GetMessagesAsync(root.Conversation, Ct))[^2].Text);

        // The newer summary is one more saved version; the one that was current stays so (I2).
        var detail = (await h.SummaryNodes.GetAsync(summary.Node.Id, Ct))!;
        Assert.Equal((summary.Node.Current, 2), (detail.Node.Current, detail.Versions.Count));
        Assert.Equal("Trains so far", detail.Versions[^1].Title);
        Assert.Equal(1, await h.CountAsync("summary_link"));
        Assert.Equal(ImportStatus.NothingNew, (await ImportAsync(h, second)).Status);

        // An older file brings nothing back.
        Assert.Equal(ImportStatus.NothingNew, (await ImportAsync(h, first)).Status);
    }

    /// <summary>Acceptance 7.</summary>
    [Fact]
    public async Task An_outline_then_the_same_with_content_fills_what_was_empty_and_leaves_what_is_the_users()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, twig, document) = ThePath();
        await ImportAsync(h, document.Structure());
        var mine = await AskAsync(h, branch.Id, "What was the first engine for?", "Pumping water.", opens: true);
        await h.Conversations.RenameAsync(branch.Id, "My own name", Ct);
        var marks = await new SqliteImportStore(h.Database).MarksAsync(h.Workspace, Ct);
        var before = await RowsAsync(h, "branch_source", mine.ConversationId);
        var contextBefore = await RowsAsync(h, "branch_context_range", mine.ConversationId);

        var result = await ImportAsync(h, document);

        Assert.Empty(result.Created);
        Assert.Equal([root.Id, branch.Id, twig.Id], result.Gained);
        Assert.Equal(root.Id, result.Opens);
        Assert.Equal(root.Content!.Messages.Select(m => m.Text), (await h.Store.GetMessagesAsync(root.Id, Ct)).Select(m => m.Text));
        var source = (await h.Conversations.GetSourceAsync(branch.Id, Ct))!;
        Assert.Equal((branch.Content!.Source!.Message, "steam engines"), (source.SourceMessage!.Value, source.Quote));
        Assert.Equal([root.Id, branch.Id], (await h.Branches.GetAncestorContextAsync(twig.Id, Ct)).Select(s => s.Conversation));
        Assert.Equal("My own name", (await h.Store.GetAsync(branch.Id, Ct))!.Title);
        Assert.All(await h.Conversations.ListAsync(h.Workspace, Ct), r => Assert.False(r.IsUnexplored));

        // The user's branch hangs where it did, with the context it was answered with; the record names the first file.
        Assert.Equal(before, await RowsAsync(h, "branch_source", mine.ConversationId));
        Assert.Equal(contextBefore, await RowsAsync(h, "branch_context_range", mine.ConversationId));
        Assert.Empty(await h.Branches.GetAncestorContextAsync(mine.ConversationId, Ct));
        Assert.Equal(marks, await new SqliteImportStore(h.Database).MarksAsync(h.Workspace, Ct));
        Assert.Equal(1, await h.CountAsync("import"));
    }

    /// <summary>§2: when a source arrives, the received branches below get their context too, but not through one of the user's own.</summary>
    [Fact]
    public async Task A_source_that_arrives_later_sets_the_context_of_the_received_branches_below_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, twig, document) = ThePath();
        var late = document with { Conversations = [root, branch with { Content = branch.Content! with { Source = null } }, twig] };
        await ImportAsync(h, late);
        var mine = await AskAsync(h, branch.Id, "Mine?", "Yours.", opens: true);
        var below = SharedParts.Conversation(SharedParts.Conversation(null, null, null) with { Id = mine.ConversationId }, "Someone else's", new(mine.Id, "Yours", null, null), SharedParts.Question(0, "Below mine?"), SharedParts.Answer(1, "Yes."));
        await ImportAsync(h, SharedParts.Document(below));
        Assert.Equal([(branch.Id, 1)], await RangesAsync(h, twig.Id));
        Assert.Equal([(branch.Id, 1), (mine.ConversationId, 1)], await RangesAsync(h, below.Id));

        await ImportAsync(h, document);

        Assert.Equal([(root.Id, 1), (branch.Id, 1)], await RangesAsync(h, twig.Id));
        Assert.Equal([(branch.Id, 1)], await RangesAsync(h, mine.ConversationId));
        Assert.Equal([(branch.Id, 1), (mine.ConversationId, 1)], await RangesAsync(h, below.Id));
    }

    /// <summary>Acceptance 6, the second half: the user's question at the end is a conversation of their own that builds on the whole received one.</summary>
    [Fact]
    public async Task A_question_at_the_end_of_a_received_conversation_continues_it_in_one_of_the_users_own()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, _, document) = ThePath();
        await ImportAsync(h, document);
        var before = await StateAsync(h, "message", "message_block", "branch_source", "branch_context_range", "import_conversation");

        h.Client.Reply("Because of the boiler.");
        var changed = new List<ConversationId>();
        h.Conversations.ConversationChanged += (_, id) => changed.Add(id);
        var sent = await h.Conversations.AskAsync(branch.Id, "Why did it burst?", Ct);

        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.NotEqual(branch.Id, sent.Conversation);
        Assert.Contains(branch.Id, changed); // A page open on it lists the continuation.
        var own = (await h.Conversations.ListAsync(h.Workspace, Ct)).Single(r => r.Conversation.Id == sent.Conversation);
        Assert.Equal((branch.Id, (string?)null, false), (own.Parent!.Value, own.Quote, own.Conversation.IsImported));
        var source = (await h.Conversations.GetSourceAsync(sent.Conversation, Ct))!;
        Assert.True(source.IsContinuation);
        Assert.Equal(branch.Content!.Messages[^1].Id, source.SourceMessage);
        Assert.Contains(await h.Conversations.ListBranchesAsync(branch.Id, Ct), b => b.Child == sent.Conversation);

        // What the model got: the path above, the whole received conversation with its quote, then the question.
        var request = h.Client.Requests.Last(FakeChatClient.IsAnswer);
        var turns = request.Turns.Where(t => t.Role != Application.Ai.ChatRole.System).Select(t => t.Content).ToList();
        Assert.Equal(["How did trains begin?", "With steam engines on rails."], turns[..2]);
        Assert.Contains("steam engines", turns[2], StringComparison.Ordinal);
        Assert.Contains("Who built one?", turns[2], StringComparison.Ordinal);
        Assert.Equal(["Newcomen did.", "Why did it burst?"], turns[3..]);
        Assert.Equal([root.Id, branch.Id], (await h.Branches.GetAncestorContextAsync(sent.Conversation, Ct)).Select(s => s.Conversation));
        Assert.Equal("Because of the boiler.", answer.Text);

        // Its next question goes into it, with the same context; a retry of its answer works as anywhere.
        h.Client.Reply("In 1725.");
        var next = await h.Conversations.AskAsync(sent.Conversation, "When?", Ct);
        Assert.Equal(sent.Conversation, next.Conversation);
        await h.WaitForAnswerAsync(next.Answer!.Value);
        var retried = await h.Conversations.RetryAsync(sent.Conversation, next.Answer!.Value, Ct);
        Assert.True(retried.Accepted, retried.Rejection?.ToString());
        await h.WaitForAnswerAsync(retried.Answer!.Value);

        // Nothing of what was received changed.
        var after = await StateAsync(h, "message", "message_block", "branch_source", "branch_context_range", "import_conversation");
        Assert.All(before.Split('\n'), line => Assert.Contains(line, after, StringComparison.Ordinal));
    }

    /// <summary>Summarizing, branching and the other things the user may do with a received conversation work on it and on a continuation.</summary>
    [Fact]
    public async Task A_received_conversation_can_be_summarized_branched_renamed_archived_and_deleted()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, _, document) = ThePath();
        await ImportAsync(h, document);
        var mine = await AskAsync(h, branch.Id, "Why?", "Heat.", opens: true);
        await SummarizeAsync(h, branch.Id, "Title: My summary\n\nOf theirs.");
        await SummarizeAsync(h, mine.ConversationId, "Title: Mine\n\nOf mine.");
        var answer = (await h.Store.GetMessagesAsync(root.Id, Ct))[1];
        Assert.True((await h.Conversations.CreateBranchAsync(root.Id, BranchFlowTests.Selection(answer, "rails"), Ct)).Accepted);
        await h.Conversations.RenameAsync(root.Id, "Renamed", Ct);
        await h.SettleAsync(mine.ConversationId);

        Assert.True((await h.Conversations.ArchiveAsync(root.Id, Ct)).Archived);
        Assert.Equal(SendRejection.ConversationArchived, (await h.Conversations.AskAsync(branch.Id, "More?", Ct)).Rejection);
        await h.Conversations.RestoreAsync(root.Id, Ct);
        Assert.True((await h.ConversationDeletion.DeleteAsync(await h.ConversationDeletion.InspectAsync([root.Id], Ct), Ct)).IsDeleted);
        Assert.Equal(0, await h.CountAsync("conversation") + await h.CountAsync("message") + await h.CountAsync("message_block") + await h.CountAsync("summary_node") + await h.CountAsync("import"));
        Assert.Equal(ImportStatus.Imported, (await ImportAsync(h, document)).Status);
    }

    [Fact]
    public async Task A_workspace_with_what_was_received_and_continued_can_be_deleted()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, branch, _, document) = ThePath();
        var workspace = new Workspace(WorkspaceId.New(), WorkspaceName.FromStored("Trains"), SharedParts.Day);
        await Imports(h).ImportAsync(document, ImportTarget.New(workspace), Ct);
        await AskAsync(h, branch.Id, "Why?", "Heat.", opens: true);

        Assert.NotNull((await h.Workspaces.DeleteAsync(workspace.Id, Ct)).Next);
        Assert.Equal(0, await h.CountAsync("message") + await h.CountAsync("import"));
    }

    /// <summary>I4: whatever asks, nothing is added to a received conversation.</summary>
    [Fact]
    public async Task A_received_conversation_refuses_a_question_and_a_retry_at_the_store_and_a_retry_in_the_service()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _, _, document) = ThePath();
        await ImportAsync(h, document);
        var last = (await h.Store.GetMessagesAsync(root.Id, Ct))[^1];
        var model = (await h.Models.GetActiveSnapshotAsync(Ct))!;
        GenerationTaskRecord Task(MessageId target) => new(GenerationTaskId.New(), GenerationTaskKind.Answer, root.Id, target, model, [], SharedParts.Day);
        var question = new Message(MessageId.New(), root.Id, 0, MessageRole.User, [new TextBlock("?")], MessageState.Complete, 0, null, null, null, SharedParts.Day, SharedParts.Day);
        var answer = new Message(MessageId.New(), root.Id, 0, MessageRole.Assistant, [new TextBlock("")], MessageState.Pending, 0, null, null, null, SharedParts.Day, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.AppendQuestionAsync(question, answer, Task(answer.Id), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.AddRetryAsync(answer with { Supersedes = last.Id }, Task(answer.Id), Ct));
        Assert.Equal(SendRejection.NotRetryable, (await h.Conversations.RetryAsync(root.Id, last.Id, Ct)).Rejection);
        Assert.Equal(root.Content!.Messages.Count, (await h.Store.GetMessagesAsync(root.Id, Ct)).Count);
    }

    /// <summary>Acceptance 9.</summary>
    [Fact]
    public async Task Both_sides_go_on_and_each_gets_the_others_without_losing_its_own()
    {
        await using var sharer = await ChatHarness.StartAsync();
        sharer.Client.Titles.FailNext(GenerationErrorKind.Unauthorized, 20);
        var root = await StartAsync(sharer, "How did trains begin?", "With steam engines.");
        await using var h = await ChatHarness.StartAsync();
        h.Client.Titles.FailNext(GenerationErrorKind.Unauthorized, 20);
        await ImportAsync(h, await ShareAsync(sharer, root.Conversation));
        var mine = await AskAsync(h, root.Conversation, "Who built one?", "Newcomen did.", opens: true);
        var context = await RowsAsync(h, "branch_context_range", mine.ConversationId);

        // The sharer continues too; the newer share only adds here.
        await AskAsync(sharer, root.Conversation, "And then?", "Electricity.");
        var result = await ImportAsync(h, await ShareAsync(sharer, root.Conversation));
        Assert.Equal([root.Conversation], result.Gained);
        Assert.Equal(4, (await h.Store.GetMessagesAsync(root.Conversation, Ct)).Count);
        Assert.Equal(context, await RowsAsync(h, "branch_context_range", mine.ConversationId));
        Assert.Equal(2, Assert.Single(await h.Branches.GetAncestorContextAsync(mine.ConversationId, Ct)).Messages.Count);

        // Shared back, it is a branch of the sharer's own conversation, with the context it had.
        var back = await ImportAsync(sharer, await ShareAsync(h, root.Conversation));
        Assert.Equal([mine.ConversationId], back.Created);
        Assert.Empty(back.Gained);
        Assert.False((await sharer.Store.GetAsync(root.Conversation, Ct))!.IsImported);
        var source = (await sharer.Conversations.GetSourceAsync(mine.ConversationId, Ct))!;
        Assert.Equal((root.Conversation, root.Answer.Id, true), (source.Parent, source.SourceMessage!.Value, source.IsContinuation));
        Assert.Equal(2, Assert.Single(await sharer.Branches.GetAncestorContextAsync(mine.ConversationId, Ct)).Messages.Count);
        Assert.True((await sharer.Store.GetAsync(mine.ConversationId, Ct))!.IsImported);

        // And continued there in turn.
        var further = await AskAsync(sharer, mine.ConversationId, "Is that so?", "It is.", opens: true);
        Assert.Equal([root.Conversation, mine.ConversationId], (await sharer.Branches.GetAncestorContextAsync(further.ConversationId, Ct)).Select(s => s.Conversation));
    }

    /// <summary>A file that arrives while a question is being sent: what is stored as its context is what was sent.</summary>
    [Fact]
    public async Task A_source_that_arrives_while_the_question_is_sent_does_not_change_what_it_was_asked_about()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, twig, document) = ThePath();
        await ImportAsync(h, document with { Conversations = [root, branch with { Content = branch.Content! with { Source = null } }, twig] });
        var context = await h.Branches.GetAncestorContextAsync(branch.Id, Ct);
        Assert.Empty(context);
        var messages = await h.Store.GetMessagesAsync(branch.Id, Ct);
        await ImportAsync(h, document);

        var id = ConversationId.New();
        var question = new Message(MessageId.New(), id, 0, MessageRole.User, [new TextBlock("?")], MessageState.Complete, 0, null, null, null, SharedParts.Day, SharedParts.Day);
        var answer = new Message(MessageId.New(), id, 1, MessageRole.Assistant, [new TextBlock("")], MessageState.Pending, 0, null, null, null, SharedParts.Day, null);
        var task = new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Answer, id, answer.Id, (await h.Models.GetActiveSnapshotAsync(Ct))!, [question.Id], SharedParts.Day);
        await h.Store.CreateBranchAsync(branch.Id, [new ContextSegment(branch.Id, null, messages)], new Conversation(id, h.Workspace, null, TitleSource.None, SharedParts.Day, SharedParts.Day), question, answer, task, Ct);

        Assert.Equal([(branch.Id, 1)], await RangesAsync(h, id));
        Assert.Equal([root.Id, branch.Id], (await h.Conversations.GetLineageAsync(id, Ct)).SkipLast(1).Select(e => e.Conversation.Id));
    }

    /// <summary>Acceptance 10.</summary>
    [Fact]
    public async Task A_title_a_summary_and_an_archive_of_the_users_stay_and_the_files_summary_is_a_further_version()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _, _, document) = ThePath();
        await ImportAsync(h, document);
        await h.Conversations.RenameAsync(root.Id, "My name for it", Ct);
        await SummarizeAsync(h, root.Id, "Title: My summary\n\nIn my words.");
        await h.Conversations.ArchiveAsync(root.Id, Ct);
        var mine = (await h.SummaryNodes.ForConversationAsync(root.Id, Ct))!;
        var theirs = SharedParts.Summary("Theirs", 1);

        var result = await ImportAsync(h, document with { Conversations = [root.With(theirs), .. document.Conversations.Skip(1)] });

        Assert.Equal([root.Id], result.Summarized);
        var detail = (await h.SummaryNodes.GetAsync(mine.Node.Id, Ct))!;
        Assert.Equal((mine.Node.Current, mine.Node.DisplayName, mine.Node.UpdatedAt), (detail.Node.Current, detail.Node.DisplayName, detail.Node.UpdatedAt));
        var further = detail.Versions.Single(v => v.Id == theirs.Id);
        Assert.Equal((1, "Theirs", theirs.Body, null, null), (further.ToSeq, further.Title, further.Body, further.Task, further.Model));
        Assert.DoesNotContain(theirs.Id, detail.ReadStates.Keys);
        Assert.Equal("My name for it", (await h.Store.GetAsync(root.Id, Ct))!.Title);
        Assert.All(await h.Conversations.ListAsync(h.Workspace, Ct), r => Assert.True(r.IsHidden));
    }

    /// <summary>A summary being written while a file brings one: the one that arrived is current, the user's waits to be adopted.</summary>
    [Fact]
    public async Task A_summary_that_arrives_while_the_user_summarizes_makes_theirs_a_candidate()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _, _, document) = ThePath();
        await ImportAsync(h, document);
        var gate = h.Client.Knowledge.Hold();
        Assert.True((await h.SummaryService.SummarizeAsync(root.Id, Ct)).Accepted);
        var theirs = SharedParts.Summary("Theirs", 1);
        await ImportAsync(h, document with { Conversations = [root.With(theirs), .. document.Conversations.Skip(1)] });
        gate.TrySetResult();

        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(root.Id, Ct) is { State: GenerationTaskState.Completed });
        var node = (await h.SummaryNodes.ForConversationAsync(root.Id, Ct))!.Node;
        Assert.Equal(theirs.Id, node.Current);
        Assert.NotNull(node.Pending);
    }

    /// <summary>Acceptance 11 (I3): an address in what was received is not the user's own, along the whole chain; one the user types is.</summary>
    [Fact]
    public async Task An_address_in_received_text_waits_for_approval_in_a_continuation_and_in_a_branch_below_it()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        await h.WebSearch.SetKeyAsync("tvly-secret-key-1234567890", Ct);
        h.Pages.Page("https://theirs.example/a", "Theirs", "Their page.");
        h.Pages.Page("https://mine.example/b", "Mine", "My page.");
        var asked = SharedParts.Question(0, "Read https://theirs.example/a please.");
        var root = SharedParts.Conversation(null, "Trains", null, asked, SharedParts.Answer(1, "See https://theirs.example/a for more."));
        await ImportAsync(h, SharedParts.Document(root));

        async Task<(SendOutcome Sent, LiveAnswer Live)> AskForAsync(ConversationId id, string url, string question)
        {
            h.Client.CallTool(FetchPageTool.ToolName, JsonSerializer.Serialize(new { url }));
            h.Client.Reply("Read.");
            var sent = await h.Conversations.AskAsync(id, question, [], new SendOptions(WebSearchMode.Auto), Ct);
            Assert.True(sent.Accepted, sent.Rejection?.ToString());
            return (sent, h.Conversations.Observe(sent.Answer!.Value)!);
        }

        async Task WaitsAsync((SendOutcome Sent, LiveAnswer Live) run)
        {
            await ChatHarness.UntilAsync(() => Task.FromResult(run.Live.Snapshot().PendingApproval is not null));
            var card = run.Live.Snapshot().PendingApproval!;
            Assert.Equal("https://theirs.example/a", card.Target);
            Assert.True(h.Conversations.DecideApproval(run.Sent.Answer!.Value, card.CallId, allow: true));
            await h.WaitForAnswerAsync(run.Sent.Answer!.Value);
        }

        var continued = await AskForAsync(root.Id, "https://theirs.example/a", "Read that page and https://mine.example/b.");
        await WaitsAsync(continued);

        // Still so when it is tried again, in its next question, and in a branch of it; the user's own address needs no asking in either.
        var own = continued.Sent.Conversation;
        h.Client.CallTool(FetchPageTool.ToolName, JsonSerializer.Serialize(new { url = "https://theirs.example/a" }));
        h.Client.Reply("Read.");
        var retried = await h.Conversations.RetryAsync(own, continued.Sent.Answer!.Value, new SendOptions(WebSearchMode.On), Ct);
        await WaitsAsync((retried, h.Conversations.Observe(retried.Answer!.Value)!));
        await WaitsAsync(await AskForAsync(own, "https://theirs.example/a", "Once more."));
        var mine = await AskForAsync(own, "https://mine.example/b", "And mine.");
        var answer = await h.WaitForAnswerAsync(mine.Sent.Answer!.Value);
        Assert.Equal(ToolResultStatus.Succeeded, answer.Blocks.OfType<ToolResultBlock>().Single().Status);
        var twig = (await h.Conversations.CreateBranchAsync(own, BranchFlowTests.Selection(answer, "Read"), Ct)).Child;
        await WaitsAsync(await AskForAsync(twig, "https://theirs.example/a", "Here too?"));
    }

    /// <summary>Acceptance 12, as it is stored and sent: names without files, citations without material, counts without steps.</summary>
    [Fact]
    public async Task What_did_not_travel_has_a_stand_in_that_survives_being_shared_again()
    {
        await using var h = await ChatHarness.StartAsync();
        var asked = SharedParts.Question(0, "What is in these?", "plan.pdf", null);
        var told = SharedParts.Answer(1, "A gauge [1], as noted [2][3][4].") with
        {
            Activity = new ExchangeActivity(1, 2, 3, 4, 5),
            Citations =
            [
                new(1, CitationKind.Web, "Gauges", new Uri("https://example.org/gauges")),
                new(2, CitationKind.Attachment),
                new(3, CitationKind.Summary),
                new(4, CitationKind.Summary, "Steam"),
            ],
        };
        var root = SharedParts.Conversation(null, "Trains", null, asked, told);
        var branch = SharedParts.Conversation(root, "Steam", SharedParts.From(told, "gauge"), SharedParts.Question(0, "", "only.txt"), SharedParts.Answer(1, "Seen.", MessageState.Stopped)).With(SharedParts.Summary("Steam", 1));
        var full = root with { Content = root.Content! with { Messages = [asked, told with { Citations = [.. told.Citations.Take(3), told.Citations[3] with { Summary = branch.Id }] }] } };
        var document = SharedParts.Document(full, branch) with { Conversations = [full with { Questions = 1 }, branch with { Questions = 1 }] };

        await ImportAsync(h, document);

        var messages = await h.Store.GetMessagesAsync(root.Id, Ct);
        Assert.Equal([new AbsentAttachmentBlock("plan.pdf"), new AbsentAttachmentBlock(null), new TextBlock("What is in these?")], messages[0].Blocks);
        Assert.Equal(new ActivityCountBlock(1, 2, 3, 4, 5), Assert.Single(messages[1].Blocks.OfType<ActivityCountBlock>()));
        var citations = ContentBlocks.CitationsOf(messages[1].Blocks);
        Assert.Equal([true, false, false, false], citations.Select(c => c.CanBeOpened));
        Assert.Equal(["Gauges", "", "", "Steam"], citations.Select(c => c.Title));
        Assert.Equal((MessageState.Stopped, SharedParts.Day.AddMinutes(1)), ((await h.Store.GetMessagesAsync(branch.Id, Ct))[1].State, (await h.Store.GetMessagesAsync(branch.Id, Ct))[1].CompletedAt!.Value));
        Assert.Equal(0, await h.CountAsync("resource"));

        // Shared again, it leaves as it came.
        Assert.True((await ShareAsync(h, root.Id)).SameContents(document));

        // The model is told that attachments were there, and no more.
        await AskAsync(h, root.Id, "And now?", "Nothing.", opens: true);
        var turns = h.Client.Requests.Last(FakeChatClient.IsAnswer).Turns;
        var first = turns.First(t => t.Content.Contains("What is in these?", StringComparison.Ordinal)).Content;
        Assert.DoesNotContain("plan.pdf", first, StringComparison.Ordinal);
        Assert.Contains(Application.Languages.ModelPhrases.Current.AttachmentNotIncluded, first, StringComparison.Ordinal);
        Assert.Contains(turns, t => t.Content.Contains("[1] Gauges https://example.org/gauges", StringComparison.Ordinal));
    }

    /// <summary>§6.6, point 4 of the design: whichever file brings which attempt, one answer to a question stands.</summary>
    [Fact]
    public async Task Attempts_that_arrive_in_different_files_leave_one_standing_answer()
    {
        await using var h = await ChatHarness.StartAsync();
        var asked = SharedParts.Question(0, "Q?");
        var (one, two, three) = (SharedParts.Answer(1, "One."), SharedParts.Answer(2, "Two."), SharedParts.Answer(3, "Three."));
        var root = SharedParts.Conversation(null, "Trains", null, asked, two);
        async Task<IReadOnlyList<MessageId?>> AfterAsync(params ExchangeMessage[] messages)
        {
            await ImportAsync(h, SharedParts.Document(root with { Content = root.Content! with { Messages = messages } }));
            return [.. (await h.Store.GetMessagesAsync(root.Id, Ct)).Select(m => m.Supersedes)];
        }

        Assert.Equal([null, null], await AfterAsync(asked, two));
        Assert.Equal([null, null, two.Id], await AfterAsync(asked, three));
        Assert.Equal([null, null, one.Id, two.Id], await AfterAsync(asked, one with { ReplacedBy = three.Id }, three));
        Assert.Equal(["Q?", "Three."], ConversationRules.SelectContext(await h.Store.GetMessagesAsync(root.Id, Ct)).Select(m => m.Text));
    }

    /// <summary>§2 and §7: a file that does not fit changes nothing of what it does not fit.</summary>
    [Fact]
    public async Task What_does_not_fit_is_skipped_and_the_rest_arrives()
    {
        await using var h = await ChatHarness.StartAsync();
        var own = await StartAsync(h, "Mine?", "Yours.");
        var root = SharedParts.Conversation(null, "Trains", null, SharedParts.Question(0, "Q?"), SharedParts.Answer(1, "A."));
        await ImportAsync(h, SharedParts.Document(root));
        var before = await StateAsync(h, "message", "conversation");

        var other = SharedParts.Conversation(null, "Other", null, SharedParts.Question(0, "Other?"), SharedParts.Answer(1, "Yes."));
        var clash = root with { Content = root.Content! with { Messages = [root.Content.Messages[0], SharedParts.Answer(1, "Not A."), SharedParts.Question(2, "More?"), SharedParts.Answer(3, "More.")] } };
        var theirs = SharedParts.Conversation(null, "Mine", null, SharedParts.Question(5, "Not mine?"), SharedParts.Answer(6, "No.")) with { Id = own.Conversation };
        var below = SharedParts.Conversation(clash, "Below", SharedParts.From(clash.Content!.Messages[3], "More"), SharedParts.Question(0, "Below?"), SharedParts.Answer(1, "Yes."));
        var stolen = SharedParts.Conversation(null, "Stolen", null, root.Content.Messages[0] with { Seq = 0 });
        var result = await ImportAsync(h, SharedParts.Document(clash, theirs, below, other));
        Assert.Equal(new HashSet<ConversationId> { other.Id, below.Id }, result.Created.ToHashSet());
        Assert.Empty(result.Gained);
        Assert.Equal(ImportStatus.NothingNew, (await ImportAsync(h, SharedParts.Document(stolen))).Status);

        // Its branch arrives without a source: the message it left from did not.
        Assert.Null((await h.Conversations.GetSourceAsync(below.Id, Ct))!.SourceMessage);
        Assert.Empty(await h.Branches.GetAncestorContextAsync(below.Id, Ct));
        var after = await StateAsync(h, "message", "conversation");
        Assert.All(before.Split('\n'), line => Assert.Contains(line, after, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_import_that_fails_partway_imports_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, _, _, document) = ThePath();
        await ImportAsync(h, document.Structure());
        var before = await StateAsync(h);

        h.Database.BeforeCommitForTests = (_, _) => throw new InvalidOperationException("simulated failure");
        await Assert.ThrowsAnyAsync<Exception>(() => ImportAsync(h, document));
        h.Database.BeforeCommitForTests = null;

        Assert.Equal(before, await StateAsync(h));
    }

    /// <summary>The user's own share, imported where it was made: everything is already here (§7).</summary>
    [Fact]
    public async Task The_users_own_share_with_content_is_already_here()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await StartAsync(h, "How did trains begin?", "With steam.");
        var branch = await BranchAsync(h, root.Conversation, root.Answer, "steam", "Why?", "Heat.");
        await SummarizeAsync(h, root.Conversation, "Title: Trains\n\nBody");
        var file = await ShareAsync(h, root.Conversation);
        await AskAsync(h, root.Conversation, "More?", "More.");
        var before = await StateAsync(h);

        Assert.Equal(ImportStatus.NothingNew, (await ImportAsync(h, file)).Status);
        Assert.Equal(before, await StateAsync(h));
        Assert.NotEqual(root.Conversation, branch.Conversation);
    }

    /// <summary>Pages hear of what they show: the conversations that gained something, their summaries and the links.</summary>
    [Fact]
    public async Task What_gained_something_is_announced()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, branch, twig, document) = ThePath();
        await ImportAsync(h, document.Structure());
        var (changed, summarized, linked) = (new List<ConversationId>(), new List<ConversationId>(), new List<WorkspaceId>());
        h.Conversations.ConversationChanged += (_, id) => changed.Add(id);
        h.SummaryService.Changed += (_, id) => summarized.Add(id);
        h.Links.Changed += (_, id) => linked.Add(id);

        var full = document with { Conversations = [root.With(SharedParts.Summary("A", 1)), branch.With(SharedParts.Summary("B", 1)), twig], Links = [new(root.Id, branch.Id) { Note = "n" }] };
        await ImportAsync(h, full);

        Assert.Equal(new HashSet<ConversationId> { root.Id, branch.Id, twig.Id }, changed.ToHashSet());
        Assert.Equal(new HashSet<ConversationId> { root.Id, branch.Id }, summarized.ToHashSet());
        Assert.Equal([h.Workspace], linked);
    }

    /// <summary>The statistics are of what the user did here: a received question is not one they asked.</summary>
    [Fact]
    public async Task Received_questions_are_not_counted_as_the_users()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, branch, _, document) = ThePath();
        await ImportAsync(h, document);
        await AskAsync(h, branch.Id, "Mine?", "Yours.", opens: true);

        var facts = await h.StatisticsStore.ActivityAsync(new ActivityQuery(null, TimeZoneInfo.Utc, DateTimeOffset.UtcNow.AddDays(-30), [1], [1]), Ct);
        Assert.Equal((1, 1), (facts.Questions, facts.ActiveDays));
        Assert.True(Assert.Single(facts.Milestones).Day > DateOnly.FromDateTime(SharedParts.Day.UtcDateTime));
    }

    private static (ExchangeConversation Root, ExchangeConversation Branch, ExchangeConversation Twig, ExchangeDocument Document) ThePath()
    {
        var root = SharedParts.Conversation(null, "Trains", null, SharedParts.Question(0, "How did trains begin?"), SharedParts.Answer(1, "With steam engines on rails."));
        var branch = SharedParts.Conversation(root, "Steam", SharedParts.From(root.Content!.Messages[1], "steam engines"), SharedParts.Question(0, "Who built one?"), SharedParts.Answer(1, "Newcomen did."));
        var twig = SharedParts.Conversation(branch, "Newcomen", SharedParts.From(branch.Content!.Messages[1], "Newcomen"), SharedParts.Question(0, "When?"), SharedParts.Answer(1, "In 1712."));
        return (root, branch, twig, SharedParts.Document(root, branch, twig));
    }

    /// <summary>Through the file, as the dialog does it.</summary>
    private async Task<ImportResult> ImportAsync(ChatHarness h, ExchangeDocument document)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".html");
        await Shares(h).SaveAsync(document, new ExportDestination("share.html", path, null), Ct);
        var read = await new SharedFileReader().ReadAsync(path, Ct);
        Assert.Null(read.Refusal);
        return await Imports(h).ImportAsync(read.Document!, ImportTarget.Existing(h.Workspace), Ct);
    }

    private static async Task<ExchangeDocument> ShareAsync(ChatHarness h, ConversationId root)
    {
        var source = await Shares(h).ReadAsync(h.Workspace, Ct);
        return await ShareContentFlowTests.Contents(h).BuildAsync("Trains", h.Workspace, source, ShareSelection.Exploration(source.Parents, root), SharedParts.Day, "0.7.0", Ct);
    }

    private static async Task<(ConversationId Conversation, Message Answer)> StartAsync(ChatHarness h, string question, string reply)
    {
        h.Client.Reply(reply);
        var sent = await h.Conversations.StartAsync(h.Workspace, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.TitleEndedAsync(sent.Conversation); // What is stored is compared.
        return (sent.Conversation, answer);
    }

    /// <param name="opens">Asked in a received conversation: the answer is in another one.</param>
    private static async Task<Message> AskAsync(ChatHarness h, ConversationId id, string question, string reply, bool opens = false)
    {
        h.Client.Reply(reply);
        var sent = await h.Conversations.AskAsync(id, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        Assert.Equal(opens, sent.Conversation != id);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.TitleEndedAsync(sent.Conversation);
        return answer;
    }

    private static async Task<(ConversationId Conversation, Message Answer)> BranchAsync(ChatHarness h, ConversationId parent, Message answer, string quote, string question, string reply)
    {
        var child = (await h.Conversations.CreateBranchAsync(parent, BranchFlowTests.Selection(answer, quote), Ct)).Child;
        return (child, await AskAsync(h, child, question, reply));
    }

    private static async Task SummarizeAsync(ChatHarness h, ConversationId id, string reply)
    {
        h.Client.Knowledge.Replies.Enqueue(reply);
        var before = await h.SummaryService.LatestTaskAsync(id, Ct);
        Assert.True((await h.SummaryService.SummarizeAsync(id, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(id, Ct) is { State: GenerationTaskState.Completed } done && done.Id != before?.Id);
    }

    private static async Task<SummaryNodeId> NodeAsync(ChatHarness h, ConversationId id) => (await h.SummaryNodes.ForConversationAsync(id, Ct))!.Node.Id;

    private static Task<IReadOnlyList<(ConversationId, int)>> RangesAsync(ChatHarness h, ConversationId id) => h.Database.ReadAsync<IReadOnlyList<(ConversationId, int)>>(
        connection => [.. SqliteBranchStore.ReadRanges(connection, null, id).Where(r => r.MaxSeq != BranchContextRange.Nothing).Select(r => (r.Ancestor, r.MaxSeq))], Ct);

    private static Task<string> RowsAsync(ChatHarness h, string table, ConversationId child) =>
        ReadAsync(h, $"SELECT * FROM {table} WHERE child_id = '{child}' ORDER BY 1, 2;");

    /// <summary>Every row that an import could touch.</summary>
    private static async Task<string> StateAsync(ChatHarness h, params string[] tables)
    {
        var all = tables.Length > 0 ? tables :
        [
            "workspace", "conversation", "branch_source", "branch_context_range", "import", "import_conversation", "conversation_archive", "message", "message_block",
            "summary_node", "summary_version", "summary_read_state", "summary_link",
        ];
        var state = new List<string>();
        foreach (var table in all)
        {
            state.Add(await ReadAsync(h, $"SELECT * FROM {table} ORDER BY 1, 2;"));
        }

        return string.Join('\n', state.Where(s => s.Length > 0));
    }

    private static Task<string> ReadAsync(ChatHarness h, string sql) => h.Database.ReadAsync(
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

    private static ShareService Shares(ChatHarness h) =>
        new(h.Store, h.SummaryNodes, new SqliteSummaryNetworkStore(h.Database), new SqliteSummaryLinkStore(h.Database), new SharePageWriter(new PlainMarkdown()), new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance), NullLogger<ShareService>.Instance);

    private static ImportService Imports(ChatHarness h) =>
        new(new SqliteImportStore(h.Database), h.Conversations, h.Reading, h.Health, new SystemClock(), NullLogger<ImportService>.Instance, h.SummaryService, h.Links);
}
