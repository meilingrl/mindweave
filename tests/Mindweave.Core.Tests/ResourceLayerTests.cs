using Mindweave.Core.Branches;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;

namespace Mindweave.Core.Tests;

/// <summary>
/// The attachment layer of a plan (architecture §15.4, requirements §10.4.3): whole when it fits; then older attachments as
/// summaries, older conversation summarized, the current attachment as summary and relevant parts; refused, never cut silently.
/// </summary>
public class ResourceLayerTests
{
    private static readonly ConversationId Conversation = ConversationId.New();

    [Fact]
    public void Attachments_that_fit_are_sent_whole_and_a_message_counts_only_their_names()
    {
        var (own, candidates) = Conversation1(older: 0, currentText: new string('文', 3_000));

        var plan = ContextPlanner.Plan([], own, budget: 10_000, _ => null, candidates).Plan!;

        var placement = Assert.Single(plan.Resources);
        Assert.Equal(ResourceForm.Whole, placement.Form);
        Assert.False(plan.IsResourceReduced);
        Assert.Equal("[附件：doc.txt]\n\n这讲了什么", plan.RawItems.Last().Text);
        Assert.True(plan.EstimatedTokens > 3_000);
    }

    [Fact]
    public void A_current_attachment_too_large_is_sent_as_its_summary_and_the_most_relevant_parts_in_text_order()
    {
        var text = string.Concat(Enumerable.Range(0, 20).Select(i => new string('文', 790) + "。\n\n"));
        var (own, candidates) = Conversation1(older: 0, currentText: text, ranking: [7, 3, 12]);

        var plan = ContextPlanner.Plan([], own, budget: 3_000, _ => null, candidates).Plan!;

        var placement = Assert.Single(plan.Resources);
        Assert.Equal(ResourceForm.Parts, placement.Form);
        Assert.True(placement.NeedsSummary);
        Assert.Equal(20, placement.ChunkCount);
        Assert.Equal([3, 7], placement.Chunks); // the two best fit; sent in the text's order
        Assert.True(plan.EstimatedTokens <= 3_000);
    }

    [Fact]
    public void Older_attachments_switch_to_their_summaries_before_older_conversation_is_summarized()
    {
        var (own, candidates) = Conversation1(older: 2, currentText: "短的", olderText: new string('旧', 3_000));

        var plan = ContextPlanner.Plan([], own, budget: 5_000, _ => null, candidates).Plan!;

        // Oldest first, and only as many as needed.
        Assert.Equal([ResourceForm.Summary, ResourceForm.Whole, ResourceForm.Whole], plan.Resources.Select(r => r.Form));
        Assert.False(plan.IsCompressed); // the conversation itself still fits once the older attachments are summaries
    }

    [Fact]
    public void A_stored_summary_counts_with_its_real_size_and_is_not_asked_for_again()
    {
        var (own, candidates) = Conversation1(older: 1, currentText: "短的", olderText: new string('旧', 3_000), olderSummaryTokens: 120);

        var plan = ContextPlanner.Plan([], own, budget: 1_500, _ => null, candidates).Plan!;

        var older = plan.Resources[0];
        Assert.Equal((ResourceForm.Summary, false), (older.Form, older.NeedsSummary));
    }

    [Fact]
    public void A_question_whose_attachments_cannot_fit_even_as_summary_and_one_part_is_refused()
    {
        var (own, candidates) = Conversation1(older: 0, currentText: new string('文', 9_000));

        var outcome = ContextPlanner.Plan([], own, budget: 900, _ => null, candidates);

        Assert.Null(outcome.Plan);
        Assert.Equal(ContextRejection.QuestionTooLarge, outcome.Rejection);
    }

    [Fact]
    public void Images_cost_a_fixed_amount_and_older_ones_are_left_out_for_room()
    {
        var question = Message(0, MessageRole.User, null, "图里是什么", image: true);
        var answer = Message(1, MessageRole.Assistant, null, new string('答', 2_000));
        var current = Message(2, MessageRole.User, null, "再看这张", image: true);
        var own = new ContextSegment(Conversation, null, [question, answer, current]);
        ResourceCandidate Image(Message m) => new(Attachment(m)!, m.Id, ResourceKind.Image, ResourceMaterial.ImageTokens + 10, null, []);

        var plan = ContextPlanner.Plan([], own, budget: 4_200, _ => null, [Image(question), Image(current)]).Plan!;

        Assert.Equal([ResourceForm.ImageOmitted, ResourceForm.Image], plan.Resources.Select(r => r.Form));
        Assert.True(plan.IsResourceReduced);
    }

