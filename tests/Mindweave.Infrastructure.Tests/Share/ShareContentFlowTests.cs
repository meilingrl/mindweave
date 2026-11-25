using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Export;
using Mindweave.Application.Models;
using Mindweave.Application.Share;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Export;
using Mindweave.Infrastructure.Share;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Share;

/// <summary>A share with content through the real stores, the run loop and the file (docs/design/sharing.md S6, S7; acceptance 2 to 4 and 6).</summary>
public sealed class ShareContentFlowTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly ModelSettingsInput ToolModel = new(
        "PLANTEDCONNECTION", "https://planted-service.example/v1", "PLANTEDMODEL", "sk-planted",
        Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-share-content", Guid.NewGuid().ToString("N"));

    public ShareContentFlowTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Target => Path.Combine(_dir, "share.html");

    /// <summary>Acceptance 4: a marker planted in every place that must not leave is nowhere in the file's bytes.</summary>
    [Fact]
    public async Task The_file_holds_what_was_said_and_nothing_that_stays_home()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        await h.WorkspaceService.RenameAsync(h.Workspace, WorkspaceName.TryCreate("PLANTEDWORKSPACE", out var name, out _) ? name! : throw new InvalidOperationException(), Ct);
        await h.WebSearch.SetKeyAsync("tvly-planted-key-1234567890", Ct);
        var server = await h.Mcp.AddAsync(FakeMcp.Command("PLANTEDSERVER"), Ct);
        var tool = (await h.McpServers.StartAsync(server.Id, Ct)).Single(t => t.Name == "echo");
        var echo = await h.Mcp.EnableToolAsync(server.Id, tool, Ct);

        // A question with a file, answered with a web search, a page read and a third party's tool.
        h.Search.Returns(
            new SearchHit("Cited page", new Uri("https://example.org/cited"), "PLANTEDSNIPPET of the cited page"),
            new SearchHit("PLANTEDUNCITEDTITLE", new Uri("https://example.org/uncited"), "PLANTEDSNIPPET two"));
        h.Pages.Page("https://example.org/cited", "Cited page", "PLANTEDPAGETEXT of the page");
        h.Client.CallTool(WebSearchTool.ToolName, JsonSerializer.Serialize(new { query = "PLANTEDQUERY" }));
        h.Client.CallTool(FetchPageTool.ToolName, JsonSerializer.Serialize(new { url = "https://example.org/cited" }));
        h.Client.CallTool(SearchAttachmentsTool.ToolName, JsonSerializer.Serialize(new { query = "PLANTEDATTACHMENT" }));
        h.Client.CallTool(echo.ModelName, JsonSerializer.Serialize(new { text = "PLANTEDARGUMENT" }));
        h.Client.Reply("The standing answer, from the web[1].");
        var path = Path.Combine(_dir, "gauge-table.txt");
        await File.WriteAllTextAsync(path, "PLANTEDATTACHMENT text of the file", Ct);
        var file = (await h.ResourceService.AttachFileAsync(DraftSlot.NewExploration(h.Workspace), path, Ct)).Resource!;
        h.Client.Titles.Replies.Enqueue("Root title");
        var sent = await h.Conversations.StartAsync(h.Workspace, "The question asked?", [file.Resource.Id], null, new SendOptions(WebSearchMode.Auto), Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        var root = sent.Conversation;
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value, timeoutMs: 20_000);
        await UntilTitledAsync(h, root, "Root title");
        Assert.Equal(4, answer.Blocks.OfType<ToolCallBlock>().Count());
        var cited = Assert.Single(ContentBlocks.CitationsOf(answer.Blocks));
        Assert.Equal("https://example.org/cited", cited.Url!.AbsoluteUri);

        // An answer no branch left from, tried again.
        var superseded = await AskAsync(h, root, "A second question?", reply: "PLANTEDSUPERSEDED answer");
        h.Client.Reply("The second answer that stands.");
        var retried = await h.Conversations.RetryAsync(root, superseded.Id, Ct);
        await h.WaitForAnswerAsync(retried.Answer!.Value);

        // A shared branch, and an unticked one.
        var shared = (await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(answer, "standing answer"), Ct)).Child;
        h.Client.Titles.Replies.Enqueue("Shared title");
        await AskAsync(h, shared, "The branch question?", reply: "The branch answer.");
        await UntilTitledAsync(h, shared, "Shared title");
        h.Client.Titles.Replies.Enqueue("PLANTEDUNTICKED title");
        var unticked = (await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(answer, "from the web"), Ct)).Child;
        await AskAsync(h, unticked, "PLANTEDUNTICKED question?", reply: "PLANTEDUNTICKED answer");
        await UntilTitledAsync(h, unticked, "PLANTEDUNTICKED title");

        // A summary with a history version, a current one and a candidate.
        await SummarizeAsync(h, root, "Title: PLANTEDOLD title\nShort name: PLANTEDOLDNAME\n\nPLANTEDOLD body");
        var node = (await h.SummaryService.ForConversationAsync(root, Ct))!.Node;
        await SummarizeAsync(h, root, "Title: The current title\nShort name: Trains\n\nThe current body");
        await h.SummaryService.AdoptAsync(node.Id, (await h.SummaryService.ForConversationAsync(root, Ct))!.Node.Pending!.Value, Ct);
        await SummarizeAsync(h, root, "Title: PLANTEDCANDIDATE title\nShort name: PLANTEDCANDIDATENAME\n\nPLANTEDCANDIDATE body");
        Assert.NotNull((await h.SummaryService.ForConversationAsync(root, Ct))!.Node.Pending);

        var source = await Shares(h).ReadAsync(h.Workspace, Ct);
        var selection = ShareSelection.Exploration(source.Parents, root).Untick(unticked);
        var document = await Contents(h).BuildAsync("How trains came to be", h.Workspace, source, selection, Now, "0.7.0", Ct);
        await Shares(h).SaveAsync(document, new ExportDestination("share.html", Target, null), Ct);
        var html = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Target, Ct));

        // What may leave is in it.
        foreach (var kept in new[] { "The question asked?", "The standing answer, from the web[1].", "The second answer that stands.", "standing answer", "The branch question?", "The branch answer.", "The current title", "The current body", "gauge-table.txt", "Cited page", "https://example.org/cited" })
        {
            Assert.Contains(kept, html, StringComparison.Ordinal);
        }

        // What may not leave is not.
        Assert.DoesNotContain("PLANTED", html, StringComparison.OrdinalIgnoreCase);
        foreach (var secret in new[] { unticked.ToString(), superseded.Id.ToString(), h.Workspace.ToString(), file.Resource.Id.ToString(), cited.Resource.ToString(), server.Id, echo.ModelName, "example.org/uncited" })
        {
            Assert.DoesNotContain(secret, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>§6.6, point 1: with content a conversation without a title goes by the name the app shows.</summary>
    [Fact]
    public async Task A_conversation_without_a_title_goes_by_its_quote_or_first_question_only_with_content()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Titles.FailNext(GenerationErrorKind.Unauthorized, 10);
        var sent = await h.Conversations.StartAsync(h.Workspace, "A question without a title?", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        var branch = (await h.Conversations.CreateBranchAsync(sent.Conversation, BranchFlowTests.Selection(answer, answer.Text[..2]), Ct)).Child;

        var source = await Shares(h).ReadAsync(h.Workspace, Ct);
        var document = await Contents(h).BuildAsync("T", h.Workspace, source, ShareSelection.Exploration(source.Parents, branch), Now, "1", Ct);

        Assert.All(source.Conversations, c => Assert.Null(c.Name));
        Assert.Equal(
            (await h.Store.ListAsync(h.Workspace, Ct)).ToDictionary(c => c.Conversation.Id, c => ConversationNames.Of(c.Conversation.Title, c.Quote, c.FirstQuestion)),
            document.Conversations.ToDictionary(c => c.Id, c => c.Name!));
    }

    [Fact]
    public async Task A_conversation_deleted_after_the_list_was_read_is_not_in_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "One?", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var source = await Shares(h).ReadAsync(h.Workspace, Ct);
        await h.SettleAsync(sent.Conversation);
        await h.DeleteAsync(sent.Conversation);

        Assert.Empty((await Contents(h).BuildAsync("T", h.Workspace, source, ShareSelection.Exploration(source.Parents, sent.Conversation), Now, "1", Ct)).Conversations);
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

    private static ShareService Shares(ChatHarness h) =>
        new(h.Store, h.SummaryNodes, new SqliteSummaryNetworkStore(h.Database), new SqliteSummaryLinkStore(h.Database), new SharePageWriter(new PlainMarkdown()), new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance), NullLogger<ShareService>.Instance);

    internal static ShareContentService Contents(ChatHarness h) =>
        new(h.Store, h.Branches, h.SummaryNodes, new SqliteSummaryLinkStore(h.Database), new PlainMarkdown());

    private static async Task<Message> AskAsync(ChatHarness h, ConversationId id, string question, string reply)
    {
        h.Client.Reply(reply);
        return await h.AskAsync(id, question);
    }

    /// <summary>A title comes in the background: the next one is queued only after it, so each conversation gets its own.</summary>
    private static Task UntilTitledAsync(ChatHarness h, ConversationId id, string title) =>
        ChatHarness.UntilAsync(async () => (await h.Store.GetAsync(id, Ct))?.Title == title);

    private static async Task SummarizeAsync(ChatHarness h, ConversationId id, string reply)
    {
        h.Client.Knowledge.Replies.Enqueue(reply);
        var before = await h.SummaryService.LatestTaskAsync(id, Ct);
        Assert.True((await h.SummaryService.SummarizeAsync(id, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(id, Ct) is { State: GenerationTaskState.Completed } done && done.Id != before?.Id);
    }
}

/// <summary>Stands in for the app's Markdown, which Infrastructure does not have: every text is one paragraph.</summary>
internal sealed class PlainMarkdown : IShareMarkdown
{
    public IReadOnlyList<ShareNode> Draw(string markdown, IReadOnlySet<int> citations, IReadOnlyList<ShareMark> marks) =>
        markdown.Contains('$', StringComparison.Ordinal)
            ? [new ShareNode("p", [ShareNode.OfText(markdown)]), new ShareNode("mathblock", [], markdown.Trim('$'))]
            : [new ShareNode("p", [ShareNode.OfText(markdown)])];

    public int? OffsetOf(BranchSource source) => null;
}
