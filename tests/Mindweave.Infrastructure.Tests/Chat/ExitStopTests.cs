using System.Runtime.CompilerServices;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The generation side of the exit sequence (architecture §12.3 steps 2–5): what the prompt counts, the bounded
/// stop, and going back to work after a failed exit without reviving what was stopped.
/// </summary>
public class ExitStopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_exit_prompt_counts_user_answers_and_summaries_separately_but_not_titles()
    {
        await using var h = await ChatHarness.StartAsync();
        var titles = h.Client.HoldTitles();
        var done = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(done.Answer!.Value);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsTitle));
        Assert.False(h.Generation.UnfinishedUserWork.Any);

        h.Client.KnowledgeGate = new TaskCompletionSource();
        Assert.True((await h.SummaryService.SummarizeAsync(done.Conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsKnowledgeSummary));

        // Both slots are taken (the held title and the summary), so this answer waits in the queue; queued counts too.
        h.Client.Hold("进行中");
        await h.Conversations.StartAsync(h.Workspace, "另一个问题", Ct);

        Assert.Equal(new UnfinishedUserWork(1, 1), h.Generation.UnfinishedUserWork);

        await h.Generation.StopForExitAsync(TimeSpan.FromSeconds(5));
        Assert.False(h.Generation.UnfinishedUserWork.Any);
        Assert.Equal(0, h.Generation.UnfinishedCount);
        titles.TrySetResult();
        h.Client.KnowledgeGate.TrySetResult();
    }

    [Fact]
    public async Task Stopping_for_exit_waits_only_a_bounded_time_for_a_task_that_ignores_cancellation()
    {
        var stubborn = new StubbornClient();
        await using var h = await ChatHarness.StartAsync(client: stubborn);
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await stubborn.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value, m => m.Text == "已收到");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var acknowledged = await h.Generation.StopForExitAsync(TimeSpan.FromMilliseconds(300));
        watch.Stop();

        Assert.False(acknowledged);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(4));

        // The last checkpoint stands; the next start would mark the answer interrupted.
        var stored = await h.GetMessageAsync(sent.Answer!.Value);
        Assert.Equal("已收到", stored!.Text);
        Assert.Equal(MessageState.Streaming, stored.State);
        stubborn.Release.TrySetResult();
    }

    [Fact]
    public async Task After_reopening_new_work_runs_and_what_the_exit_stopped_stays_stopped()
    {
        await using var h = await ChatHarness.StartAsync();
        var holds = Enumerable.Range(0, 2).Select(_ => h.Client.Hold("已收到")).ToArray();
        var a = await h.Conversations.StartAsync(h.Workspace, "a", Ct);
        var b = await h.Conversations.StartAsync(h.Workspace, "b", Ct);
        await Task.WhenAll(holds.Select(x => x.Started.Task)).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var queued = await h.Conversations.StartAsync(h.Workspace, "c", Ct);

        Assert.True(await h.Generation.StopForExitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(h.Generation.IsClosed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Conversations.StartAsync(h.Workspace, "关闭中", Ct));

        h.Generation.Reopen();
        Assert.False(h.Generation.IsClosed);
        var fresh = await h.Conversations.StartAsync(h.Workspace, "新问题", Ct);
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(fresh.Answer!.Value)).State);

        Assert.Equal(MessageState.Stopped, (await h.GetMessageAsync(a.Answer!.Value))!.State);
        Assert.Equal(MessageState.Stopped, (await h.GetMessageAsync(b.Answer!.Value))!.State);
        Assert.Equal(MessageState.Cancelled, (await h.GetMessageAsync(queued.Answer!.Value))!.State);
        Assert.Equal(0, h.Generation.UnfinishedCount);
    }

    [Fact]
    public async Task A_title_stopped_by_an_exit_is_asked_for_again_when_the_next_answer_completes()
    {
        await using var h = await ChatHarness.StartAsync();
        var titles = h.Client.HoldTitles();
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Count(FakeChatClient.IsTitle) == 1);

        await h.Generation.StopForExitAsync(TimeSpan.FromSeconds(5));
        Assert.Null((await h.Store.GetAsync(sent.Conversation, Ct))!.Title);
        titles.TrySetResult();
        h.Generation.Reopen();

        var again = await h.Conversations.AskAsync(sent.Conversation, "再问一个", Ct);
        await h.WaitForAnswerAsync(again.Answer!.Value);
        await ChatHarness.UntilAsync(() => h.Store.GetAsync(sent.Conversation, Ct).Result!.Title == FakeChatClient.DefaultTitle);
        Assert.Equal(2, h.Client.Requests.Count(FakeChatClient.IsTitle));
    }

    [Fact]
    public async Task Failed_terminal_writes_are_reported_until_they_are_saved()
    {
        ConversationFlowTests.FlakyStore? flaky = null;
        await using var h = await ChatHarness.StartAsync(wrapStore: s => flaky = new ConversationFlowTests.FlakyStore(s));
        var held = h.Client.Hold("已收到");
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        flaky!.FailFinishes = int.MaxValue;
        await h.Generation.StopForExitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await h.Generation.SaveUnsavedAsync());

        flaky.FailFinishes = 0;
        Assert.True(await h.Generation.SaveUnsavedAsync());
        Assert.Equal(MessageState.Stopped, (await h.GetMessageAsync(sent.Answer!.Value))!.State);
    }

    /// <summary>Streams one piece, then waits for <see cref="Release"/> without honouring cancellation.</summary>
    private sealed class StubbornClient : IChatClient
    {
        private readonly FakeChatClient _fallback = new();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!FakeChatClient.IsAnswer(request))
            {
                await foreach (var item in _fallback.StreamAsync(request, apiKey, cancellationToken))
                {
                    yield return item;
                }

                yield break;
            }

            yield return new ChatStreamEvent.TextDelta("已收到");
            Started.TrySetResult();
            await Release.Task;
            yield return new ChatStreamEvent.Completed(false);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
            _fallback.ListModelsAsync(baseUrl, apiKey, cancellationToken);
    }
}
