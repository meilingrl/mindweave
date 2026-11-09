using Mindweave.Application.Ai;
using Mindweave.Application.Models;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// A model that bills more than the estimate (Claude through a relay, 2026-11-05): the budget learns it from the
/// reported usage, so requests keep fitting the declared window.
/// </summary>
public class TokenCalibrationFlowTests
{
    /// <summary>12,000 − 4,096 output − 1,200 margin = 6,704 billed tokens of input.</summary>
    private static readonly ModelSettingsInput SmallModel = new("fake", "https://fake.example/v1", "fake-model", "sk-test", ContextWindowTokens: 12_000);

    private const int InputTokens = 6_704;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int Estimate(ChatRequest request) => TokenEstimator.Request(request.Turns.Select(t => t.Content));

    /// <summary>Half as much again as the estimate, plus a hidden prompt.</summary>
    private static int Billed(ChatRequest request) => (int)Math.Ceiling((1.5 * Estimate(request)) + 500);

    [Fact]
    public async Task Every_answer_request_fits_once_the_model_has_reported_its_counts()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        h.Client.PromptTokens = r => Billed(r);
        var conversation = await AskAsync(h, null, "问题 0");
        for (var i = 1; i < 8; i++)
        {
            h.Client.Reply(new string('史', 600));
            await AskAsync(h, conversation, $"问题 {i}" + new string('补', 300));
        }

        // The plain estimate would have let about 6,700 estimated tokens through, billed as about 10,500.
        var answers = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.All(answers, r => Assert.True(Billed(r) <= InputTokens, $"billed {Billed(r)} for estimate {Estimate(r)}"));
        Assert.NotEmpty(await h.Summaries.CompressedAnswersAsync(conversation, Ct));
    }

    [Fact]
    public async Task Requests_keep_their_estimate_and_the_budget_shows_billed_tokens()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        h.Client.PromptTokens = r => Billed(r);
        var conversation = await AskAsync(h, null, "第一个问题");

        var sent = h.Client.Requests.ToList();
        var samples = await h.Requests.RecentTokenSamplesAsync("https://fake.example/v1", "fake-model", 50, Ct);
        Assert.Equal(sent.Select(r => (Estimate(r), Billed(r))).Order(), samples.Select(s => (s.Estimate, s.Reported)).Order());
        Assert.Empty(await h.Requests.RecentTokenSamplesAsync("https://fake.example/v1", "other-model", 50, Ct));
        Assert.Empty(await h.Requests.RecentTokenSamplesAsync("https://other.example/v1", "fake-model", 50, Ct));

        var usage = (await h.Conversations.GetContextUsageAsync(conversation, Ct))!;
        var raw = TokenEstimator.PerRequest + (await h.Store.GetMessagesAsync(conversation, Ct)).Sum(m => TokenEstimator.Message(m.Text));
        Assert.Equal(InputTokens, usage.Budget);
        var fitted = TokenCalibration.Fit([.. samples.Select(s => (s.Estimate, s.Reported))]);
        Assert.Equal(fitted.Predict(raw), usage.Tokens);
        Assert.True(usage.Tokens > raw);
    }

    [Fact]
    public async Task A_service_that_reports_no_usage_keeps_the_plain_estimate()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        var conversation = await AskAsync(h, null, "第一个问题");

        var usage = (await h.Conversations.GetContextUsageAsync(conversation, Ct))!;
        var raw = TokenEstimator.PerRequest + (await h.Store.GetMessagesAsync(conversation, Ct)).Sum(m => TokenEstimator.Message(m.Text));
        Assert.Equal(raw, usage.Tokens);
        Assert.Equal(InputTokens, usage.Budget);
    }

    /// <summary>Asks, then waits until every request so far is recorded: traces are written just after each request ends.</summary>
    private static async Task<ConversationId> AskAsync(ChatHarness h, ConversationId? conversation, string question)
    {
        var sent = conversation is { } id
            ? await h.Conversations.AskAsync(id, question, Ct)
            : await h.Conversations.StartAsync(h.Workspace, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal(MessageState.Complete, answer.State);
        if (conversation is null)
        {
            // The title is asked for beside the first answer; it is a request too, so wait until it is in.
            await ChatHarness.UntilAsync(async () => (await h.Conversations.LoadAsync(sent.Conversation, Ct))!.Conversation.TitleSource == TitleSource.Auto);
        }

        await ChatHarness.UntilAsync(async () => (await h.Requests.RecentTokenSamplesAsync("https://fake.example/v1", "fake-model", 1_000, Ct)).Count == (h.Client.PromptTokens is null ? 0 : h.Client.Requests.Count));
        return sent.Conversation;
    }
}
