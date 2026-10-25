using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class GenerationCoordinatorTests
{
    // Break caught: dispatching a third physical stream, accepting a duplicate, or releasing a stopped stream early.
    [Fact]
    public async Task Two_slots_queue_third_and_stop_freezes_body_until_uncooperative_stream_disposes()
    {
        await using var f = await Fixture.OpenAsync(ignoreCancellation: true);
        var a = await f.ConversationAsync();
        var b = await f.ConversationAsync();
        var c = await f.ConversationAsync();
        var firstId = await f.Coordinator.SendAsync(a.Id, "one");
        var first = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(b.Id, "two");
        var second = await f.Transport.NextAsync();
        var thirdId = await f.Coordinator.SendAsync(c.Id, "three");
        Assert.Equal(GenerationStatus.Queued, f.Snapshot(thirdId).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.SendAsync(a.Id, "duplicate"));
        Assert.Equal(2, (await f.Chat.ReadMessagesAsync(a.Id)).Count);
        first.Emit("partial");
        await f.WaitAsync(() => f.Snapshot(firstId).Body == "partial");
        await f.Coordinator.StopAsync(a.Id);
        var stopped = f.Snapshot(firstId);
        Assert.Equal(GenerationStatus.Stopped, stopped.Status);
        Assert.Equal("partial", stopped.Body);
        Assert.True(first.Cancellation.IsCancellationRequested);
        first.Emit("late");
        // Finishing another stream proves the late event was processed while the stopped stream still owns its slot.
        second.Fail("Timeout");
        var third = await f.Transport.NextAsync();
        Assert.Equal("three", third.Request.Messages.Last().Content);
        first.Finish();
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(stopped, f.Snapshot(firstId));
        Assert.Equal(2, f.Transport.PeakActive);
        var answer = (await f.Chat.ReadMessagesAsync(a.Id)).Last();
        Assert.Equal("partial", answer.Body);
        Assert.Equal(GenerationStatus.Stopped, answer.Status);
    }

    [Fact]
    public async Task Queued_cancel_never_dispatches_and_frozen_configuration_uses_old_credential()
    {
        await using var f = await Fixture.OpenAsync();
        var a = await f.ConversationAsync();
        var b = await f.ConversationAsync();
        var c = await f.ConversationAsync();
        var d = await f.ConversationAsync();
        await f.Coordinator.SendAsync(a.Id, "a");
        var first = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(b.Id, "b");
        await f.Transport.NextAsync();
        var cancelled = await f.Coordinator.SendAsync(c.Id, "cancel me");
        var frozen = await f.Coordinator.SendAsync(d.Id, "old config");
        await f.Coordinator.StopAsync(c.Id);
        await f.Chat.SaveConfigurationAsync(f.Configuration with { ModelId = "new-model", CredentialReference = "v2" });
        first.Fail("Timeout");
        var next = await f.Transport.NextAsync();
        Assert.Equal("old config", next.Request.Messages.Last().Content);
        Assert.Equal(f.Configuration, next.Request.Configuration);
        Assert.Equal("old-secret", next.Secret);
        Assert.Equal(GenerationStatus.Stopped, f.Snapshot(cancelled).Status);
        Assert.Equal(GenerationStatus.Running, f.Snapshot(frozen).Status);
    }

    [Fact]
    public async Task Save_failure_keeps_latest_body_pauses_dispatch_and_explicit_retry_persists_terminal_state()
    {
        await using var f = await Fixture.OpenAsync();
        var a = await f.ConversationAsync();
        var b = await f.ConversationAsync();
        var id = await f.Coordinator.SendAsync(a.Id, "q");
        var call = await f.Transport.NextAsync();
        f.Sql("CREATE TRIGGER fail_update BEFORE UPDATE ON generation_task BEGIN SELECT RAISE(ABORT,'fixture'); END");
        call.Emit("retained");
        call.Fail("Timeout");
        await f.WaitAsync(() => f.Coordinator.HasPersistenceFailure);
        var memory = f.Snapshot(id);
        Assert.Equal("retained", memory.Body);
        Assert.Equal(GenerationStatus.Failed, memory.Status);
        Assert.Equal("Timeout", memory.Error);
        Assert.True(memory.HasUnsavedChanges);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.SendAsync(b.Id, "must remain input"));
        Assert.Empty(await f.Chat.ReadMessagesAsync(b.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator.ShutdownAsync());
        f.Sql("DROP TRIGGER fail_update");
        await f.Coordinator.RetryPersistenceAsync();
        Assert.False(f.Coordinator.HasPersistenceFailure);
        Assert.False(f.Snapshot(id).HasUnsavedChanges);
        var answer = (await f.Chat.ReadMessagesAsync(a.Id)).Last();
        Assert.Equal("retained", answer.Body);
        Assert.Equal(GenerationStatus.Failed, answer.Status);
        f.Coordinator.ResumeAccepting();
        await f.Coordinator.SendAsync(b.Id, "now accepted");
        await f.Transport.NextAsync();
    }

    [Fact]
    public async Task Finish_event_is_not_completion_until_enumeration_succeeds_and_title_never_overwrites_manual_name()
    {
        await using var f = await Fixture.OpenAsync();
        var a = await f.ConversationAsync();
        var id = await f.Coordinator.SendAsync(a.Id, "q");
        var reply = await f.Transport.NextAsync();
        reply.Emit("answer", "stop");
        await f.WaitAsync(() => f.Snapshot(id).Body == "answer");
        Assert.Equal(GenerationStatus.Running, f.Snapshot(id).Status);
        reply.Finish();
        var title = await f.Transport.NextAsync();
        Assert.Equal("answer", title.Request.Messages.Last().Content);
        Assert.True(title.Request.Configuration.MaxOutputTokens <= 128);
        await f.Chat.RenameConversationAsync(a.Id, "manual");
        title.Emit("automatic", "stop");
        title.Finish();
        await f.WaitAsync(() => f.Coordinator.Snapshots.Any(s => s.Kind == GenerationKind.Title && s.Status == GenerationStatus.Stopped && !s.HasUnsavedChanges));
        Assert.Equal("manual", (await f.Chat.LoadConversationAsync(a.Id))!.Title);
        Assert.Equal(GenerationStatus.Completed, f.Snapshot(id).Status);
    }

    [Fact]
    public async Task Budget_refusal_precedes_accept_preserves_draft_and_does_not_count_superseded_attempts()
    {
        await using var f = await Fixture.OpenAsync();
        var a = await f.ConversationAsync();
        await f.Chat.SaveConfigurationAsync(f.Configuration with { ContextTokenBudget = 4096, MaxOutputTokens = 128 });
        var tooLarge = new string('界', 2000);
        await f.Chat.SaveDraftAsync(a.Id, tooLarge, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.SendAsync(a.Id, tooLarge));
        Assert.Equal(tooLarge, (await f.Chat.LoadDraftAsync(a.Id)).Text);
        Assert.Empty(await f.Chat.ReadMessagesAsync(a.Id));
        var old = await f.Chat.AcceptReplyAsync(a.Id, "q", f.Configuration);
        await f.Chat.WriteGenerationAsync(old.Task.Id, 1, new string('x', 6000), GenerationStatus.Failed);
        var latest = await f.Chat.AcceptReplyAsync(a.Id, null, f.Configuration);
        await f.Chat.WriteGenerationAsync(latest.Task.Id, 1, "small", GenerationStatus.Failed);
        var id = await f.Coordinator.SendAsync(a.Id, "next");
        var request = await f.Transport.NextAsync();
        Assert.Equal(new[] { "q", "small", "next" }, request.Request.Messages.Select(m => m.Content));
        Assert.Equal(GenerationStatus.Running, f.Snapshot(id).Status);
    }

    [Fact]
    public async Task Configuration_new_reference_rolls_back_on_sqlite_failure_and_discovery_never_generates()
    {
        await using var f = await Fixture.OpenAsync();
        var service = new ChatConfigurationService(f.Chat, f.Vault, f.Transport);
        f.Sql("CREATE TRIGGER reject_configuration BEFORE UPDATE ON active_ai_configuration BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await Assert.ThrowsAsync<SqliteException>(() => service.SaveAsync(f.Configuration, "replacement"));
        Assert.Equal(f.Configuration, await f.Chat.LoadActiveConfigurationAsync());
        Assert.Equal(new[] { "v1", "v2" }, f.Vault.Secrets.Keys.Order());
        f.Sql("DROP TRIGGER reject_configuration");
        await service.SaveAsync(f.Configuration, "replacement");
        var saved = (await f.Chat.LoadActiveConfigurationAsync())!;
        Assert.Equal("replacement", f.Vault.Secrets[saved.CredentialReference]);
        Assert.Equal("old-secret", f.Vault.Secrets["v1"]);
        Assert.Equal(new[] { "model-a" }, await service.DiscoverModelsAsync(saved, "unsaved", default));
        Assert.Equal("unsaved", f.Transport.DiscoverySecret);
        Assert.Empty(f.Coordinator.Snapshots);
    }

    [Fact]
    public async Task Reply_priority_overtakes_queued_title_without_waiting_on_its_own_slot()
    {
        await using var f = await Fixture.OpenAsync();
        var conversations = new List<Conversation>();
        for (var i = 0; i < 4; i++) conversations.Add(await f.ConversationAsync());
        await f.Coordinator.SendAsync(conversations[0].Id, "first");
        var first = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(conversations[1].Id, "second");
        var second = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(conversations[2].Id, "third");
        first.Emit("answer", "stop"); first.Finish();
        var third = await f.Transport.NextAsync();
        Assert.Equal("third", third.Request.Messages.Last().Content);
        await f.WaitAsync(() => f.Coordinator.Snapshots.Any(s => s.Kind == GenerationKind.Title));
        var titleId = f.Coordinator.Snapshots.Single(s => s.Kind == GenerationKind.Title).TaskId;
        Assert.Equal(GenerationStatus.Queued, f.Snapshot(titleId).Status);
        await f.Coordinator.SendAsync(conversations[3].Id, "fourth");
        second.Fail("Timeout");
        Assert.Equal("fourth", (await f.Transport.NextAsync()).Request.Messages.Last().Content);
        Assert.Equal(GenerationStatus.Queued, f.Snapshot(titleId).Status);
        third.Fail("Timeout");
        var title = await f.Transport.NextAsync();
        Assert.Equal("system", title.Request.Messages[0].Role);
        Assert.Equal("answer", title.Request.Messages.Last().Content);
        Assert.Equal(2, f.Transport.PeakActive);
        title.Emit("  title  ", "stop"); title.Finish();
        await f.WaitAsync(() => f.Snapshot(titleId).Status == GenerationStatus.Completed && !f.Snapshot(titleId).HasUnsavedChanges);
        Assert.Equal("title", (await f.Chat.LoadConversationAsync(conversations[0].Id))!.Title);
    }

    [Theory]
    [InlineData("timeout", "Timeout")]
    [InlineData("eof", "IncompleteResponse")]
    [InlineData("length", "OutputTruncated")]
    [InlineData("empty", "EmptyResponse")]
    public async Task Failure_classification_preserves_partial_text_and_does_not_schedule_title(string mode, string expected)
    {
        await using var f = await Fixture.OpenAsync();
        var conversation = await f.ConversationAsync();
        var id = await f.Coordinator.SendAsync(conversation.Id, "q");
        var call = await f.Transport.NextAsync();
        call.Events.Writer.TryWrite(new(mode == "empty" ? "" : "partial", mode is "empty" or "timeout" ? "stop" : mode == "length" ? "length" : null, "actual-model"));
        if (mode == "timeout") call.Fail("Timeout");
        else if (mode == "length") call.Fail("OutputTruncated");
        else call.Finish();
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Failed && !f.Snapshot(id).HasUnsavedChanges);
        var record = (await f.Chat.LoadGenerationAsync(id))!;
        Assert.Equal(expected, record.ErrorCode);
        Assert.Equal("actual-model", record.ActualModelId);
        Assert.Equal(mode == "empty" ? "" : "partial", (await f.Chat.ReadMessagesAsync(conversation.Id)).Last().Body);
        Assert.DoesNotContain(f.Coordinator.Snapshots, s => s.Kind == GenerationKind.Title);
    }

    [Fact]
    public async Task Retry_keeps_old_attempt_and_freezes_only_latest_user_context()
    {
        await using var f = await Fixture.OpenAsync();
        var conversation = await f.ConversationAsync();
        var id = await f.Coordinator.SendAsync(conversation.Id, "original question");
        var call = await f.Transport.NextAsync();
        call.Emit("old answer"); call.Fail("Timeout");
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Failed && !f.Snapshot(id).HasUnsavedChanges);
        var retry = await f.Coordinator.RetryAsync(conversation.Id);
        var retried = await f.Transport.NextAsync();
        Assert.Equal("original question", Assert.Single(retried.Request.Messages).Content);
        var messages = await f.Chat.ReadMessagesAsync(conversation.Id);
        Assert.Equal(3, messages.Count);
        Assert.Equal("old answer", messages[1].Body);
        Assert.Equal(messages[1].Id, messages[2].SupersedesMessageId);
        Assert.NotEqual(id, retry);
    }

    [Fact]
    public async Task Checkpoint_failure_does_not_block_memory_and_keeps_queued_work_paused_until_retry()
    {
        await using var f = await Fixture.OpenAsync();
        var a = await f.ConversationAsync(); var b = await f.ConversationAsync(); var c = await f.ConversationAsync();
        var id = await f.Coordinator.SendAsync(a.Id, "a");
        var first = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(b.Id, "b");
        await f.Transport.NextAsync();
        var queued = await f.Coordinator.SendAsync(c.Id, "c");
        f.Sql("CREATE TRIGGER fail_checkpoint BEFORE UPDATE ON generation_task BEGIN SELECT RAISE(ABORT,'fixture'); END");
        first.Emit(new string('x', 4096));
        await f.WaitAsync(() => f.Coordinator.HasPersistenceFailure);
        first.Emit("latest"); first.Fail("Timeout");
        await f.WaitAsync(() => f.Snapshot(id).Status == GenerationStatus.Failed);
        Assert.Equal(new string('x', 4096) + "latest", f.Snapshot(id).Body);
        Assert.Equal(GenerationStatus.Queued, f.Snapshot(queued).Status);
        f.Sql("DROP TRIGGER fail_checkpoint");
        await f.Coordinator.RetryPersistenceAsync();
        Assert.Equal("c", (await f.Transport.NextAsync()).Request.Messages.Last().Content);
        Assert.Equal(f.Snapshot(id).Body, (await f.Chat.ReadMessagesAsync(a.Id)).Last().Body);
    }

    [Fact]
    public async Task Failed_exit_stops_all_jobs_and_return_never_resurrects_cancelled_work()
    {
        await using var f = await Fixture.OpenAsync();
        var a = await f.ConversationAsync(); var b = await f.ConversationAsync(); var c = await f.ConversationAsync();
        await f.Coordinator.SendAsync(a.Id, "a"); await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(b.Id, "b"); await f.Transport.NextAsync();
        var queued = await f.Coordinator.SendAsync(c.Id, "never dispatch");
        f.Sql("CREATE TRIGGER fail_exit BEFORE UPDATE ON generation_task BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator.ShutdownAsync());
        Assert.All(f.Coordinator.Snapshots, s => Assert.Equal(GenerationStatus.Stopped, s.Status));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.SendAsync(c.Id, "closed"));
        f.Sql("DROP TRIGGER fail_exit");
        await f.Coordinator.RetryPersistenceAsync();
        f.Coordinator.ResumeAccepting();
        await f.Coordinator.SendAsync(c.Id, "fresh");
        Assert.Equal("fresh", (await f.Transport.NextAsync()).Request.Messages.Last().Content);
        Assert.Equal(GenerationStatus.Stopped, f.Snapshot(queued).Status);
    }

    [Fact]
    public async Task Killed_process_recovers_real_checkpoint_as_interrupted_without_resending()
    {
        using var directory = new TestDirectory();
        using var child = ProcessProbe.Start("generation-checkpoint", directory.Path);
        var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.StartsWith("CHECKPOINT:", line);
        var ids = line!.Split(':');
        var conversation = Guid.Parse(ids[1]); var task = Guid.Parse(ids[2]);
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", directory.Path));
        var transport = new ControlledTransport();
        await using var coordinator = new GenerationCoordinator(store, transport, new MemoryVault());
        await coordinator.InitializeAsync();
        var snapshot = Assert.Single(coordinator.Snapshots);
        Assert.Equal(task, snapshot.TaskId);
        Assert.Equal(GenerationStatus.Interrupted, snapshot.Status);
        Assert.Equal("saved checkpoint", snapshot.Body);
        Assert.Equal(GenerationStatus.Interrupted, (await store.ReadMessagesAsync(conversation)).Last().Status);
        Assert.Empty(await store.ListActiveGenerationsAsync());
        Assert.False(coordinator.HasActiveWork);
        Assert.Equal(0, transport.StartCount);
        await coordinator.InitializeAsync();
        Assert.Single(coordinator.Snapshots);
    }

    [Fact]
    public async Task Writer_lock_does_not_block_stream_text_and_small_text_gets_periodic_checkpoint()
    {
        await using var f = await Fixture.OpenAsync();
        var conversation = await f.ConversationAsync();
        var id = await f.Coordinator.SendAsync(conversation.Id, "q");
        var call = await f.Transport.NextAsync();
        using (var blocker = new SqliteConnection($"Data Source={f.Paths.DatabasePath};Pooling=False"))
        {
            blocker.Open();
            using var transaction = blocker.BeginTransaction();
            // A real competing writer holds SQLite, while generation deltas must still reach memory.
            call.Emit(new string('x', 4096));
            await f.WaitAsync(() => f.Snapshot(id).Body.Length == 4096);
            call.Emit("newer");
            await f.WaitAsync(() => f.Snapshot(id).Body.EndsWith("newer", StringComparison.Ordinal));
            Assert.True(f.Snapshot(id).HasUnsavedChanges);
            transaction.Commit();
        }
        await f.WaitAsync(() => !f.Snapshot(id).HasUnsavedChanges);
        Assert.Equal(new string('x', 4096) + "newer", (await f.Chat.ReadMessagesAsync(conversation.Id)).Last().Body);
        call.Emit("small");
        await f.WaitAsync(() => !f.Snapshot(id).HasUnsavedChanges && f.Snapshot(id).Body.EndsWith("small", StringComparison.Ordinal));
        Assert.EndsWith("small", (await f.Chat.ReadMessagesAsync(conversation.Id)).Last().Body);
    }

    [Fact]
    public async Task Initial_running_save_failure_never_calls_transport_and_retry_dispatches_once()
    {
        await using var f = await Fixture.OpenAsync();
        var conversation = await f.ConversationAsync();
        f.Sql("CREATE TRIGGER fail_running BEFORE UPDATE ON generation_task BEGIN SELECT RAISE(ABORT,'fixture'); END");
        var id = await f.Coordinator.SendAsync(conversation.Id, "q");
        await f.WaitAsync(() => f.Coordinator.HasPersistenceFailure);
        Assert.Equal(0, f.Transport.StartCount);
        Assert.Equal(GenerationStatus.Queued, (await f.Chat.LoadGenerationAsync(id))!.Status);
        f.Sql("DROP TRIGGER fail_running");
        await f.Coordinator.RetryPersistenceAsync();
        await f.Transport.NextAsync();
        Assert.Equal(1, f.Transport.StartCount);
        Assert.Equal(GenerationStatus.Running, f.Snapshot(id).Status);
    }

    [Fact]
    public async Task Acceptance_failure_preserves_input_then_explicit_retry_allows_new_acceptance()
    {
        await using var f = await Fixture.OpenAsync();
        var conversation = await f.ConversationAsync();
        await f.Chat.SaveDraftAsync(conversation.Id, "preserved", 1);
        f.Sql("CREATE TRIGGER fail_accept BEFORE INSERT ON generation_task BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await Assert.ThrowsAsync<SqliteException>(() => f.Coordinator.SendAsync(conversation.Id, "preserved"));
        Assert.True(f.Coordinator.HasPersistenceFailure);
        Assert.Equal("preserved", (await f.Chat.LoadDraftAsync(conversation.Id)).Text);
        Assert.Empty(await f.Chat.ReadMessagesAsync(conversation.Id));
        Assert.Empty(f.Coordinator.Snapshots);
        f.Sql("DROP TRIGGER fail_accept");
        await f.Coordinator.RetryPersistenceAsync();
        await f.Coordinator.SendAsync(conversation.Id, "preserved");
        await f.Transport.NextAsync();
        Assert.Equal("", (await f.Chat.LoadDraftAsync(conversation.Id)).Text);
    }

    [Fact]
    public async Task Title_failure_never_saves_partial_title_or_changes_completed_answer()
    {
        await using var f = await Fixture.OpenAsync();
        var conversation = await f.ConversationAsync();
        var replyId = await f.Coordinator.SendAsync(conversation.Id, "q");
        var reply = await f.Transport.NextAsync();
        reply.Emit("good answer", "stop"); reply.Finish();
        var title = await f.Transport.NextAsync();
        var titleId = f.Coordinator.Snapshots.Single(s => s.Kind == GenerationKind.Title).TaskId;
        title.Emit("partial title"); title.Fail("Timeout");
        await f.WaitAsync(() => f.Snapshot(titleId).Status == GenerationStatus.Failed && !f.Snapshot(titleId).HasUnsavedChanges);
        Assert.Equal("", f.Snapshot(titleId).Body);
        Assert.Equal("新会话", (await f.Chat.LoadConversationAsync(conversation.Id))!.Title);
        Assert.Equal(GenerationStatus.Completed, f.Snapshot(replyId).Status);
        Assert.Equal("good answer", (await f.Chat.ReadMessagesAsync(conversation.Id)).Last().Body);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly TestDirectory directory;
        public AppPaths Paths { get; }
        public SqliteWorkspaceStore Store { get; }
        public IChatStore Chat => Store;
        public ControlledTransport Transport { get; }
        public MemoryVault Vault { get; } = new();
        public GenerationCoordinator Coordinator { get; }
        public AiConfiguration Configuration { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "test", "https://example.invalid/v1", "v1", "model-a", 32768, 2048);
        private Fixture(TestDirectory directory, AppPaths paths, SqliteWorkspaceStore store, bool ignoreCancellation)
        {
            this.directory = directory;
            Paths = paths;
            Store = store;
            Transport = new(ignoreCancellation);
            Coordinator = new(Chat, Transport, Vault);
        }
        public static async Task<Fixture> OpenAsync(bool ignoreCancellation = false)
        {
            var dir = new TestDirectory();
            var paths = AppPaths.ForTrack("codex-steady", dir.Path);
            var f = new Fixture(dir, paths, await SqliteWorkspaceStore.OpenAsync(paths), ignoreCancellation);
            await f.Chat.SaveConfigurationAsync(f.Configuration);
            await f.Coordinator.InitializeAsync();
            return f;
        }
        public async Task<Conversation> ConversationAsync() => await Chat.CreateConversationAsync((await Store.CreateAsync("test")).Id);
        public GenerationSnapshot Snapshot(Guid id) => Coordinator.Snapshots.Single(s => s.TaskId == id);
        public async Task WaitAsync(Func<bool> condition)
        {
            var wake = Channel.CreateUnbounded<bool>();
            void Changed() => wake.Writer.TryWrite(true);
            Coordinator.Changed += Changed;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!condition()) await wake.Reader.ReadAsync(timeout.Token);
            }
            finally { Coordinator.Changed -= Changed; }
        }
        public void Sql(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Paths.DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Close();
            await Coordinator.DisposeAsync();
            await Store.DisposeAsync();
            directory.Dispose();
        }
    }

    internal sealed class MemoryVault : ICredentialVault
    {
        public Dictionary<string, string> Secrets { get; } = new() { ["v1"] = "old-secret", ["v2"] = "new-secret" };
        public Task<string> WriteNewAsync(string secret) { var key = Guid.NewGuid().ToString(); lock (Secrets) Secrets[key] = secret; return Task.FromResult(key); }
        public Task<string> ReadAsync(string reference) { lock (Secrets) return Task.FromResult(Secrets[reference]); }
        public Task DeleteAsync(string reference) { lock (Secrets) Secrets.Remove(reference); return Task.CompletedTask; }
    }

    internal sealed class ControlledTransport(bool ignoreCancellation = false) : IChatTransport
    {
        private readonly Channel<Call> calls = Channel.CreateUnbounded<Call>();
        private readonly List<Call> all = [];
        private int active;
        public int PeakActive { get; private set; }
        public int StartCount { get { lock (all) return all.Count; } }
        public string? DiscoverySecret { get; private set; }
        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string secret, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var call = new Call(request, secret, cancellationToken);
            lock (all) { all.Add(call); PeakActive = Math.Max(PeakActive, ++active); }
            calls.Writer.TryWrite(call);
            try
            {
                await foreach (var item in call.Events.Reader.ReadAllAsync(ignoreCancellation ? default : cancellationToken)) yield return item;
            }
            finally { lock (all) active--; call.Disposed.TrySetResult(); }
        }
        public Task<Call> NextAsync() => calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public Task<IReadOnlyList<string>> ListModelsAsync(AiConfiguration configuration, string secret, CancellationToken cancellationToken)
        { DiscoverySecret = secret; return Task.FromResult<IReadOnlyList<string>>(["model-a"]); }
        public void Close() { lock (all) foreach (var call in all) call.Finish(); }
    }

    internal sealed class Call(ChatRequest request, string secret, CancellationToken cancellation)
    {
        public ChatRequest Request { get; } = request;
        public string Secret { get; } = secret;
        public CancellationToken Cancellation { get; } = cancellation;
        public Channel<ChatStreamEvent> Events { get; } = Channel.CreateUnbounded<ChatStreamEvent>();
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(string text, string? finish = null) => Events.Writer.TryWrite(new(text, finish));
        public void Finish() => Events.Writer.TryComplete();
        public void Fail(string code) => Events.Writer.TryComplete(new ChatTransportException(code));
    }
}
