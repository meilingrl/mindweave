using System.Runtime.CompilerServices;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The scheduler's own rules, checked directly (architecture §6): user work runs ahead of background work, no more than
/// <see cref="GenerationOptions.Slots"/> requests are open at once, and stopping a queued task reports it once.
/// </summary>
public class SchedulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_queued_answer_starts_before_a_title_queued_earlier()
    {
        await using var h = await ChatHarness.StartAsync(slots: 1);
        var first = h.Client.Hold("一");
        var second = h.Client.Hold("二");
        var one = await h.Conversations.StartAsync(h.Workspace, "问一", Ct);
        await first.Started.Task.WaitAsync(Ct);
        var two = await h.Conversations.StartAsync(h.Workspace, "问二", Ct);

        // The first answer ends; the second takes the only slot and the first conversation's title waits behind it.
        first.Release.TrySetResult();
        await second.Started.Task.WaitAsync(Ct);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 2);
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsTitle);

        // Asked after the title was queued, yet sent before it.
        var three = await h.Conversations.StartAsync(h.Workspace, "问三", Ct);
        second.Release.TrySetResult();
        await h.WaitForAnswerAsync(three.Answer!.Value);
        await h.SettleAsync(one.Conversation);
        await h.SettleAsync(two.Conversation);
        await h.SettleAsync(three.Conversation);

        var sent = h.Client.Requests.ToList();
        var thirdAnswer = sent.FindIndex(r => FakeChatClient.IsAnswer(r) && r.Turns[^1].Content == "问三");
        Assert.True(thirdAnswer >= 0);
        Assert.True(thirdAnswer < sent.FindIndex(FakeChatClient.IsTitle), "The answer asked last went after a queued title.");
    }

    [Fact]
    public async Task No_more_requests_are_open_at_once_than_there_are_slots()
    {
        var counting = new CountingClient(new FakeChatClient());
        await using var h = await ChatHarness.StartAsync(client: counting);
        var held = Enumerable.Range(0, 4).Select(i => counting.Inner.Hold($"部分{i}")).ToList();
        var answers = new List<MessageId>();
        for (var i = 0; i < held.Count; i++)
        {
            answers.Add((await h.Conversations.StartAsync(h.Workspace, $"问{i}", Ct)).Answer!.Value);
        }

        await Task.WhenAll(held.Take(2).Select(r => r.Started.Task)).WaitAsync(Ct);
        Assert.Equal(2, counting.Open);
        Assert.Equal(4, h.Generation.UnfinishedAnswerCount);

        foreach (var reply in held)
        {
            await reply.Started.Task.WaitAsync(Ct);
            reply.Release.TrySetResult();
        }

        foreach (var answer in answers)
        {
            Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(answer)).State);
        }

        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        Assert.Equal(2, counting.MostOpen);
    }

    [Fact]
    public async Task Stopping_a_queued_summary_cancels_it_and_reports_it_finished_once()
    {
        await using var h = await ChatHarness.StartAsync(slots: 1);
        var summarized = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(summarized.Answer!.Value);
        await h.SettleAsync(summarized.Conversation);

        var busy = h.Client.Hold("部分");
        var other = await h.Conversations.StartAsync(h.Workspace, "另一个问题", Ct);
        await busy.Started.Task.WaitAsync(Ct);

        var finished = new List<GenerationTaskRecord>();
        h.Generation.SummaryTaskFinished += (_, task) =>
        {
            lock (finished)
            {
                finished.Add(task);
            }
        };
        var queued = await h.SummaryService.SummarizeAsync(summarized.Conversation, Ct);
        Assert.True(queued.Accepted);
        Assert.True(h.Generation.HasUnfinishedSummary(summarized.Conversation));

        await h.Generation.StopTaskAsync(queued.Task!.Value);

        Assert.False(h.Generation.HasUnfinishedSummary(summarized.Conversation));
        Assert.Equal([queued.Task.Value], finished.Select(t => t.Id));
        var state = await h.Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT state FROM generation_task WHERE id = $id;", ("$id", queued.Task.Value.ToString()));
                return cmd.ExecuteScalar() as string;
            },
            Ct);
        Assert.Equal("cancelled", state);

        busy.Release.TrySetResult();
        await h.WaitForAnswerAsync(other.Answer!.Value);
        await h.SettleAsync(other.Conversation);
        Assert.Single(finished);
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsKnowledgeSummary);
    }

    /// <summary>Counts the streams open at once around a fake client.</summary>
    private sealed class CountingClient(FakeChatClient inner) : IChatClient
    {
        private int _open;
        private int _mostOpen;

        public FakeChatClient Inner { get; } = inner;

        public int Open => Volatile.Read(ref _open);

        public int MostOpen => Volatile.Read(ref _mostOpen);

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var open = Interlocked.Increment(ref _open);
            int most;
            while (open > (most = Volatile.Read(ref _mostOpen)) && Interlocked.CompareExchange(ref _mostOpen, open, most) != most)
            {
            }

            try
            {
                await foreach (var item in Inner.StreamAsync(request, apiKey, cancellationToken))
                {
                    yield return item;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
            Inner.ListModelsAsync(baseUrl, apiKey, cancellationToken);
    }
}
