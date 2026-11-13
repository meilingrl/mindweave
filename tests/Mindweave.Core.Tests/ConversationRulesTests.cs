using Mindweave.Core.Conversations;

namespace Mindweave.Core.Tests;

public class ConversationRulesTests
{
    private static readonly ConversationId Conversation = ConversationId.New();

    [Fact]
    public void Context_takes_each_question_and_only_its_latest_usable_attempt()
    {
        var q1 = User(0, "q1");
        var a1 = Answer(1, "a1", MessageState.Complete);
        var q2 = User(2, "q2");
        var a2Failed = Answer(3, "half", MessageState.Failed);
        var a2Retry = Answer(4, "a2", MessageState.Complete, supersedes: a2Failed.Id);
        var q3 = User(5, "q3");
        var a3Stopped = Answer(6, "partial", MessageState.Stopped);
        var q4 = User(7, "q4");
        var a4Empty = Answer(8, string.Empty, MessageState.Cancelled);

        var context = ConversationRules.SelectContext([q1, a1, q2, a2Failed, a2Retry, q3, a3Stopped, q4, a4Empty]);

        Assert.Equal(["q1", "a1", "q2", "a2", "q3", "partial", "q4"], context.Select(m => m.Text));
    }

    [Fact]
    public void A_failed_latest_attempt_is_not_replaced_by_an_older_successful_one()
    {
        var q = User(0, "q");
        var ok = Answer(1, "old", MessageState.Complete);
        var failed = Answer(2, "bad", MessageState.Failed, supersedes: ok.Id);

        Assert.Equal(["q"], ConversationRules.SelectContext([q, ok, failed]).Select(m => m.Text));
    }

    [Fact]
    public void Only_the_last_finished_answer_can_be_retried()
    {
        var q = User(0, "q");
        var a = Answer(1, "a", MessageState.Complete);
        var q2 = User(2, "q2");
        var running = Answer(3, "…", MessageState.Streaming);

        Assert.True(ConversationRules.CanRetry([q, a], a.Id));
        Assert.False(ConversationRules.CanRetry([q, a, q2, running], a.Id));
        Assert.False(ConversationRules.CanRetry([q, a, q2, running], running.Id));
        Assert.True(ConversationRules.HasUnfinishedAnswer([q, a, q2, running]));
    }

    [Theory]
    [InlineData("“蒸汽机的发明”。", "蒸汽机的发明")]
    [InlineData("标题：火车历史\n解释……", "火车历史")]
    [InlineData("**Steam engines**", "Steam engines")]
    [InlineData("题目分析的方法", "题目分析的方法")]
    [InlineData("Title: \"Steam\".", "Steam")]
    [InlineData("   ", null)]
    [InlineData("# 火车历史", "火车历史")]
    [InlineData("**“蒸汽机”**。", "蒸汽机")]
    public void Generated_titles_are_cleaned(string raw, string? expected) =>
        Assert.Equal(expected, ConversationRules.CleanGeneratedTitle(raw));

    /// <summary>
    /// Only a pair enclosing the whole title is a wrapper; marks inside the title stay (2026-11-10: “死寂》 电影剧情概括” lost
    /// its 《 because each end was trimmed on its own).
    /// </summary>
    [Theory]
    [InlineData("《死寂》电影剧情概括", "《死寂》电影剧情概括")]
    [InlineData("《死寂》", "死寂")]
    [InlineData("“《死寂》剧情概括”", "《死寂》剧情概括")]
    [InlineData("**《三体》读后感**。", "《三体》读后感")]
    [InlineData("「蒸汽机」与铁路", "「蒸汽机」与铁路")]
    [InlineData("“火箭号”与“机车”", "“火箭号”与“机车”")]
    [InlineData("\"Steam\" engines", "\"Steam\" engines")]
    [InlineData("铁路与《物种起源》", "铁路与《物种起源》")]
    public void Marks_that_belong_to_the_title_are_kept(string raw, string expected) =>
        Assert.Equal(expected, ConversationRules.CleanGeneratedTitle(raw));

    [Fact]
    public void Fallback_titles_are_one_line_and_bounded()
    {
        var title = ConversationRules.FallbackTitle("火车\n是怎么   发明的？" + new string('长', 50));

        Assert.StartsWith("火车 是怎么 发明的？", title, StringComparison.Ordinal);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
        Assert.Equal(ConversationRules.FallbackTitleLength + 1, title.Length);
    }

    private static Message User(int seq, string body) =>
        new(MessageId.New(), Conversation, seq, MessageRole.User, ContentBlocks.FromText(body), MessageState.Complete, 0, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static Message Answer(int seq, string body, MessageState state, MessageId? supersedes = null) =>
        new(MessageId.New(), Conversation, seq, MessageRole.Assistant, ContentBlocks.FromText(body), state, 0, supersedes, null, null, DateTimeOffset.UnixEpoch, null);

    /// <summary>Health check 2026-11-12: reasoning some models put inside the text is not part of what the program uses.</summary>
    [Theory]
    [InlineData("<think>\nThe user asks about trains.\n</think>\n蒸汽机的发明", "蒸汽机的发明")]
    [InlineData("<THINKING>想一想</THINKING>标题", "标题")]
    [InlineData("<think>只有推理，没有结束", "")]
    [InlineData("没有推理的正文", "没有推理的正文")]
    [InlineData("正文里提到 <think> 这个词", "正文里提到 <think> 这个词")]
    public void Inline_reasoning_is_removed(string raw, string expected) =>
        Assert.Equal(expected, ConversationRules.WithoutInlineReasoning(raw));

    /// <summary>Low finding gap-unprompted-9: invisible control and direction characters never reach a title.</summary>
    [Fact]
    public void Shortening_drops_invisible_control_characters_but_keeps_emoji_joiners()
    {
        Assert.Equal("蒸汽机车", ConversationRules.Shorten("蒸\u202E汽\u200B机\u0007车", 20));
        Assert.Equal("家庭👨\u200D👩\u200D👧", ConversationRules.Shorten("家庭👨\u200D👩\u200D👧", 20));
        Assert.Equal("می\u200Cخواهم", ConversationRules.Shorten("می\u200Cخواهم", 20));
    }

    /// <summary>Low finding gap-summary-10: a cut never ends on half a character.</summary>
    [Fact]
    public void Cutting_keeps_whole_characters()
    {
        Assert.Equal("ab😀", ConversationRules.FirstTextElements("ab😀😀", 3));
        Assert.Equal("ab", ConversationRules.FirstTextElements("ab", 3));
    }
}
