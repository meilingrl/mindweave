using Mindweave.Application.Conversations;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Health check 2026-11-12, item 8 (c): the request that summarizes one chunk of older context asks for no more output
/// than the model's window leaves after the chunk itself; a long pasted message makes one large chunk.
/// </summary>
public class SummaryOutputCapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_large_chunk_summary_request_fits_the_window_with_its_output_allowance()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Reply("好");
        var sent = await h.Conversations.StartAsync(h.Workspace, new string('史', 20_000), Ct);
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(sent.Answer!.Value)).State);

        var answer = await h.AskAsync(sent.Conversation, "第二问" + new string('补', 6_000));

        Assert.Equal(MessageState.Complete, answer.State);
        var summary = h.Client.Requests.Single(FakeChatClient.IsSummary);
        var input = TokenEstimator.Request(summary.Turns.Select(t => t.Content));
        Assert.True(input + summary.Model.MaxOutputTokens!.Value <= ContextBudget.DefaultCapacity, $"input {input} + output {summary.Model.MaxOutputTokens}");
        Assert.True(summary.Model.MaxOutputTokens >= ContextCompressor.MinimumSummaryOutputTokens);
    }
}
