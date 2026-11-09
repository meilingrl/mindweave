using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Conversations;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>The "new answer" mark follows what was on screen when the answer ended, not when it was stored.</summary>
public class ConversationMarksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_answer_watched_to_its_end_is_not_marked_when_its_write_lands_after_the_reader_left()
    {
        FlakyStore? flaky = null;
        await using var h = await ChatHarness.StartAsync(wrapStore: s => flaky = new FlakyStore(s));
        var marks = new ConversationMarksService(h.Generation, h.Conversations, new SqlitePreferenceStore(h.Database), h.Health, NullLogger<ConversationMarksService>.Instance);
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flaky!.BeforeFinish = () =>
        {
            writing.TrySetResult();
            return write.Task;
        };
        var held = h.Client.Hold("火车");
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        marks.SetViewing(sent.Conversation);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The answer ends on screen; its terminal write is slow (a disk sync), and the reader moves on meanwhile.
        held.Finish("是蒸汽机车。");
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            marks.SetViewing(null);
        }
        finally
        {
            // Always let the write through: the harness waits for it when it is disposed.
            write.TrySetResult();
        }

        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await ChatHarness.UntilAsync(() => !h.Conversations.HasUnfinishedAnswer(sent.Conversation));

        Assert.False(marks.IsUnread(sent.Conversation));
    }

    [Fact]
    public async Task An_answer_that_ends_while_the_reader_is_elsewhere_is_marked_before_it_is_stored()
    {
        FlakyStore? flaky = null;
        await using var h = await ChatHarness.StartAsync(wrapStore: s => flaky = new FlakyStore(s));
        var marks = new ConversationMarksService(h.Generation, h.Conversations, new SqlitePreferenceStore(h.Database), h.Health, NullLogger<ConversationMarksService>.Instance);
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flaky!.BeforeFinish = () =>
        {
            writing.TrySetResult();
            return write.Task;
        };
        var held = h.Client.Hold("火车");
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        held.Finish("是蒸汽机车。");
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            // Marked as soon as it ended, while its write is still under way; opening it then clears the mark.
            Assert.True(marks.IsUnread(sent.Conversation));
            marks.SetViewing(sent.Conversation);
        }
        finally
        {
            write.TrySetResult();
        }

        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await ChatHarness.UntilAsync(() => !h.Conversations.HasUnfinishedAnswer(sent.Conversation));
        Assert.False(marks.IsUnread(sent.Conversation));
    }

    [Fact]
    public async Task A_follow_up_sent_while_the_last_answer_is_still_being_written_waits_for_it_and_sees_its_whole_text()
    {
        FlakyStore? flaky = null;
        await using var h = await ChatHarness.StartAsync(wrapStore: s => flaky = new FlakyStore(s));
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flaky!.BeforeFinish = () =>
        {
            if (writing.TrySetResult())
            {
                return write.Task;
            }

            return Task.CompletedTask;
        };
        var held = h.Client.Hold("火车");
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        held.Finish("是蒸汽机车。");
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Task<SendOutcome> asked;
        try
        {
            // The answer has ended on screen; its terminal write has not landed yet.
            Assert.False(h.Conversations.HasUnfinishedAnswer(sent.Conversation));
            asked = h.Conversations.AskAsync(sent.Conversation, "谁造的？", Ct);
            await ChatHarness.StaysAsync(() => !asked.IsCompleted, 100);
        }
        finally
        {
            write.TrySetResult();
        }

        var outcome = await asked.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Null(outcome.Rejection);
        await h.WaitForAnswerAsync(outcome.Answer!.Value);
        var request = h.Client.Requests.Last(Mindweave.Testing.FakeChatClient.IsAnswer);
        Assert.Contains(request.Turns, t => t.Content == "火车是蒸汽机车。");
    }
}
