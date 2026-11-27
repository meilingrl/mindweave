using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The first to-do of docs/design/agent-loop.md §8 (reply quality, increment 1): the digest of a long attachment was asked
/// for in segments as large as the input space allows, so the first segment's request left a reasoning model too little
/// output (about 4,000 tokens in a 16,000-token window) and came back cut off. Every digest request now leaves the full
/// digest allowance for output.
/// </summary>
public class AttachmentDigestOutputTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_digest_request_of_a_long_attachment_leaves_the_full_output_allowance()
    {
        const int window = 16_000;
        await using var h = await ChatHarness.StartAsync(model: new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", window));
        var text = string.Concat(Enumerable.Range(0, 60).Select(i => $"第{i}节。" + new string('村', 195) + "\n\n"));
        var paste = (await h.ResourceService.AttachPasteAsync(Application.Storage.DraftSlot.NewExploration(h.Workspace), text, Ct)).Resource!;

        var sent = await h.Conversations.StartAsync(h.Workspace, "第41节说了什么？", [paste.Resource.Id], null, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(sent.Answer!.Value)).State);

        var digests = h.Client.Requests.Where(FakeChatClient.IsResourceSummary).ToList();
        Assert.True(digests.Count >= 2, $"{digests.Count} digest requests");
        var budget = ContextBudget.For(digests[0].Model);
        foreach (var digest in digests)
        {
            var input = TokenEstimator.Request(digest.Turns.Select(t => t.Content));
            Assert.Equal(ResourceContext.SummaryOutputTokens, digest.Model.MaxOutputTokens);
            Assert.True(input + ResourceContext.SummaryOutputTokens <= window - budget.Margin, $"input {input} leaves too little output in {window}");
        }
    }
}
