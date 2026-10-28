using System.Diagnostics;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Faults;

/// <summary>
/// Another program holding the database's write lock longer than the busy timeout while the app runs (architecture
/// §12.1): the write fails as busy, nothing is half written, and nothing is lost once the lock is released.
/// Repeated in the stress workflow (filter <c>Category=FaultInjection</c>).
/// </summary>
[Trait("Category", "FaultInjection")]
public class BusyWriterTests
{
    private static readonly SqliteDatabaseOptions ShortWait = new() { BusyTimeout = TimeSpan.FromMilliseconds(200) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_waits_out_the_busy_timeout_then_fails_as_busy_and_succeeds_after_release()
    {
        using var dir = new TempDataDirectory();
        await using var database = dir.Database(options: ShortWait);
        Assert.True((await database.OpenAsync(Ct)).IsReady);

        using (var holder = StorageFaults.HoldWriteLock(dir.Paths.DatabasePath))
        {
            var clock = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<StorageWriteException>(() => database.WriteAsync(
                (c, t) => c.Execute(t, "INSERT INTO app_state (key, value) VALUES ('k', 'v');"),
                Ct));

            Assert.Equal(StorageFailureKind.Busy, error.Kind);

            // Bounded by the configured wait (plus the driver's retry, rounded up to whole seconds), not the default 5 s.
            Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(4));
            await Assert.ThrowsAsync<StorageWriteException>(() => database.ProbeWriteAsync(Ct));
        }

        await database.WriteAsync((c, t) => c.Execute(t, "INSERT INTO app_state (key, value) VALUES ('k', 'v');"), Ct);
        Assert.Equal("v", await database.ReadAsync(c => c.Scalar<string>(null, "SELECT value FROM app_state WHERE key = 'k';"), Ct));
        await StorageFaults.AssertIntactAsync(database);
    }

    [Fact]
    public async Task An_answer_finishing_while_another_program_holds_the_lock_is_kept_and_saved_after_release()
    {
        await using var h = await ChatHarness.StartAsync(databaseOptions: ShortWait);
        var other = await h.Conversations.StartAsync(h.Workspace, "另一个会话", Ct);
        await h.WaitForAnswerAsync(other.Answer!.Value);
        await h.SettleAsync(other.Conversation);
        var held = h.Client.Hold("说到");
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value, m => m.Text == "说到");

        using (var holder = StorageFaults.HoldWriteLock(h.DatabasePath))
        {
            held.Finish("一半");
            await ChatHarness.UntilAsync(() => h.Conversations.IsStorageBlocked, timeoutMs: 15_000);

            // The finished text stays in memory and readable; new questions are refused, and nothing was half written.
            Assert.Equal("说到一半", h.Conversations.Observe(sent.Answer!.Value)!.Snapshot().Text);
            var refused = await h.Conversations.AskAsync(other.Conversation, "再问", Ct);
            Assert.Equal(SendRejection.StorageUnavailable, refused.Rejection);
            var stored = await h.GetMessageAsync(sent.Answer!.Value);
            Assert.Equal("说到", stored!.Text);
            Assert.Equal(MessageState.Streaming, stored.State);

            // Still held: the retries and the probe keep failing, so requests stay paused.
            await Task.Delay(500, Ct);
            Assert.True(h.Conversations.IsStorageBlocked);
        }

        await ChatHarness.UntilAsync(() => !h.Conversations.IsStorageBlocked, timeoutMs: 15_000);
        var saved = await h.GetMessageAsync(sent.Answer!.Value);
        Assert.Equal(MessageState.Complete, saved!.State);
        Assert.Equal("说到一半", saved.Text);
        Assert.Equal("completed", await h.TaskStateAsync(sent.Answer!.Value));
        var accepted = await h.Conversations.AskAsync(other.Conversation, "再问", Ct);
        Assert.True(accepted.Accepted, accepted.Rejection.ToString());
        await h.WaitForAnswerAsync(accepted.Answer!.Value);
        await StorageFaults.AssertIntactAsync(h.Database);
    }

    [Fact]
    public async Task A_question_sent_while_the_lock_is_held_is_refused_whole_and_can_be_sent_after_release()
    {
        await using var h = await ChatHarness.StartAsync(databaseOptions: ShortWait);
        var first = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await h.WaitForAnswerAsync(first.Answer!.Value);
        await h.SettleAsync(first.Conversation);
        var before = await StorageFaults.CountRowsAsync(h.Database);

        using (var holder = StorageFaults.HoldWriteLock(h.DatabasePath))
        {
            var refused = await h.Conversations.AskAsync(first.Conversation, "锁住时的问题", Ct);
            Assert.Equal(SendRejection.StorageUnavailable, refused.Rejection);
            Assert.True(h.Conversations.IsStorageBlocked);
        }

        Assert.Equal(before, await StorageFaults.CountRowsAsync(h.Database));
        await ChatHarness.UntilAsync(() => !h.Conversations.IsStorageBlocked, timeoutMs: 15_000);
        var accepted = await h.Conversations.AskAsync(first.Conversation, "锁住时的问题", Ct);
        Assert.True(accepted.Accepted, accepted.Rejection.ToString());
        Assert.Equal("答：锁住时的问题", (await h.WaitForAnswerAsync(accepted.Answer!.Value)).Text);
    }
}
