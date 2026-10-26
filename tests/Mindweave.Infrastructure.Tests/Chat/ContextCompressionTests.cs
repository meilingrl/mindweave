using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>S3 through the real store and coordinator: budget, summaries in the answer's slot, reuse, failure.</summary>
public class ContextCompressionTests
{
    /// <summary>Budget 12,000 − 4,096 − 1,200 = 6,704 estimated tokens.</summary>
    private static readonly ModelSettingsInput SmallModel = new("fake", "https://fake.example/v1", "fake-model", "sk-test", ContextWindowTokens: 12_000);

    /// <summary>About 1,000 estimated tokens: four turns close a chunk.</summary>
    private static readonly string LongAnswer = new('史', 1_000);

    /// <summary>Six long turns fit (about 6,050); adding this question does not, so it is the one that compresses.</summary>
    private static readonly string Tipping = new('补', 800);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_conversation_over_budget_sends_older_turns_as_a_summary_and_keeps_recent_ones()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        var conversation = await LongConversationAsync(h, turns: 6);

        var answer = await AskAsync(h, conversation, "最后的问题" + Tipping);

        var request = h.Client.Requests.Last(FakeChatClient.IsAnswer);
        Assert.Equal(ChatRole.System, request.Turns[0].Role);
        Assert.StartsWith("以下是较早一段对话（第 1–8 条消息）的摘要，原文保存在本地：", request.Turns[0].Content, StringComparison.Ordinal);
        Assert.StartsWith("最后的问题", request.Turns[^1].Content, StringComparison.Ordinal);
        Assert.Equal("问题 4", request.Turns[1].Content);
        Assert.True(TokenEstimator.PerRequest + request.Turns.Sum(t => TokenEstimator.Message(t.Content)) <= 6_704);

