using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;

namespace Mindweave.Core.Tests;

public class BranchRulesTests
{
    private static readonly ConversationId Root = ConversationId.New();
    private static readonly ConversationId Child = ConversationId.New();
    private static readonly ConversationId Grandchild = ConversationId.New();

    [Theory]
    [InlineData(MessageState.Complete, "text", null)]
    [InlineData(MessageState.Stopped, "part", null)]
    [InlineData(MessageState.Failed, "part", null)]
    [InlineData(MessageState.Interrupted, "part", null)]
    [InlineData(MessageState.Streaming, "growing", BranchRejection.AnswerNotFinished)]
    [InlineData(MessageState.Pending, "", BranchRejection.AnswerNotFinished)]
    [InlineData(MessageState.Cancelled, "", BranchRejection.EmptyAnswer)]
    [InlineData(MessageState.Failed, "  ", BranchRejection.EmptyAnswer)]
    public void Only_finished_answers_with_saved_text_can_be_branched_from(MessageState state, string body, BranchRejection? expected) =>
        Assert.Equal(expected, BranchRules.CheckSource(Answer(Root, 1, body, state)));

    [Fact]
    public void Questions_cannot_be_branched_from() =>
        Assert.Equal(BranchRejection.NotAnAnswer, BranchRules.CheckSource(User(Root, 0, "q")));

    [Fact]
    public void Selections_need_a_quote_within_limits()
    {
        var ok = new BranchSelection(MessageId.New(), 0, "蒸汽机", "{}", 1, "p");
        Assert.Null(BranchRules.CheckSelection(ok));
        Assert.Equal(BranchRejection.EmptyQuote, BranchRules.CheckSelection(ok with { Quote = " \n" }));
        Assert.Equal(BranchRejection.QuoteTooLong, BranchRules.CheckSelection(ok with { Quote = new string('字', BranchRules.MaxQuoteLength + 1) }));
        Assert.Equal(BranchRejection.AnchorTooLarge, BranchRules.CheckSelection(ok with { AnchorJson = new string('x', BranchRules.MaxAnchorLength + 1) }));
    }

    [Fact]
    public void A_child_inherits_the_parents_frozen_ranges_and_adds_the_parent_up_to_the_source()
    {
        var fromRoot = BranchRules.RangesForChild(Root, [], sourceSeq: 3);
        Assert.Equal([new BranchContextRange(Root, 0, 3)], fromRoot);

        var fromChild = BranchRules.RangesForChild(Child, fromRoot, sourceSeq: 5);
        Assert.Equal([new BranchContextRange(Root, 0, 3), new BranchContextRange(Child, 1, 5)], fromChild);
    }

    [Fact]
    public void Context_is_the_ancestor_chain_up_to_each_source_then_the_conversation_itself()
    {
        // Root: q0 a1 (source of Child) — later messages are outside the frozen range and never passed in.
        var root = new ContextSegment(Root, null, [User(Root, 0, "火车怎么来的"), Answer(Root, 1, "蒸汽机推动了火车", MessageState.Complete)]);
        // Child: q0 a1 (source of Grandchild).
        var child = new ContextSegment(Child, "蒸汽机", [User(Child, 0, "谁发明的"), Answer(Child, 1, "瓦特改良了它", MessageState.Complete)]);
        var own = new ContextSegment(Grandchild, "瓦特", [User(Grandchild, 0, "他还做了什么")]);

        var items = BranchContext.Assemble([root, child], own);

        Assert.Equal(
        [
            "火车怎么来的",
            "蒸汽机推动了火车",
            "关于上文中的这段内容：\n> 蒸汽机\n\n谁发明的",
            "瓦特改良了它",
            "关于上文中的这段内容：\n> 瓦特\n\n他还做了什么",
        ], items.Select(i => i.Text));
        // What is stored never changes; only what is sent carries the quote.
        Assert.Equal("他还做了什么", items[^1].Message.Text);
    }

    [Fact]
    public void A_partial_source_answer_is_kept_but_other_failed_attempts_are_not()
    {
        var root = new ContextSegment(Root, null,
        [
            User(Root, 0, "q0"),
            Answer(Root, 1, "broken", MessageState.Failed),
            User(Root, 2, "q2"),
            Answer(Root, 3, "半截回答", MessageState.Interrupted),
        ]);
        var own = new ContextSegment(Child, "半截", []);

        Assert.Equal(["q0", "q2", "半截回答"], BranchContext.Assemble([root], own).Select(i => i.Text));
    }

    [Fact]
    public void A_branch_from_a_superseded_attempt_uses_that_attempt_not_the_retry()
    {
        var question = User(Root, 0, "q");
        var first = Answer(Root, 1, "第一次", MessageState.Complete);
        // The range ends at the source (seq 1); the retry at seq 2 was never passed in.
        var items = BranchContext.Assemble([new ContextSegment(Root, null, [question, first])], new ContextSegment(Child, "第一", []));

        Assert.Equal(["q", "第一次"], items.Select(i => i.Text));
    }

    [Fact]
    public void A_multi_line_quote_is_quoted_line_by_line() =>
        Assert.Equal("关于上文中的这段内容：\n> 第一行\n> 第二行\n\n问", BranchContext.QuotedQuestion("第一行\r\n第二行", "问"));

    private static Message User(ConversationId conversation, int seq, string body) =>
        new(MessageId.New(), conversation, seq, MessageRole.User, ContentBlocks.FromText(body), MessageState.Complete, 0, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static Message Answer(ConversationId conversation, int seq, string body, MessageState state) =>
        new(MessageId.New(), conversation, seq, MessageRole.Assistant, ContentBlocks.FromText(body), state, 0, null, null, null, DateTimeOffset.UnixEpoch, null);
}
