using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Faults;

/// <summary>
/// A full disk hitting the real writer and its transactions (requirements §9.3, architecture §12.1): the write is
/// rolled back whole, it is reported as a storage failure, core content pauses new requests until storage takes
/// writes again, and the same work then succeeds.
/// </summary>
[Trait("Category", "FaultInjection")]
public class DiskFullTests
{
    /// <summary>70 KB: more than the probe's 64 KB, yet within the default model's context budget (Latin text is cheap in tokens).</summary>
    private static readonly string LongQuestion = new('a', 70_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_that_does_not_fit_is_rolled_back_whole_and_reported_as_a_full_disk()
    {
        await using var h = await ChatHarness.StartAsync();
        await StorageFaults.FillDiskAsync(h.Database);

        var error = await Assert.ThrowsAsync<StorageWriteException>(() => h.Database.WriteAsync(
            (c, t) =>
            {
                c.Execute(t, "INSERT INTO app_state (key, value) VALUES ('small', 'fits');");
                return c.Execute(t, $"INSERT INTO app_state (key, value) VALUES ('large', '{LongQuestion}');");
            },
            Ct));

        Assert.Equal(StorageFailureKind.Full, error.Kind);
        Assert.Equal(0, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM app_state WHERE key IN ('small', 'large');"), Ct));
        await Assert.ThrowsAsync<StorageWriteException>(() => h.Database.ProbeWriteAsync(Ct));
        await StorageFaults.AssertIntactAsync(h.Database);

        await StorageFaults.FreeDiskAsync(h.Database);
        await h.Database.ProbeWriteAsync(Ct);
        Assert.Equal(0, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM app_state WHERE key = 'storage-probe';"), Ct));
    }

    [Fact]
    public async Task A_question_that_does_not_fit_leaves_nothing_and_pauses_requests_until_space_returns()
    {
        await using var h = await ChatHarness.StartAsync();
        var first = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(first.Answer!.Value);
        await h.SettleAsync(first.Conversation);
        await StorageFaults.FillDiskAsync(h.Database);
        var before = await StorageFaults.CountRowsAsync(h.Database);

        var refused = await h.Conversations.AskAsync(first.Conversation, LongQuestion, Ct);

        Assert.Equal(SendRejection.StorageUnavailable, refused.Rejection);
        Assert.True(h.Conversations.IsStorageBlocked);
        Assert.Equal(before, await StorageFaults.CountRowsAsync(h.Database));
        await StorageFaults.AssertIntactAsync(h.Database);
        Assert.Equal(1, h.Client.StartedAnswers);

        // Still full: the probe keeps failing, so requests stay paused, even short ones that would fit.
        await ChatHarness.StaysAsync(() => h.Conversations.IsStorageBlocked, 300);
        Assert.Equal(SendRejection.StorageUnavailable, (await h.Conversations.AskAsync(first.Conversation, "短问题", Ct)).Rejection);

        await StorageFaults.FreeDiskAsync(h.Database);
        await ChatHarness.UntilAsync(() => !h.Conversations.IsStorageBlocked);

        var accepted = await h.Conversations.AskAsync(first.Conversation, LongQuestion, Ct);
        Assert.True(accepted.Accepted, accepted.Rejection.ToString());
        var answer = await h.WaitForAnswerAsync(accepted.Answer!.Value);
        Assert.Equal("答：" + LongQuestion, answer.Text);
        await StorageFaults.AssertIntactAsync(h.Database);
    }

    [Fact]
    public async Task A_new_conversation_that_does_not_fit_is_refused_and_leaves_no_conversation_behind()
    {
        await using var h = await ChatHarness.StartAsync();
        await StorageFaults.FillDiskAsync(h.Database);

        var refused = await h.Conversations.StartAsync(h.Workspace, LongQuestion, Ct);

        Assert.Equal(SendRejection.StorageUnavailable, refused.Rejection);
        Assert.Empty(await h.Conversations.ListAsync(h.Workspace, Ct));
        Assert.Equal(0, h.Client.StartedAnswers);
        await StorageFaults.AssertIntactAsync(h.Database);
    }

    [Fact]
    public async Task A_summary_save_failing_for_another_reason_fails_the_task_without_pausing_requests()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.SettleAsync(sent.Conversation);

        // A defect, not a storage condition: the insert is rejected as a constraint, which is not a storage failure.
        await h.Database.WriteAsync((c, t) => c.Execute(t, "CREATE TRIGGER reject_versions BEFORE INSERT ON summary_version BEGIN SELECT RAISE(ABORT, 'rejected'); END;"), Ct);

        var outcome = await h.SummaryService.SummarizeAsync(sent.Conversation, Ct);

        Assert.True(outcome.Accepted, outcome.Rejection.ToString());
        await ChatHarness.UntilAsync(async () => (await h.SummaryService.LatestTaskAsync(sent.Conversation, Ct)) is { State: GenerationTaskState.Failed });
        Assert.False(h.Conversations.IsStorageBlocked);
        Assert.Null(await h.SummaryService.ForConversationAsync(sent.Conversation, Ct));
    }

    [Fact]
    public async Task A_summary_version_that_does_not_fit_leaves_no_version_and_pauses_requests_until_space_returns()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.SettleAsync(sent.Conversation);

        // Room for the task row and its updates, not for a long summary (nor for the probe).
        await StorageFaults.FillDiskAsync(h.Database, headroomPages: 8);
        var before = await StorageFaults.CountRowsAsync(h.Database);
        var body = new string('总', 40_000);
        h.Client.Knowledge.Replies.Enqueue($"标题：很长的总结\n\n{body}");

        var outcome = await h.SummaryService.SummarizeAsync(sent.Conversation, Ct);

        Assert.True(outcome.Accepted, outcome.Rejection.ToString());
        await ChatHarness.UntilAsync(async () => (await h.SummaryService.LatestTaskAsync(sent.Conversation, Ct)) is { State: GenerationTaskState.Failed });
        var task = (await h.SummaryService.LatestTaskAsync(sent.Conversation, Ct))!;
        Assert.Equal(GenerationErrorKind.StorageUnavailable, task.Error);
        Assert.True(h.Conversations.IsStorageBlocked);
        var after = await StorageFaults.CountRowsAsync(h.Database);
        Assert.Equal(before["summary_node"], after["summary_node"]);
        Assert.Equal(before["summary_version"], after["summary_version"]);
        Assert.Null(await h.SummaryService.ForConversationAsync(sent.Conversation, Ct));
        await StorageFaults.AssertIntactAsync(h.Database);

        await ChatHarness.StaysAsync(() => h.Conversations.IsStorageBlocked, 300);

        await StorageFaults.FreeDiskAsync(h.Database);
        await ChatHarness.UntilAsync(() => !h.Conversations.IsStorageBlocked);
        h.Client.Knowledge.Replies.Enqueue($"标题：很长的总结\n\n{body}");
        Assert.True((await h.SummaryService.SummarizeAsync(sent.Conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => (await h.SummaryService.ForConversationAsync(sent.Conversation, Ct)) is not null);
        var saved = (await h.SummaryService.ForConversationAsync(sent.Conversation, Ct))!;
        Assert.Equal(body, (await h.SummaryService.GetAsync(saved.Node.Id, Ct))!.Current.Body);
        await StorageFaults.AssertIntactAsync(h.Database);
    }
}
