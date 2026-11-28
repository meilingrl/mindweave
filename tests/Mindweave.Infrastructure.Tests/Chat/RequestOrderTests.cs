using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Prompt caching, step 2 (requirements §10.11.4, architecture §15.5 "Order of a request"): a request starts with what is the
/// same for every conversation, then the ancestors and the conversation so far; what belongs to this request only comes last,
/// with the question, marked as the app's own lines. So sibling branches, and a branch and its parent, begin with the same
/// turns, and a provider's cache can serve them.
/// </summary>
public class RequestOrderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_requests_of_two_sibling_branches_are_identical_up_to_the_branch_point()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("火车是怎么发明的？");
        var first = await h.BranchAsync(root, answer, "火车");
        await h.AskAsync(first, "谁造了第一台？");
        var second = await h.BranchAsync(root, answer, "发明");
        await h.AskAsync(second, "在哪一年？");
        await h.AskAsync(root, "后来呢？");

        var requests = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.Equal(4, requests.Count);
        var (a, b, parent) = (requests[1].Turns, requests[2].Turns, requests[3].Turns);

        // The system turn, the root's question and its answer; then each branch's own first question, which differ.
        Assert.Equal(4, a.Count);
        Assert.Equal(a.Take(3), b.Take(3));
        Assert.Equal(new ChatTurn(ChatRole.System, AnswerPrompt.Fixed), a[0]);
        Assert.NotEqual(a[3], b[3]);
        Assert.StartsWith(RequestNote.Of(AnswerPrompt.BranchPart(true)), a[3].Content, StringComparison.Ordinal);

        // The parent's next question begins as its branches do.
        Assert.Equal(a.Take(3), parent.Take(3));
        Assert.Equal("后来呢？", parent[^1].Content);
    }

    [Fact]
    public async Task Two_continuing_branches_and_their_parent_begin_with_the_same_turns()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _) = await h.RootWithAnswerAsync("火车是怎么发明的？");
        var first = (await h.Conversations.ContinueInBranchAsync(root, Ct)).Child;
        await h.AskAsync(first, "谁造了第一台？");
        var second = (await h.Conversations.ContinueInBranchAsync(root, Ct)).Child;
        await h.AskAsync(second, "在哪一年？");
        await h.AskAsync(root, "后来呢？");

        var requests = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.Equal(4, requests.Count);
        var (a, b, parent) = (requests[1].Turns, requests[2].Turns, requests[3].Turns);

        // The system turn, the root's question and its answer; then each branch's own question. With no passage selected there
        // is no branch part, so the question goes alone.
        Assert.Equal(4, a.Count);
        Assert.Equal(a.Take(3), b.Take(3));
        Assert.Equal(new ChatTurn(ChatRole.System, AnswerPrompt.Fixed), a[0]);
        Assert.Equal(new ChatTurn(ChatRole.User, "谁造了第一台？"), a[3]);
        Assert.Equal(new ChatTurn(ChatRole.User, "在哪一年？"), b[3]);
        Assert.Equal(a.Take(3), parent.Take(3));
    }

    [Fact]
    public async Task A_later_question_begins_with_what_the_earlier_one_sent_up_to_its_question()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("火车是怎么发明的？");
        var child = await h.BranchAsync(root, answer, "火车");
        await h.AskAsync(child, "谁造了第一台？");
        await h.AskAsync(child, "后来呢？");

        var requests = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        var (earlier, later) = (requests[1].Turns, requests[2].Turns);

        // The earlier question is sent again as it is stored; only its note, which went with it as the last turn, is not.
        Assert.Equal(earlier.SkipLast(1), later.Take(earlier.Count - 1));
        Assert.Equal(RequestNote.Without(earlier[^1].Content), later[earlier.Count - 1].Content);
        Assert.StartsWith(RequestNote.Open, later[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_goes_before_the_question_or_as_a_turn_of_its_own_and_can_be_taken_off_again()
    {
        Assert.Equal(string.Empty, RequestNote.Of(null, " ", string.Empty));
        var note = RequestNote.Of("甲", null, "乙\n\n丙");
        Assert.Equal("<app-notes>\n甲\n\n乙\n\n丙\n</app-notes>", note);

        var question = new ChatTurn(ChatRole.User, "问题");
        var noted = RequestNote.Append([new ChatTurn(ChatRole.System, "说明"), question], note);
        Assert.Equal(2, noted.Count);
        Assert.Equal(note + "\n\n问题", noted[^1].Content);
        Assert.Equal("问题", RequestNote.Without(noted[^1].Content));

        var afterTool = RequestNote.Append([question, new ChatTurn(ChatRole.Tool, "结果") { ToolCallId = "c1" }], note);
        Assert.Equal(new ChatTurn(ChatRole.User, note), afterTool[^1]);
        Assert.Equal(string.Empty, RequestNote.Without(afterTool[^1].Content));

        // Unchanged without a note, and a text that only looks like one stays the user's.
        IReadOnlyList<ChatTurn> plain = [question];
        Assert.Same(plain, RequestNote.Append(plain, string.Empty));
        Assert.Equal("<app-notes> 用户自己写的", RequestNote.Without("<app-notes> 用户自己写的"));
    }
}
