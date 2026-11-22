using Mindweave.Core.Branches;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;

namespace Mindweave.Core.Tests;

public class ContextPlannerTests
{
    private static readonly ConversationId Root = ConversationId.New();
    private static readonly ConversationId Child = ConversationId.New();

    /// <summary>About 1,000 estimated tokens per answer, so four turns close a chunk.</summary>
    private static readonly string Long = new('字', 1000);

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 1)]
    [InlineData("abcd", 2)]
    [InlineData("蒸汽机", 3)]
    [InlineData("😀", 1)]
    [InlineData("ok 蒸汽", 3)]
    public void Tokens_are_estimated_high_rather_than_low(string text, int expected) =>
        Assert.Equal(expected, TokenEstimator.Estimate(text));

    [Fact]
    public void The_budget_leaves_room_for_output_and_a_margin()
    {
        var unknown = ContextBudget.For(Model(contextWindow: null, maxOutput: null));
        Assert.Equal(new ContextBudget(32_000, 4_096, 3_200), unknown);
        Assert.Equal(32_000 - 4_096 - 3_200, unknown.Available);

        var small = ContextBudget.For(Model(contextWindow: 8_000, maxOutput: 1_000));
        Assert.Equal(new ContextBudget(8_000, 1_000, 1_000), small);
    }

    [Fact]
    public void Material_that_fits_is_sent_as_it_is()
    {
        var own = Segment(Root, null, Turns(Root, 3, "短"), "现在的问题");

        var plan = ContextPlanner.Plan([], own, budget: 10_000, _ => null, TestPhrases.Chinese).Plan!;

        Assert.False(plan.IsCompressed);
        Assert.Equal(7, plan.RawItems.Count());
    }

    [Fact]
    public void The_oldest_runs_are_summarized_first_and_recent_turns_stay_raw()
    {
        // Twelve long turns: chunks close every four turns (about 4,000 tokens).
        var own = Segment(Root, null, Turns(Root, 12, Long), "现在的问题");

        var plan = ContextPlanner.Plan([], own, budget: 7_000, _ => null, TestPhrases.Chinese).Plan!;

        var summaries = plan.Summaries.ToList();
        Assert.Equal(2, summaries.Count);
        Assert.True(summaries.All(s => s.Closed));
        Assert.Equal((0, 7), (summaries[0].Key.FromSeq, summaries[0].Key.ToSeq));
        Assert.Equal((8, 15), (summaries[1].Key.FromSeq, summaries[1].Key.ToSeq));
        Assert.Equal("现在的问题", plan.RawItems.Last().Text);
        Assert.Equal(16, plan.RawItems.First().Message.Seq);
        Assert.True(plan.EstimatedTokens <= 7_000);
        Assert.IsType<SummaryPart>(plan.Parts[0]);
    }

    [Fact]
    public void Closed_chunk_boundaries_do_not_move_as_the_conversation_grows()
    {
        var turns = Turns(Root, 13, Long);
        var shorter = ContextPlanner.Plan([], Segment(Root, null, turns[..24], "问"), 9_000, _ => null, TestPhrases.Chinese).Plan!;
        var longer = ContextPlanner.Plan([], Segment(Root, null, turns, "问"), 9_000, _ => null, TestPhrases.Chinese).Plan!;

        Assert.Equal(shorter.Summaries.First().Key, longer.Summaries.First().Key);
    }

    [Fact]
    public void A_stored_summary_counts_with_its_real_size()
    {
        var own = Segment(Root, null, Turns(Root, 12, Long), "问");
        var first = ContextPlanner.Plan([], own, 9_000, _ => null, TestPhrases.Chinese).Plan!.Summaries.First().Key;

        // With a tiny stored summary for the first chunk, compressing it alone is enough.
        var plan = ContextPlanner.Plan([], own, 9_000, key => key == first ? 50 : null, TestPhrases.Chinese).Plan!;

        Assert.Equal([first], plan.Summaries.Select(s => s.Key));
    }

    [Fact]
    public void The_question_quote_and_direct_source_are_never_compressed()
    {
        var parent = Segment(Root, null, Turns(Root, 12, Long), null);
        var own = Segment(Child, "蒸汽机", [], "问");

        var plan = ContextPlanner.Plan([parent], own, 4_000, _ => null, TestPhrases.Chinese).Plan!;

        var raw = plan.RawItems.ToList();
        Assert.Equal("关于上文中的这段内容：\n> 蒸汽机\n\n问", raw[^1].Text);
        Assert.Equal(Root, raw[^2].Message.ConversationId);
        Assert.Equal(MessageRole.Assistant, raw[^2].Message.Role);
        Assert.Equal(23, raw[^2].Message.Seq);
        Assert.Equal(22, raw[^3].Message.Seq);
        Assert.Equal(3, raw.Count);
    }

    [Fact]
    public void A_question_that_cannot_fit_with_its_source_is_refused()
    {
        var own = Segment(Root, null, [], new string('问', 5_000));

        Assert.Equal(ContextRejection.QuestionTooLarge, ContextPlanner.Plan([], own, 4_000, _ => null, TestPhrases.Chinese).Rejection);
    }

    [Fact]
    public void History_too_long_even_when_summarized_is_refused_not_truncated()
    {
        var own = Segment(Root, null, Turns(Root, 40, Long), "问");

        Assert.Equal(ContextRejection.HistoryTooLong, ContextPlanner.Plan([], own, 2_000, _ => null, TestPhrases.Chinese).Rejection);
    }

    [Fact]
    public void A_retried_answer_inside_a_range_changes_the_summary_identity()
    {
        var turns = Turns(Root, 12, Long);
        var before = ContextPlanner.Plan([], Segment(Root, null, turns, "问"), 9_000, _ => null, TestPhrases.Chinese).Plan!.Summaries.First().Key;

        // Same range, but the fourth answer has a newer attempt that replaces it in the selection.
        var retried = turns.Take(8).Append(Item(Message(Root, 7, MessageRole.Assistant, Long) with { Supersedes = turns[7].Message.Id })).ToList();
        var after = ContextPlanner.Plan([], Segment(Root, null, [.. retried, .. turns.Skip(8)], "问"), 9_000, _ => null, TestPhrases.Chinese).Plan!.Summaries.First().Key;

        Assert.Equal(before.FromSeq, after.FromSeq);
        Assert.NotEqual(before.InputHash, after.InputHash);
    }

    private static ContextSegment Segment(ConversationId conversation, string? quote, IReadOnlyList<ContextItem> turns, string? question)
    {
        var messages = turns.Select(t => t.Message).ToList();
        if (question is not null)
        {
            messages.Add(Message(conversation, messages.Count, MessageRole.User, question));
        }

        return new ContextSegment(conversation, quote, messages);
    }

    private static List<ContextItem> Turns(ConversationId conversation, int count, string answer)
    {
        var list = new List<ContextItem>();
        for (var i = 0; i < count; i++)
        {
            list.Add(Item(Message(conversation, i * 2, MessageRole.User, $"问题 {i}")));
            list.Add(Item(Message(conversation, (i * 2) + 1, MessageRole.Assistant, answer)));
        }

        return list;
    }

    private static ContextItem Item(Message message) => new(message, message.Text);

    private static Message Message(ConversationId conversation, int seq, MessageRole role, string text) =>
        new(MessageId.New(), conversation, seq, role, ContentBlocks.FromText(text), MessageState.Complete, 0, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static ModelSnapshot Model(int? contextWindow, int? maxOutput) =>
        new("m", "https://x/v1", ChatProtocol.ChatCompletions, "m", "c", maxOutput, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ContextWindowTokens = contextWindow });
}