        // The original record is complete and the answer is marked as sent with a summary.
        Assert.Equal(14, (await h.Store.GetMessagesAsync(conversation, Ct)).Count);
        Assert.Contains(answer.Id, await h.Summaries.CompressedAnswersAsync(conversation, Ct));
        var traces = await h.Requests.ForTaskAsync((await TaskOfAsync(h, answer.Id)), Ct);
        Assert.Equal(2, traces.Count);
    }

    [Fact]
    public async Task A_later_question_reuses_the_stored_summary()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        var conversation = await LongConversationAsync(h, turns: 6);
        await AskAsync(h, conversation, "第一次" + Tipping);
        var summaries = h.Client.Requests.Count(FakeChatClient.IsSummary);

        await AskAsync(h, conversation, "第二次" + Tipping);

        Assert.Equal(summaries, h.Client.Requests.Count(FakeChatClient.IsSummary));
        Assert.Single(await h.Summaries.ListAsync([conversation], Ct));
    }

    [Fact]
    public async Task Sibling_branches_share_the_summary_of_their_common_ancestor()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        var root = await LongConversationAsync(h, turns: 6);
        var source = (await h.Store.GetMessagesAsync(root, Ct))[^1];
        var first = (await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(source, "史"), Ct)).Child;
        var second = (await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(source, "史史"), Ct)).Child;

        await AskAsync(h, first, "甲" + Tipping);
        var afterFirst = h.Client.Requests.Count(FakeChatClient.IsSummary);
        await AskAsync(h, second, "乙" + Tipping);

        Assert.Equal(afterFirst, h.Client.Requests.Count(FakeChatClient.IsSummary));
        var owned = Assert.Single(await h.Summaries.ListAsync([root, first, second], Ct));
        Assert.Equal(root, owned.Key.Conversation);
    }

    [Fact]
    public async Task A_failed_summary_fails_the_answer_keeps_the_question_and_can_be_retried()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        var conversation = await LongConversationAsync(h, turns: 6);
        h.Client.FailSummaries = GenerationErrorKind.Network;

        var failed = await AskAsync(h, conversation, "会失败的问题" + Tipping);

        Assert.Equal(MessageState.Failed, failed.State);
        Assert.Equal(GenerationErrorKind.CompressionFailed, failed.ErrorKind);
        Assert.Contains("simulated summary failure", failed.ErrorDetail, StringComparison.Ordinal);
        Assert.Equal(0, h.Client.Requests.Count(r => FakeChatClient.IsAnswer(r) && r.Turns[^1].Content.StartsWith("会失败的问题", StringComparison.Ordinal)));
        var messages = await h.Store.GetMessagesAsync(conversation, Ct);
        Assert.StartsWith("会失败的问题", messages[^2].Text, StringComparison.Ordinal);

        h.Client.FailSummaries = null;
        var retry = await h.Conversations.RetryAsync(conversation, failed.Id, Ct);
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(retry.Answer!.Value)).State);
    }

    [Fact]
    public async Task Stopping_while_summarizing_stops_the_answer()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        var conversation = await LongConversationAsync(h, turns: 6);
        h.Client.SummaryGate = new TaskCompletionSource();

        var sent = await h.Conversations.AskAsync(conversation, "问" + Tipping, Ct);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Count(FakeChatClient.IsSummary) == 1);
        await h.Conversations.StopAsync(sent.Answer!.Value);

        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal(MessageState.Stopped, answer.State);
        Assert.Equal(0, h.Client.Requests.Count(r => FakeChatClient.IsAnswer(r) && r.Turns[^1].Content.StartsWith("问补", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_question_that_alone_exceeds_the_budget_is_refused_and_nothing_is_stored()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        var conversation = await LongConversationAsync(h, turns: 1);

        var refused = await h.Conversations.AskAsync(conversation, new string('长', 7_000), Ct);
        var refusedStart = await h.Conversations.StartAsync(h.Workspace, new string('长', 7_000), Ct);

        Assert.Equal(SendRejection.QuestionTooLargeForModel, refused.Rejection);
        Assert.Equal(SendRejection.QuestionTooLargeForModel, refusedStart.Rejection);
        Assert.Equal(2, (await h.Store.GetMessagesAsync(conversation, Ct)).Count);
        Assert.Single(await h.Conversations.ListAsync(h.Workspace, Ct));
    }

    [Fact]
    public async Task A_conversation_that_fits_is_never_summarized()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        var conversation = await LongConversationAsync(h, turns: 3);

        var answer = await AskAsync(h, conversation, "问");

        Assert.Equal(0, h.Client.Requests.Count(FakeChatClient.IsSummary));
        Assert.DoesNotContain(answer.Id, await h.Summaries.CompressedAnswersAsync(conversation, Ct));
    }

    [Fact]
    public async Task The_declared_context_capacity_is_validated_and_kept_with_the_model()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);

        await Assert.ThrowsAsync<ModelSettingsException>(() => h.Models.SaveAsync(SmallModel with { ContextWindowTokens = 100 }, Ct));
        await h.Models.SaveAsync(SmallModel with { ContextWindowTokens = 128_000 }, Ct);

        Assert.Equal(128_000, (await h.Models.GetActiveSnapshotAsync(Ct))!.Capabilities.ContextWindowTokens);
        Assert.Equal(128_000 - 4_096 - 12_800, ContextBudget.For((await h.Models.GetActiveSnapshotAsync(Ct))!).Available);
    }

    private static async Task<ConversationId> LongConversationAsync(ChatHarness h, int turns)
    {
        h.Client.Reply(LongAnswer);
        var sent = await h.Conversations.StartAsync(h.Workspace, "问题 0", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        for (var i = 1; i < turns; i++)
        {
            h.Client.Reply(LongAnswer);
            await AskAsync(h, sent.Conversation, $"问题 {i}");
        }

        return sent.Conversation;
    }

    private static async Task<Message> AskAsync(ChatHarness h, ConversationId conversation, string question)
    {
        var sent = await h.Conversations.AskAsync(conversation, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        return await h.WaitForAnswerAsync(sent.Answer!.Value);
    }

    private static Task<GenerationTaskId> TaskOfAsync(ChatHarness h, MessageId answer) =>
        h.Database.ReadAsync(
            c => GenerationTaskId.Parse(Infrastructure.Sqlite.SqliteCommands.Scalar<string>(c, null, "SELECT id FROM generation_task WHERE target_message_id = $id;", ("$id", answer.ToString()))!),
            CancellationToken.None);
}
