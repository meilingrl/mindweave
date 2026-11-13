using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;
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
        var titles = h.Client.Titles.Hold();
        var done = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(done.Answer!.Value);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsTitle));
        Assert.False(h.Generation.UnfinishedUserWork.Any);

        h.Client.Knowledge.Gate = new TaskCompletionSource();
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
        h.Client.Knowledge.Gate.TrySetResult();
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
        var late = await h.Conversations.StartAsync(h.Workspace, "关闭中", Ct);
        Assert.Equal(MessageState.Cancelled, (await h.WaitForAnswerAsync(late.Answer!.Value)).State);

        h.Generation.Reopen();
        Assert.False(h.Generation.IsClosed);
        var fresh = await h.Conversations.StartAsync(h.Workspace, "新问题", Ct);
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(fresh.Answer!.Value)).State);

        Assert.Equal(MessageState.Stopped, (await h.GetMessageAsync(a.Answer!.Value))!.State);
        Assert.Equal(MessageState.Stopped, (await h.GetMessageAsync(b.Answer!.Value))!.State);
        Assert.Equal(MessageState.Cancelled, (await h.GetMessageAsync(queued.Answer!.Value))!.State);

        // The new answer's title follows it and its slot is released just after its terminal write: wait until settled.
        Assert.False(h.Generation.UnfinishedUserWork.Any);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
    }

    [Fact]
    public async Task A_finished_answer_still_holding_its_slot_is_not_counted_by_the_exit_prompt()
    {
        await using var h = await ChatHarness.StartAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.Generation.AnswerFinished += (_, _) =>
        {
            // Runs on the answer's worker after the terminal write, before the slot is released.
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));

        try
        {
            Assert.Equal(MessageState.Complete, (await h.GetMessageAsync(sent.Answer!.Value))!.State);
            // Still in its slot (a title may already be queued behind it), yet not something the prompt should name.
            Assert.Equal(1, h.Generation.UnfinishedAnswerCount);
            Assert.False(h.Generation.UnfinishedUserWork.Any);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Saving_unsaved_writes_waits_for_a_retry_already_writing_and_reports_that_it_failed()
    {
        FlakyStore? flaky = null;
        await using var h = await ChatHarness.StartAsync(wrapStore: s => flaky = new FlakyStore(s));
        var held = h.Client.Hold("已收到");
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        flaky!.FailFinishes = int.MaxValue;
        await h.Generation.StopForExitAsync(TimeSpan.FromSeconds(5));

        // The background retry takes the failed write out of the queue and is stuck writing it.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flaky.BeforeFinish = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        flaky.BeforeFinish = null;

        Task<bool> save;
        try
        {
            save = h.Generation.SaveUnsavedAsync();
            await ChatHarness.StaysAsync(() => !save.IsCompleted, 100);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.False(await save.WaitAsync(TimeSpan.FromSeconds(5), Ct));

        flaky.FailFinishes = 0;
        Assert.True(await h.Generation.SaveUnsavedAsync());
        Assert.Equal(MessageState.Stopped, (await h.GetMessageAsync(sent.Answer!.Value))!.State);
    }

    [Fact]
    public async Task An_answer_completing_while_the_exit_is_under_way_gets_its_title_after_returning()
    {
        var stubborn = new StubbornClient();
        await using var h = await ChatHarness.StartAsync(client: stubborn);
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await stubborn.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(await h.Generation.StopForExitAsync(TimeSpan.FromMilliseconds(100)));

        // The answer ignores the stop and completes after the entry closed; its title cannot be queued now.
        stubborn.Release.TrySetResult();
        await h.WaitForAnswerAsync(sent.Answer!.Value, m => m.State == MessageState.Complete);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        await ChatHarness.StaysAsync(async () => await QueuedTitlesAsync(h) == 0, 300);

        h.Generation.Reopen();
        var again = await h.Conversations.AskAsync(sent.Conversation, "再问一个", Ct);
        await h.WaitForAnswerAsync(again.Answer!.Value);
        await ChatHarness.UntilAsync(async () => (await h.Store.GetAsync(sent.Conversation, Ct))!.Title == FakeChatClient.DefaultTitle);
        Assert.Equal(0L, await QueuedTitlesAsync(h));
    }

    private static Task<long> QueuedTitlesAsync(ChatHarness h) =>
        h.Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT count(*) FROM generation_task WHERE kind = 'title' AND state = 'queued';");
                return (long)cmd.ExecuteScalar()!;
            },
            CancellationToken.None);

    [Fact]
    public async Task A_title_stopped_by_an_exit_is_asked_for_again_when_the_next_answer_completes()
    {
        await using var h = await ChatHarness.StartAsync();
        var titles = h.Client.Titles.Hold();
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Count(FakeChatClient.IsTitle) == 1);

        await h.Generation.StopForExitAsync(TimeSpan.FromSeconds(5));
        Assert.Null((await h.Store.GetAsync(sent.Conversation, Ct))!.Title);
        titles.TrySetResult();
        h.Generation.Reopen();

        var again = await h.Conversations.AskAsync(sent.Conversation, "再问一个", Ct);
        await h.WaitForAnswerAsync(again.Answer!.Value);
        await ChatHarness.UntilAsync(async () => (await h.Store.GetAsync(sent.Conversation, Ct))!.Title == FakeChatClient.DefaultTitle);
        Assert.Equal(2, h.Client.Requests.Count(FakeChatClient.IsTitle));
    }

    [Fact]
    public async Task Failed_terminal_writes_are_reported_until_they_are_saved()
    {
        FlakyStore? flaky = null;
        await using var h = await ChatHarness.StartAsync(wrapStore: s => flaky = new FlakyStore(s));
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
}
