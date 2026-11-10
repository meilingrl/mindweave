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

    [Fact]
    public async Task Unread_by_workspace_lists_where_new_answers_are_and_leaves_out_archived_and_seen_ones()
    {
        await using var h = await ChatHarness.StartAsync();
        var marks = new ConversationMarksService(h.Generation, h.Conversations, new SqlitePreferenceStore(h.Database), h.Health, NullLogger<ConversationMarksService>.Instance);
        var other = new Core.Workspaces.Workspace(Core.Workspaces.WorkspaceId.New(), Core.Workspaces.WorkspaceName.FromStored("另一个"), DateTimeOffset.UtcNow);
        await new SqliteWorkspaceStore(h.Database).AddAsync(other, Ct);
        Core.Workspaces.WorkspaceId[] both = [h.Workspace, other.Id];
        Assert.Empty(await marks.UnreadByWorkspaceAsync(both, Ct));

        // An answer that ends while nothing is on screen marks its conversation; the workspace it is in is listed with it.
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await ChatHarness.UntilAsync(() => marks.IsUnread(sent.Conversation));
        AssertListed([sent.Conversation], await marks.UnreadByWorkspaceAsync(both, Ct));
        Assert.Empty(await marks.UnreadByWorkspaceAsync([other.Id], Ct));

        // A second conversation of the same workspace: both are listed under it.
        var second = await h.Conversations.StartAsync(h.Workspace, "蒸汽机车有多重？", Ct);
        await h.WaitForAnswerAsync(second.Answer!.Value);
        await ChatHarness.UntilAsync(() => marks.IsUnread(second.Conversation));
        AssertListed([sent.Conversation, second.Conversation], await marks.UnreadByWorkspaceAsync(both, Ct));
        marks.MarkSeen(second.Conversation);

        // Archived: the sidebar leaves it out, so the workspace has nothing new to show.
        await ChatHarness.UntilAsync(() => !h.Conversations.HasUnfinishedAnswer(sent.Conversation));
        Assert.True((await h.Conversations.ArchiveAsync(sent.Conversation, Ct)).Archived);
        Assert.Empty(await marks.UnreadByWorkspaceAsync(both, Ct));
        Assert.Contains(sent.Conversation, await h.Conversations.RestoreAsync(sent.Conversation, Ct));
        AssertListed([sent.Conversation], await marks.UnreadByWorkspaceAsync(both, Ct));

        // Seen: no longer listed.
        marks.SetViewing(sent.Conversation);
        Assert.Empty(await marks.UnreadByWorkspaceAsync(both, Ct));

        void AssertListed(Core.Conversations.ConversationId[] expected, IReadOnlyDictionary<Core.Workspaces.WorkspaceId, IReadOnlyList<Core.Conversations.ConversationId>> found)
        {
            var entry = Assert.Single(found);
            Assert.Equal(h.Workspace, entry.Key);
            Assert.Equal(expected.ToHashSet(), entry.Value.ToHashSet());
            Assert.Equal(expected.Length, entry.Value.Count);
        }
    }

    [Fact]
    public async Task Marked_is_raised_once_when_an_answer_marks_its_conversation_and_never_for_restored_marks()
    {
        await using var h = await ChatHarness.StartAsync();
        var store = new SqlitePreferenceStore(h.Database);
        var marks = new ConversationMarksService(h.Generation, h.Conversations, store, h.Health, NullLogger<ConversationMarksService>.Instance);
        await marks.LoadAsync(Ct);
        var raised = new System.Collections.Concurrent.ConcurrentQueue<Core.Conversations.ConversationId>();
        marks.Marked += (_, id) => raised.Enqueue(id);

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await ChatHarness.UntilAsync(() => marks.IsUnread(sent.Conversation));
        Assert.Equal([sent.Conversation], raised);

        // A further answer in a conversation already marked adds nothing, so nothing is raised.
        await ChatHarness.UntilAsync(() => !h.Conversations.HasUnfinishedAnswer(sent.Conversation));
        var asked = await h.Conversations.AskAsync(sent.Conversation, "谁造的？", Ct);
        await h.WaitForAnswerAsync(asked.Answer!.Value);
        await marks.FlushAsync();
        Assert.Single(raised);

        // A second service reads the stored mark at start: a restored mark is not an answer that just ended.
        var restored = new ConversationMarksService(h.Generation, h.Conversations, store, h.Health, NullLogger<ConversationMarksService>.Instance);
        var restoredRaised = 0;
        restored.Marked += (_, _) => restoredRaised++;
        await restored.LoadAsync(Ct);
        Assert.True(restored.IsUnread(sent.Conversation));
        Assert.Equal(0, restoredRaised);
    }
}
