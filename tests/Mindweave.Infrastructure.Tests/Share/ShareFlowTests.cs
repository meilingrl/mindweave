using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Exchange;
using Mindweave.Application.Export;
using Mindweave.Application.Share;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Export;
using Mindweave.Infrastructure.Share;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Share;

/// <summary>
/// A structure-only share through the real stores (docs/design/sharing.md S6, S7, §6.6, §9): what is offered, what the file
/// holds, and above all what it does not.
/// </summary>
public sealed class ShareFlowTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-share-flow", Guid.NewGuid().ToString("N"));

    public ShareFlowTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Target => Path.Combine(_dir, "share.html");

    /// <summary>Acceptance 1 and 3: a marker planted in every place that must not leave is nowhere in the file's bytes.</summary>
    [Fact]
    public async Task The_file_holds_no_message_text_no_quote_no_summary_no_workspace_name_and_nothing_of_an_unticked_conversation()
    {
        await using var h = await ChatHarness.StartAsync();
        await h.WorkspaceService.RenameAsync(h.Workspace, MarkedName(), Ct);
        h.Client.Titles.Replies.Enqueue("Root title");
        h.Client.Reply("An answer with PLANTEDQUOTE and PLANTEDANSWER in it.");
        var root = await StartAsync(h, "PLANTEDQUESTION first?");
        await UntilTitledAsync(h, root.Conversation);

        // A branch that keeps the share's rules hardest: no title of its own, so the app would show its quote or question.
        var untitled = (await h.Conversations.CreateBranchAsync(root.Conversation, BranchFlowTests.Selection(root.Answer, "PLANTEDQUOTE"), Ct)).Child;

        h.Client.Titles.Replies.Enqueue("PLANTEDUNTICKED title");
        var unticked = (await h.Conversations.CreateBranchAsync(root.Conversation, BranchFlowTests.Selection(root.Answer, "PLANTEDANSWER"), Ct)).Child;
        await h.WaitForAnswerAsync((await h.Conversations.AskAsync(unticked, "PLANTEDBRANCHQUESTION?", Ct)).Answer!.Value);
        await UntilTitledAsync(h, unticked);

        h.Client.Knowledge.Replies.Enqueue("Title: A long summary title\nShort name: Short name\n\nPLANTEDSUMMARY body");
        await SummarizeAsync(h, root.Conversation);

        var shares = Shares(h);
        var source = await shares.ReadAsync(h.Workspace, Ct);
        var selection = ShareSelection.Exploration(source.Parents, root.Conversation).Untick(unticked);
        await shares.SaveAsync(ExchangeDocument.Structure("How trains came to be", source, selection, Now, "0.7.0"), Local(Target), Ct);

        var file = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Target, Ct));

        // The file is the share: what may leave is in it.
        Assert.Contains("How trains came to be", file, StringComparison.Ordinal);
        Assert.Contains("Short name", file, StringComparison.Ordinal);
        Assert.Contains(root.Conversation.ToString(), file, StringComparison.Ordinal);
        Assert.Contains(untitled.ToString(), file, StringComparison.Ordinal);

        // What may not leave is not.
        Assert.DoesNotContain("PLANTED", file, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("A long summary title", file, StringComparison.Ordinal);
        foreach (var secret in new[] { unticked.ToString(), h.Workspace.ToString(), root.Answer.Id.ToString(), "fake-model", "fake.example", "sk-test" })
        {
            Assert.DoesNotContain(secret, file, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_conversation_without_a_title_has_no_name_neither_its_quote_nor_its_first_question()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Titles.FailNext(GenerationErrorKind.Unauthorized, 10);
        var root = await StartAsync(h, "A question without a title?");
        var branch = (await h.Conversations.CreateBranchAsync(root.Conversation, BranchFlowTests.Selection(root.Answer, root.Answer.Text[..4]), Ct)).Child;

        var source = await Shares(h).ReadAsync(h.Workspace, Ct);

        Assert.All(source.Conversations, c => Assert.Null(c.Name));
        Assert.Equal(new HashSet<ConversationId> { root.Conversation, branch }, source.Conversations.Select(c => c.Id).ToHashSet());
    }

    [Fact]
    public async Task A_summarized_conversation_carries_the_name_the_Mindweave_page_shows()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await StartAsync(h, "How did trains begin?");
        await UntilTitledAsync(h, root.Conversation);
        h.Client.Knowledge.Replies.Enqueue("Title: The long history of trains\nShort name: Trains\n\nBody");
        await SummarizeAsync(h, root.Conversation);

        var shown = Assert.Single((await h.SummaryNetwork.ProjectAsync(h.Workspace, Ct)).Nodes);
        var offered = Assert.Single((await Shares(h).ReadAsync(h.Workspace, Ct)).Conversations);

        Assert.Equal(shown.ShortTitle, offered.Name);
        Assert.Equal("Trains", offered.Name);
        Assert.True(offered.HasSummary);
    }

    [Fact]
    public async Task A_link_between_two_summaries_is_offered_between_their_conversations()
    {
        await using var h = await ChatHarness.StartAsync();
        var a = await StartAsync(h, "One?");
        var b = await StartAsync(h, "Two?");
        var first = await SummarizeAsync(h, a.Conversation);
        var second = await SummarizeAsync(h, b.Conversation);
        await h.Links.CreateAsync(first, second, "NOTE", Ct);

        var link = Assert.Single((await Shares(h).ReadAsync(h.Workspace, Ct)).Links);

        Assert.Equal(new HashSet<ConversationId> { a.Conversation, b.Conversation }, new HashSet<ConversationId> { link.A, link.B });
    }

    [Fact]
    public async Task An_archived_conversation_is_offered_like_the_others()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await StartAsync(h, "One?");
        await h.SettleAsync(root.Conversation);
        await h.Conversations.ArchiveAsync(root.Conversation, Ct);

        Assert.Equal(root.Conversation, Assert.Single((await Shares(h).ReadAsync(h.Workspace, Ct)).Conversations).Id);
    }

    /// <summary>§9, acceptance 6: the share goes up to the last finished answer.</summary>
    [Fact]
    public async Task A_question_whose_answer_is_still_being_generated_is_not_counted_and_the_conversation_says_so()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await StartAsync(h, "One?");
        var held = h.Client.Hold("so far");
        var asked = await h.Conversations.AskAsync(root.Conversation, "Two?", Ct);
        var shares = Shares(h);

        var during = Assert.Single((await shares.ReadAsync(h.Workspace, Ct)).Conversations);
        Assert.True(during.IsAnswering);
        Assert.Equal(1, during.Questions);

        held.Release.TrySetResult();
        await h.WaitForAnswerAsync(asked.Answer!.Value);
        var after = Assert.Single((await shares.ReadAsync(h.Workspace, Ct)).Conversations);
        Assert.False(after.IsAnswering);
        Assert.Equal(2, after.Questions);
    }

    /// <summary>Trying a finished answer again: the earlier attempt stands, so its question counts.</summary>
    [Fact]
    public async Task A_question_answered_once_and_being_answered_again_is_counted()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await StartAsync(h, "One?");
        var held = h.Client.Hold("again");
        var retry = await h.Conversations.RetryAsync(root.Conversation, root.Answer.Id, Ct);

        var during = Assert.Single((await Shares(h).ReadAsync(h.Workspace, Ct)).Conversations);

        Assert.True(during.IsAnswering);
        Assert.Equal(1, during.Questions);
        held.Release.TrySetResult();
        await h.WaitForAnswerAsync(retry.Answer!.Value);
    }

    [Fact]
    public async Task A_share_in_progress_is_counted_until_its_file_is_written_and_a_failed_one_is_not()
    {
        await using var h = await ChatHarness.StartAsync();
        var gate = new TaskCompletionSource();
        var writer = new GatedWriter(gate.Task);
        var shares = Shares(h, writer);
        var document = new ExchangeDocument(ExchangeDocument.StructureKind, "t", Now, "1", [], []);

        var running = shares.SaveAsync(document, Local(Target), Ct);
        await writer.Entered.Task;
        var idle = shares.WhenIdleAsync();
        Assert.False(idle.IsCompleted);
        gate.SetResult();
        await running;
        await idle;

        await Assert.ThrowsAsync<ExportWriteException>(() => Shares(h).SaveAsync(document, Local(Path.Combine(_dir, "none", "x.html")), Ct));
        Assert.True(shares.WhenIdleAsync().IsCompleted);
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

    private static ShareService Shares(ChatHarness h, IExportFileWriter? writer = null) =>
        new(h.Store, h.SummaryNodes, new SqliteSummaryNetworkStore(h.Database), new SqliteSummaryLinkStore(h.Database), new SharePageWriter(), writer ?? new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance), NullLogger<ShareService>.Instance);

    private static WorkspaceName MarkedName() => WorkspaceName.TryCreate("PLANTEDWORKSPACE", out var name, out _) ? name! : throw new InvalidOperationException();

    private static ExportDestination Local(string path) => new(Path.GetFileName(path), path, null);

    private static async Task<(ConversationId Conversation, Message Answer)> StartAsync(ChatHarness h, string question)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, question, Ct);
        return (sent.Conversation, await h.WaitForAnswerAsync(sent.Answer!.Value));
    }

    private static Task UntilTitledAsync(ChatHarness h, ConversationId id) =>
        ChatHarness.UntilAsync(async () => (await h.Store.GetAsync(id, Ct))?.Title is not null);

    private static async Task<SummaryNodeId> SummarizeAsync(ChatHarness h, ConversationId id)
    {
        Assert.True((await h.SummaryService.SummarizeAsync(id, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(id, Ct) is { State: GenerationTaskState.Completed });
        return (await h.SummaryService.ForConversationAsync(id, Ct))!.Node.Id;
    }

    private sealed class GatedWriter(Task gate) : IExportFileWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ExportWriteMode> WriteAsync(ExportDestination destination, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await gate;
            return ExportWriteMode.Atomic;
        }
    }
}
