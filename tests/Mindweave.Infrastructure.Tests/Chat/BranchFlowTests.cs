using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

public class BranchFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_branch_continues_from_the_quoted_passage_without_repeating_the_background()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, source) = await h.RootWithAnswerAsync("火车是怎么发明的？");

        var branch = await h.Conversations.CreateBranchAsync(root, Selection(source, "火车"), Ct);
        Assert.True(branch.Accepted);
        var child = await h.Conversations.LoadAsync(branch.Child, Ct);
        Assert.Empty(child!.Messages);
        Assert.Equal(h.Workspace, child.Conversation.WorkspaceId);

        var asked = await h.Conversations.AskAsync(branch.Child, "谁造了第一台？", Ct);
        await h.WaitForAnswerAsync(asked.Answer!.Value);

        var request = LastAnswerRequest(h);
        Assert.Equal(
            ["火车是怎么发明的？", "答：火车是怎么发明的？", "关于上文中的这段内容：\n> 火车\n\n谁造了第一台？"],
            request.Turns.Select(t => t.Content));
        // The stored question is exactly what the user typed.
        var stored = await h.Conversations.LoadAsync(branch.Child, Ct);
        Assert.Equal("谁造了第一台？", stored!.Messages[0].Text);
    }

    [Fact]
    public async Task A_second_level_branch_sees_each_ancestor_only_up_to_its_source()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, rootAnswer) = await h.RootWithAnswerAsync("一");
        var child = (await h.Conversations.CreateBranchAsync(root, Selection(rootAnswer, "一"), Ct)).Child;
        var childAnswer = await h.AskAsync(child, "二");
        var grandchild = (await h.Conversations.CreateBranchAsync(child, Selection(childAnswer, "二"), Ct)).Child;

        // Later growth of the ancestors must not leak into the grandchild.
        await h.AskAsync(root, "根后来的问题");
        await h.AskAsync(child, "子后来的问题");
        await h.AskAsync(grandchild, "三");

        Assert.Equal(
        [
            "一",
            "答：一",
            "关于上文中的这段内容：\n> 一\n\n二",
            "答：关于上文中的这段内容：\n> 一\n\n二",
            "关于上文中的这段内容：\n> 二\n\n三",
        ], LastAnswerRequest(h).Turns.Select(t => t.Content));

        var lineage = await h.Conversations.GetLineageAsync(grandchild, Ct);
        Assert.Equal([root, child, grandchild], lineage.Select(e => e.Conversation.Id));
        Assert.Null(lineage[0].Source);
        Assert.Equal("二", lineage[2].Source!.Quote);
    }

    [Fact]
    public async Task Regenerating_the_source_later_does_not_change_an_existing_branch()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, source) = await h.RootWithAnswerAsync("问");
        var child = (await h.Conversations.CreateBranchAsync(root, Selection(source, "问"), Ct)).Child;

        h.Client.Reply("新的回答");
        var retry = await h.Conversations.RetryAsync(root, source.Id, Ct);
        await h.WaitForAnswerAsync(retry.Answer!.Value);
        await h.AskAsync(child, "追问");

        Assert.Equal(["问", "答：问", "关于上文中的这段内容：\n> 问\n\n追问"], LastAnswerRequest(h).Turns.Select(t => t.Content));

        // The superseded attempt can still be branched from (§6.2).
        Assert.True((await h.Conversations.CreateBranchAsync(root, Selection(source, "问"), Ct)).Accepted);
    }

    [Fact]
    public async Task Invalid_sources_are_refused_and_leave_nothing_behind()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("问");
        var (other, _) = await h.RootWithAnswerAsync("别的");
        var question = (await h.Conversations.LoadAsync(root, Ct))!.Messages[0];
        var held = h.Client.Hold("半句");
        var running = await h.Conversations.AskAsync(root, "再问", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(BranchRejection.SourceNotFound, (await h.Conversations.CreateBranchAsync(other, Selection(answer, "问"), Ct)).Rejection);
        Assert.Equal(BranchRejection.NotAnAnswer, (await h.Conversations.CreateBranchAsync(root, Selection(question, "问"), Ct)).Rejection);
        var streaming = (await h.GetMessageAsync(running.Answer!.Value))!;
        Assert.Equal(BranchRejection.AnswerNotFinished, (await h.Conversations.CreateBranchAsync(root, Selection(streaming, "半句"), Ct)).Rejection);
        Assert.Equal(BranchRejection.EmptyQuote, (await h.Conversations.CreateBranchAsync(root, Selection(answer, " "), Ct)).Rejection);
        Assert.Equal(BranchRejection.EmptyAnswer, (await h.Conversations.CreateBranchAsync(root, Selection(answer, "问") with { SourceBlock = 3 }, Ct)).Rejection);
        Assert.Equal(BranchRejection.SourceNotFound, (await h.Conversations.CreateBranchAsync(ConversationId.New(), Selection(answer, "问"), Ct)).Rejection);

        held.Finish();
        await h.WaitForAnswerAsync(running.Answer!.Value);
        Assert.Equal(2, (await h.Conversations.ListAsync(h.Workspace, Ct)).Count);
        Assert.Equal(0, await h.CountAsync("branch_source") + await h.CountAsync("branch_context_range"));
    }

    [Fact]
    public async Task The_sidebar_and_the_parent_know_their_branches()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("问");
        var first = (await h.Conversations.CreateBranchAsync(root, Selection(answer, "甲"), Ct)).Child;
        var second = (await h.Conversations.CreateBranchAsync(root, Selection(answer, "乙"), Ct)).Child;

        var children = await h.Conversations.ListBranchesAsync(root, Ct);
        Assert.Equal([first, second], children.Select(c => c.Child));
        Assert.All(children, c => Assert.Equal(answer.Id, c.SourceMessage));
        Assert.Equal("{\"v\":1}", children[0].AnchorJson);

        var summaries = await h.Conversations.ListAsync(h.Workspace, Ct);
        var branch = summaries.Single(s => s.Conversation.Id == first);
        Assert.Equal(root, branch.Parent);
        Assert.Equal("甲", branch.Quote);
        Assert.Null(summaries.Single(s => s.Conversation.Id == root).Parent);
    }

    [Fact]
    public async Task A_branch_title_request_includes_the_quote()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("问");
        var child = (await h.Conversations.CreateBranchAsync(root, Selection(answer, "蒸汽机"), Ct)).Child;
        await h.AskAsync(child, "谁发明的");

        await ChatHarness.UntilAsync(() => h.Client.Requests.Count(r => r.Turns[0].Role == ChatRole.System) == 2);
        // The root's and the branch's title requests run in the background in either order: take the branch's by its question.
        var title = Assert.Single(h.Client.Requests, r => r.Turns[0].Role == ChatRole.System && r.Turns[1].Content.Contains("谁发明的", StringComparison.Ordinal));
        Assert.Contains("> 蒸汽机", title.Turns[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Branches_survive_a_restart()
    {
        using var dir = new TempDataDirectory();
        ConversationId root;
        ConversationId child;
        await using (var h = await ChatHarness.StartAsync(directory: dir))
        {
            var (r, answer) = await h.RootWithAnswerAsync("问");
            root = r;
            child = (await h.Conversations.CreateBranchAsync(root, Selection(answer, "问"), Ct)).Child;
        }

        await using var reopened = await ChatHarness.StartAsync(directory: dir);
        Assert.Equal(root, (await reopened.Conversations.GetSourceAsync(child, Ct))!.Parent);
        await reopened.AskAsync(child, "接着");
        Assert.Equal(3, LastAnswerRequest(reopened).Turns.Count);
    }

    internal static BranchSelection Selection(Message source, string quote) =>
        new(source.Id, 0, quote, "{\"v\":1}", 1, "test-profile");

    private static ChatRequest LastAnswerRequest(ChatHarness h) => h.Client.Requests.Last(r => r.Turns[0].Role != ChatRole.System);
}
