using Mindweave.Application.Ai;
using Mindweave.Core.Branches;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>新建分支: a branch that continues a conversation from its end, with no passage selected.</summary>
public class ContinueInBranchFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_continuing_branch_takes_the_whole_parent_and_nothing_added_later()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _) = await h.RootWithAnswerAsync("一");
        var last = await h.AskAsync(root, "二");

        var branch = await h.Conversations.ContinueInBranchAsync(root, Ct);
        Assert.True(branch.Accepted);
        var child = (await h.Conversations.LoadAsync(branch.Child, Ct))!;
        Assert.Empty(child.Messages);
        Assert.Equal(h.Workspace, child.Conversation.WorkspaceId);

        var source = (await h.Conversations.GetSourceAsync(branch.Child, Ct))!;
        Assert.Equal(root, source.Parent);
        Assert.Equal(last.Id, source.SourceMessage);
        Assert.Null(source.Quote);
        Assert.True(source.IsContinuation);

        await h.AskAsync(root, "根后来的问题");
        await h.AskAsync(branch.Child, "三");

        // All of the parent up to the branch, with no quote before the first question; the parent's later turn stays out.
        Assert.Equal(["一", "答：一", "二", "答：二", "三"], LastAnswerTurns(h).Select(t => t.Content));
    }

    [Fact]
    public async Task A_continuing_branch_of_a_branch_keeps_the_ancestors_frozen_ranges()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, rootAnswer) = await h.RootWithAnswerAsync("一");
        var child = (await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(rootAnswer, "一"), Ct)).Child;
        await h.AskAsync(child, "二");
        await h.AskAsync(root, "根后来的问题");

        var grandchild = (await h.Conversations.ContinueInBranchAsync(child, Ct)).Child;
        await h.AskAsync(grandchild, "三");

        Assert.Equal(
            ["一", "答：一", "关于上文中的这段内容：\n> 一\n\n二", "答：关于上文中的这段内容：\n> 一\n\n二", "三"],
            LastAnswerTurns(h).Select(t => t.Content));
        var lineage = await h.Conversations.GetLineageAsync(grandchild, Ct);
        Assert.Equal([root, child, grandchild], lineage.Select(e => e.Conversation.Id));
    }

    [Fact]
    public async Task The_parent_lists_it_under_its_last_message()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("问");
        var child = (await h.Conversations.ContinueInBranchAsync(root, Ct)).Child;

        var entry = Assert.Single(await h.Conversations.ListBranchesAsync(root, Ct));
        Assert.Equal(child, entry.Child);
        Assert.Equal(answer.Id, entry.SourceMessage);
        Assert.Equal(root, (await h.Conversations.ListAsync(h.Workspace, Ct)).Single(s => s.Conversation.Id == child).Parent);
    }

    [Fact]
    public async Task It_is_refused_while_an_answer_grows_in_an_archived_or_missing_conversation_and_leaves_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _) = await h.RootWithAnswerAsync("问");
        var held = h.Client.Hold("半句");
        var running = await h.Conversations.AskAsync(root, "再问", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(BranchRejection.AnswerNotFinished, (await h.Conversations.ContinueInBranchAsync(root, Ct)).Rejection);
        Assert.Equal(BranchRejection.SourceNotFound, (await h.Conversations.ContinueInBranchAsync(Core.Conversations.ConversationId.New(), Ct)).Rejection);

        held.Finish();
        await h.WaitForAnswerAsync(running.Answer!.Value);
        var (archived, _) = await h.RootWithAnswerAsync("别的");
        await h.Conversations.ArchiveAsync(archived, Ct);
        Assert.Equal(BranchRejection.ConversationArchived, (await h.Conversations.ContinueInBranchAsync(archived, Ct)).Rejection);

        Assert.Equal(0, await h.CountAsync("branch_source") + await h.CountAsync("branch_context_range"));
        Assert.Equal(2, await h.CountAsync("conversation"));
    }

    /// <summary>The history of the last answer request, after its instructions and without the app's note.</summary>
    private static IReadOnlyList<ChatTurn> LastAnswerTurns(ChatHarness h) => FakeChatClient.Dialogue(h.Client.Requests.Last(FakeChatClient.IsAnswer));
}
