using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The first to-do of docs/design/agent-loop.md §8 (reply quality, increment 1): the digest of a long attachment was asked
/// for in segments as large as the input space allows, so the first segment's request left a reasoning model too little
/// output (about 4,000 tokens in a 16,000-token window) and came back cut off. The first real comparison (2026-11-27,
/// docs/reviews/reply-quality-20261127) still saw a digest cut off at 8,192 tokens: digests now follow the rule of the
/// other summaries, 16,384 tokens of output (as much as a small window leaves) and one more request after a cut-off.
/// </summary>
public class AttachmentDigestOutputTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_digest_request_of_a_long_attachment_leaves_the_full_output_allowance()
    {
        const int window = 16_000;
        await using var h = await ChatHarness.StartAsync(model: new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", window));
        var paste = (await h.ResourceService.AttachPasteAsync(Application.Storage.DraftSlot.NewExploration(h.Workspace), LongText(60), Ct)).Resource!;

        var sent = await h.Conversations.StartAsync(h.Workspace, "第41节说了什么？", [paste.Resource.Id], null, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(sent.Answer!.Value)).State);

        var digests = h.Client.Requests.Where(FakeChatClient.IsResourceSummary).ToList();
        Assert.True(digests.Count >= 2, $"{digests.Count} digest requests");
        var budget = ContextBudget.For(digests[0].Model);
        foreach (var digest in digests)
        {
            var input = TokenEstimator.Request(digest.Turns.Select(t => t.Content));
            var output = digest.Model.MaxOutputTokens!.Value;

            // A 16,000-token window cannot leave the whole 16,384: the digest gets all but a small input, more than the 8,192
            // a reasoning model was seen to run out of.
            Assert.True(output > 8_192, $"output {output}");
            Assert.True(input + output <= window - budget.Margin, $"input {input} and output {output} pass {window}");
        }
    }

    [Fact]
    public async Task A_digest_in_a_large_window_asks_for_the_same_output_as_the_other_summaries()
    {
        await using var h = await ChatHarness.StartAsync(model: new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", 32_000));
        var paste = (await h.ResourceService.AttachPasteAsync(Application.Storage.DraftSlot.NewExploration(h.Workspace), LongText(200), Ct)).Resource!;

        var sent = await h.Conversations.StartAsync(h.Workspace, "第41节说了什么？", [paste.Resource.Id], null, Ct);
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(sent.Answer!.Value)).State);

        var digests = h.Client.Requests.Where(FakeChatClient.IsResourceSummary).ToList();
        Assert.NotEmpty(digests);
        Assert.All(digests, d => Assert.Equal(ContextCompressor.SummaryOutputTokens, d.Model.MaxOutputTokens));
        Assert.Equal(ContextCompressor.SummaryOutputTokens, ResourceContext.SummaryOutputTokens);
    }

    [Fact]
    public async Task A_digest_cut_off_at_the_output_limit_is_never_used_and_is_requested_once_more()
    {
        await using var h = await ChatHarness.StartAsync(model: new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", 16_000));
        var paste = (await h.ResourceService.AttachPasteAsync(Application.Storage.DraftSlot.NewExploration(h.Workspace), LongText(60), Ct)).Resource!;
        h.Client.ResourceSummaries.TruncateNext();

        var sent = await h.Conversations.StartAsync(h.Workspace, "第41节说了什么？", [paste.Resource.Id], null, Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Complete, answer.State);
        var digests = h.Client.Requests.Where(FakeChatClient.IsResourceSummary).ToList();
        Assert.Equal(digests[0].Turns[^1].Content, digests[1].Turns[^1].Content);
        Assert.DoesNotContain(h.Client.Requests.Where(FakeChatClient.IsAnswer).SelectMany(r => r.Turns), t => t.Content.Contains(FakeChatClient.TruncatedSummaryText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_digest_cut_off_twice_fails_the_answer_and_says_it_was_tried_twice()
    {
        await using var h = await ChatHarness.StartAsync(model: new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", 16_000));
        var paste = (await h.ResourceService.AttachPasteAsync(Application.Storage.DraftSlot.NewExploration(h.Workspace), LongText(60), Ct)).Resource!;
        h.Client.ResourceSummaries.TruncateNext(2);

        var sent = await h.Conversations.StartAsync(h.Workspace, "第41节说了什么？", [paste.Resource.Id], null, Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(GenerationErrorKind.CompressionFailed, answer.ErrorKind);
        Assert.Contains("截断", answer.ErrorDetail, StringComparison.Ordinal);
        Assert.Contains($"已尝试 {ContextCompressor.SummaryAttempts} 次", answer.ErrorDetail, StringComparison.Ordinal);
        Assert.Equal(ContextCompressor.SummaryAttempts, h.Client.Requests.Count(FakeChatClient.IsResourceSummary));
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsAnswer);
    }

    private static string LongText(int sections) => string.Concat(Enumerable.Range(0, sections).Select(i => $"第{i}节。" + new string('村', 195) + "\n\n"));
}