    [Fact]
    public void Chunks_are_about_800_tokens_cut_at_paragraphs_and_cover_the_text()
    {
        var text = string.Concat(Enumerable.Range(0, 30).Select(i => $"第{i}段。" + new string('字', 300) + "\n\n"));

        var chunks = ResourceChunker.Split(text);

        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(text.Length, chunks[^1].End);
        Assert.All(chunks.Zip(chunks.Skip(1)), p => Assert.Equal(p.First.End, p.Second.Start));
        Assert.All(chunks, c => Assert.InRange(c.EstimatedTokens, 1, ResourceChunker.TargetTokens));
        Assert.All(chunks.SkipLast(1), c => Assert.EndsWith("\n\n", text[c.Start..c.End], StringComparison.Ordinal));
        Assert.Equal(chunks, ResourceChunker.Split(text)); // boundaries depend only on the text

        // One endless line still ends up in bounded chunks.
        Assert.All(ResourceChunker.Split(new string('x', 10_000)), c => Assert.InRange(c.EstimatedTokens, 1, ResourceChunker.TargetTokens));
    }

    [Fact]
    public void Search_terms_split_cjk_into_pairs_and_words_by_letters()
    {
        Assert.Equal("机器 器学 学习 is fun", SearchTerms.Of("机器学习 is FUN!"));
        Assert.Equal("\"蒸汽\" OR \"汽机\" OR \"watt\"", SearchTerms.Query("蒸汽机 Watt?"));
        Assert.Null(SearchTerms.Query("？！ 。"));
    }

    private static (ContextSegment Own, List<ResourceCandidate> Candidates) Conversation1(
        int older, string currentText, string? olderText = null, int[]? ranking = null, int? olderSummaryTokens = null)
    {
        var messages = new List<Message>();
        var candidates = new List<ResourceCandidate>();
        for (var i = 0; i < older; i++)
        {
            var q = Message(messages.Count, MessageRole.User, olderText, $"旧问题 {i}");
            messages.Add(q);
            messages.Add(Message(messages.Count, MessageRole.Assistant, null, "旧回答"));
            candidates.Add(Candidate(q, olderSummaryTokens, null));
        }

        var question = Message(messages.Count, MessageRole.User, currentText, "这讲了什么");
        messages.Add(question);
        candidates.Add(Candidate(question, null, ranking));
        return (new ContextSegment(Conversation, null, messages), candidates);
    }

    private static ResourceCandidate Candidate(Message message, int? summaryTokens, int[]? ranking)
    {
        var attachment = Attachment(message)!;
        var chunks = ResourceChunker.Split(attachment.Text!);
        var order = ranking ?? [];
        IReadOnlyList<ResourceChunk> ranked = [.. order.Select(o => chunks[o]), .. chunks.Where(c => !order.Contains(c.Ordinal))];
        return new ResourceCandidate(attachment, message.Id, ResourceKind.Text, TokenEstimator.Estimate(ResourceMaterial.Whole(attachment)), summaryTokens, ranked);
    }

    private static AttachmentBlock? Attachment(Message message) => message.Blocks.OfType<AttachmentBlock>().FirstOrDefault();

    private static Message Message(int seq, MessageRole role, string? attached, string text, bool image = false)
    {
        IReadOnlyList<ContentBlock> blocks = image
            ? [new AttachmentBlock(ResourceId.New(), "a.png", MediaTypes.Png, 10, null), new TextBlock(text)]
            : attached is null ? [new TextBlock(text)] : [new AttachmentBlock(ResourceId.New(), "doc.txt", MediaTypes.PlainText, attached.Length, attached), new TextBlock(text)];
        return new(MessageId.New(), Conversation, seq, role, blocks, MessageState.Complete, 0, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    }
}
