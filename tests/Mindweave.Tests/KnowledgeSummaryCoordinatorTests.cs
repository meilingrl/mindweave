using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Mindweave.Application.Chat;
using Mindweave.Application.Generation;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class KnowledgeSummaryCoordinatorTests
{
    private const string Payload = "{\"Title\":\"Synthetic title\",\"Markdown\":\"# Complete body\\nretained evidence\"}";

    // Break caught: committing before disposal, misclassifying a candidate, or publishing before durability.
    [Fact]
    public async Task Full_exhaustion_and_disposal_commit_current_then_candidate_and_publish_only_durable_results()
    {
        await using var f = await Fixture.Open();
        var events = new System.Collections.Concurrent.ConcurrentQueue<KnowledgeSummaryCommitted>();
        f.Summary.Committed += e => { Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version WHERE id=$id", ("$id", e.Result.VersionId))); events.Enqueue(e); };
        var first = await f.Accept(); var call = await f.Transport.NextAsync();
        call.DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        call.Emit(Payload, "stop"); call.Finish(); await call.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version")); Assert.True(f.Summary.HasActiveOrPendingWork);
        call.DisposeRelease.SetResult(); await f.Wait(() => f.Snapshot(first.Id).Status == GenerationStatus.Completed);
        Assert.Equal(0L, f.Sql("SELECT is_read FROM summary_read_state"));
        var second = await f.Accept(); var next = await f.Transport.NextAsync(); next.Emit(Payload, "stop"); next.Finish();
        await f.Wait(() => f.Snapshot(second.Id).Status == GenerationStatus.Completed);
        Assert.Equal(2L, f.Sql("SELECT count(*) FROM summary_version"));
        await f.Wait(() => events.Count == 2);
        var published = events.ToArray(); Assert.False(published[0].Result.IsCandidate); Assert.True(published[1].Result.IsCandidate);
        Assert.Equal(SummaryOutcomeKind.Blocked, (await f.Summary.AcceptAsync(f.Conversation)).Kind);
    }

    // Break caught: treating stop marker/partial JSON/provider fault as a complete version.
    [Theory]
    [InlineData("truncated")]
    [InlineData("empty")]
    [InlineData("invalid")]
    [InlineData("no-stop")]
    [InlineData("throw-after-stop")]
    [InlineData("dispose-failure")]
    public async Task Noncomplete_stream_creates_no_version(string mode)
    {
        await using var f = await Fixture.Open(); var job = await f.Accept(); var call = await f.Transport.NextAsync();
        if (mode == "dispose-failure") call.DisposeFailure = new IOException("synthetic disposal failure");
        call.Emit(mode == "empty" ? "" : mode == "invalid" ? "{bad}" : Payload, mode == "no-stop" ? null : mode == "truncated" ? "length" : "stop");
        if (mode == "throw-after-stop") call.Fail("SyntheticFailure"); else call.Finish();
        await f.Wait(() => f.Snapshot(job.Id).Status == GenerationStatus.Failed && !f.Snapshot(job.Id).HasUnsavedChanges);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version")); Assert.False(f.Runtime.HasPersistenceFailure);
    }

    // Break caught: refreshing accepted config/range, or letting low-priority Summary start ahead of Reply demand.
    [Fact]
    public async Task Queued_summary_keeps_config_credentials_and_range_while_later_reply_takes_priority()
    {
        await using var f = await Fixture.Open();
        using var blocker = f.Runtime.Scheduler.RegisterLogicalWork(GenerationPriority.Reply);
        var job = await f.Accept();
        Assert.Equal(SummaryOutcomeKind.Blocked, (await f.Summary.AcceptAsync(f.Conversation)).Kind);
        await f.Store.SaveConfigurationAsync(f.Config with { ModelId = "new-model", CredentialReference = "v2", BaseUrl = "https://changed.invalid/v1", SummaryThinking = SummaryThinkingMode.ServiceDefault });
        var reply = await f.Chat.SendAsync(f.Conversation, "later question excluded from summary");
        var replyCall = await f.Transport.NextAsync(); Assert.Equal("later question excluded from summary", replyCall.Request.Messages.Last().Content);
        replyCall.Fail("SyntheticFailure"); await f.ChatWait(() => f.Chat.Snapshots.Single(s => s.TaskId == reply).Status == GenerationStatus.Failed);
        blocker.Dispose(); var summary = await f.Transport.NextAsync();
        Assert.Equal(f.Config, summary.Request.Configuration); Assert.Equal("old-secret", summary.Secret);
        Assert.Equal(ChatRequestPurpose.Standard, summary.Request.Purpose);
        Assert.DoesNotContain("later question excluded", JsonSerializer.Serialize(summary.Request.Messages));
        summary.Emit(Payload, "stop"); summary.Finish(); await f.Wait(() => f.Snapshot(job.Id).Status == GenerationStatus.Completed);
    }

    [Fact]
    public async Task Active_reply_blocks_summary_then_stopped_reply_is_flushed_before_freeze()
    {
        await using var f = await Fixture.Open(); var reply = await f.Chat.SendAsync(f.Conversation, "second"); var call = await f.Transport.NextAsync();
        call.Emit("partial durable source"); await f.ChatWait(() => f.Chat.Snapshots.Single(s => s.TaskId == reply).Body.Length > 0);
        Assert.Equal(SummaryOutcomeKind.Blocked, (await f.Summary.AcceptAsync(f.Conversation)).Kind);
        await f.Chat.StopAsync(f.Conversation);
        var job = await f.Accept(); var summary = await f.Transport.NextAsync();
        Assert.Contains("partial durable source", JsonSerializer.Serialize(summary.Request.Messages));
        Assert.Contains(job.InputPlan.Coverage.OwnMessages, m => m.Status == GenerationStatus.Stopped);
        summary.Emit(Payload, "stop"); summary.Finish(); await f.Wait(() => f.Snapshot(job.Id).Status == GenerationStatus.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_lost_completion_response_retains_exact_result_for_explicit_save_repair(bool lost)
    {
        await using var f = await Fixture.Open(); f.Wrapped.CompletionFault = lost ? "after" : "before";
        var job = await f.Accept(); var call = await f.Transport.NextAsync(); call.Emit(Payload, "stop"); call.Finish();
        await f.Wait(() => f.Runtime.HasPersistenceFailure);
        Assert.Equal("Synthetic title", f.Snapshot(job.Id).RetainedContent!.Title); Assert.True(f.Snapshot(job.Id).HasUnsavedChanges);
        Assert.Equal(lost ? 1L : 0L, f.Sql("SELECT count(*) FROM summary_version"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Summary.AcceptAsync(f.Conversation));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Runtime.ShutdownAsync());
        f.Runtime.ResumeAccepting();
        f.Wrapped.CompletionFault = null; await f.Runtime.RetryPersistenceAsync();
        Assert.Equal(GenerationStatus.Completed, f.Snapshot(job.Id).Status); Assert.False(f.Runtime.HasPersistenceFailure);
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version")); Assert.Equal(1, f.Transport.StartCount);
        Assert.Single(f.Wrapped.Completions.Distinct());
    }

    [Fact]
    public async Task Lost_acceptance_response_reconciles_operation_without_refreezing_later_source()
    {
        await using var f = await Fixture.Open(); f.Wrapped.LoseAcceptance = true; var operation = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => f.Summary.AcceptAsync(f.Conversation, operation));
        Assert.True(f.Runtime.HasPersistenceFailure); Assert.True(f.Summary.HasActiveOrPendingWork);
        await BranchStorageTests.Answer(f.Store, f.Conversation, "later source", "later response");
        await f.Store.SaveConfigurationAsync(f.Config with { ModelId = "changed", CredentialReference = "v2" });
        await f.Runtime.RetryPersistenceAsync(); var call = await f.Transport.NextAsync();
        Assert.Equal(f.Config, call.Request.Configuration); Assert.DoesNotContain("later source", JsonSerializer.Serialize(call.Request.Messages));
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM knowledge_summary_job"));
        call.Emit(Payload, "stop"); call.Finish(); await f.Wait(() => f.Summary.Snapshots.Single().Status == GenerationStatus.Completed);
    }

    // Break caught: Return clearing the cancellation of a committed but unacknowledged acceptance.
    [Fact]
    public async Task Unknown_acceptance_keeps_shutdown_stop_after_failed_lookup_and_return_then_new_operation_runs()
    {
        await using var f = await Fixture.Open(); var operation = Guid.NewGuid();
        f.Wrapped.LoseAcceptance = true;
        await Assert.ThrowsAsync<IOException>(() => f.Summary.AcceptAsync(f.Conversation, operation));
        var durable = (await f.Store.FindKnowledgeSummaryOperationAsync(operation))!;
        Assert.Equal(GenerationStatus.Queued, durable.Status);
        f.Wrapped.FindOperationFault = true;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => f.Runtime.ShutdownAsync());
            Assert.Contains(operation, f.Summary.PendingAcceptanceOperations);
            f.Runtime.ResumeAccepting();
            f.Wrapped.FindOperationFault = false;
            await f.Runtime.RetryPersistenceAsync();
            Assert.Equal(GenerationStatus.Stopped, f.Snapshot(durable.Id).Status);
            Assert.Equal(GenerationStatus.Stopped, (await f.Store.LoadKnowledgeSummaryJobAsync(durable.Id))!.Status);
            Assert.Empty(f.Summary.PendingAcceptanceOperations);
            Assert.Equal(0, f.Transport.StartCount); Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version"));
            var replay = await f.Summary.AcceptAsync(f.Conversation, operation);
            Assert.Equal(SummaryOutcomeKind.NoChange, replay.Kind); Assert.Equal(GenerationStatus.Stopped, replay.Value!.Status);
            Assert.Equal(0, f.Transport.StartCount);
            var next = await f.Accept(); Assert.NotEqual(operation, next.OperationId);
            var call = await f.Transport.NextAsync(); call.Emit(Payload, "stop"); call.Finish();
            await f.Wait(() => f.Snapshot(next.Id).Status == GenerationStatus.Completed);
            Assert.Equal(1, f.Transport.StartCount); Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version"));
        }
        finally { f.Wrapped.FindOperationFault = false; await f.Runtime.ShutdownAsync(); }
    }

    // Break caught: an unresolved operation proved absent after shutdown being accepted anew under its old ID.
    [Fact]
    public async Task Uncommitted_unknown_operation_keeps_stop_tombstone_after_return_and_absent_lookup()
    {
        await using var f = await Fixture.Open(); var operation = Guid.NewGuid(); f.Wrapped.FailAcceptanceBeforeWrite = true;
        await Assert.ThrowsAsync<IOException>(() => f.Summary.AcceptAsync(f.Conversation, operation));
        Assert.Null(await f.Store.FindKnowledgeSummaryOperationAsync(operation));
        f.Wrapped.FindOperationFault = true;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => f.Runtime.ShutdownAsync()); f.Runtime.ResumeAccepting();
            f.Wrapped.FindOperationFault = false; await f.Runtime.RetryPersistenceAsync();
            Assert.Empty(f.Summary.PendingAcceptanceOperations);
            var replay = await f.Summary.AcceptAsync(f.Conversation, operation);
            Assert.Equal(SummaryOutcomeKind.Obsolete, replay.Kind); Assert.Equal("SummaryOperationStopped", replay.Code);
            Assert.Equal(0L, f.Sql("SELECT count(*) FROM knowledge_summary_job")); Assert.Equal(0, f.Transport.StartCount);
            var next = await f.Accept(); var call = await f.Transport.NextAsync(); call.Emit(Payload, "stop"); call.Finish();
            await f.Wait(() => f.Snapshot(next.Id).Status == GenerationStatus.Completed);
            Assert.Equal(1, f.Transport.StartCount);
        }
        finally { f.Wrapped.FindOperationFault = false; await f.Runtime.ShutdownAsync(); }
    }

    [Fact]
    public async Task Stop_is_immediate_but_shutdown_waits_for_uncooperative_disposal_tail()
    {
        await using var f = await Fixture.Open(ignoreCancellation: true); var job = await f.Accept(); var call = await f.Transport.NextAsync();
        call.DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        call.Emit("{\"Title\":\"partial", null);
        await f.Summary.StopAsync(job.Id);
        Assert.Equal(GenerationStatus.Stopped, f.Snapshot(job.Id).Status); Assert.True(call.Cancellation.IsCancellationRequested);
        Assert.True(f.Summary.HasActiveOrPendingWork); Assert.DoesNotContain("partial", (string)f.Sql("SELECT input_plan_json FROM knowledge_summary_job")!);
        var shutdown = f.Runtime.ShutdownAsync(); Assert.False(shutdown.IsCompleted);
        call.Emit(Payload, "stop"); call.Finish(); await call.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(shutdown.IsCompleted); call.DisposeRelease.SetResult(); await shutdown;
        Assert.False(f.Summary.HasActiveOrPendingWork); Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version"));
    }

    [Fact]
    public async Task Stop_racing_atomic_completion_reports_committed_truth()
    {
        await using var f = await Fixture.Open(); f.Wrapped.CompleteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = await f.Accept(); var call = await f.Transport.NextAsync(); call.Emit(Payload, "stop"); call.Finish();
        await f.Wrapped.CompleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = f.Summary.StopAsync(job.Id); Assert.NotEqual(GenerationStatus.Stopped, f.Snapshot(job.Id).Status);
        f.Wrapped.CompleteRelease.SetResult(); await stop;
        Assert.Equal(GenerationStatus.Completed, f.Snapshot(job.Id).Status); Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version"));
    }

    [Fact]
    public async Task Preparation_read_failure_remains_paused_until_explicit_repair_succeeds()
    {
        await using var f = await Fixture.Open(); f.Wrapped.PrepareFault = true;
        var job = await f.Accept(); await f.Wait(() => f.Runtime.HasPersistenceFailure);
        Assert.Equal(0, f.Transport.StartCount);
        await Assert.ThrowsAsync<IOException>(() => f.Runtime.RetryPersistenceAsync());
        Assert.True(f.Runtime.HasPersistenceFailure);
        f.Wrapped.PrepareFault = false; await f.Runtime.RetryPersistenceAsync();
        var call = await f.Transport.NextAsync(); call.Emit(Payload, "stop"); call.Finish();
        await f.Wait(() => f.Snapshot(job.Id).Status == GenerationStatus.Completed);
        Assert.Equal(1, f.Transport.StartCount);
    }

    [Fact]
    public async Task Lost_terminal_metadata_acknowledgement_repairs_without_reopening_or_output()
    {
        await using var f = await Fixture.Open(); var job = await f.Accept(); await f.Transport.NextAsync();
        f.Wrapped.LoseMetadata = true;
        await Assert.ThrowsAsync<IOException>(() => f.Summary.StopAsync(job.Id));
        Assert.Equal(GenerationStatus.Stopped, f.Snapshot(job.Id).Status);
        await f.Runtime.RetryPersistenceAsync();
        Assert.Equal(GenerationStatus.Stopped, f.Snapshot(job.Id).Status); Assert.False(f.Snapshot(job.Id).HasUnsavedChanges);
        Assert.False(f.Runtime.HasPersistenceFailure); Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version"));
    }

    [Fact]
    public async Task Lost_completion_after_output_deleted_reconciles_identity_without_resurrection()
    {
        await using var f = await Fixture.Open(); f.Wrapped.CompletionFault = "after";
        var job = await f.Accept(); var call = await f.Transport.NextAsync(); call.Emit(Payload, "stop"); call.Finish();
        await f.Wait(() => f.Runtime.HasPersistenceFailure);
        var node = (await f.Store.FindConversationSummaryAsync(f.Conversation))!;
        var version = node.State.Node.CurrentVersionId;
        var preview = (await f.Store.PreviewDeleteSummaryVersionsAsync(node.State.Node.Id, [version])).Value!;
        Assert.Equal(SummaryOutcomeKind.Applied, (await f.Store.DeleteSummaryVersionsAsync(preview)).Kind);
        f.Wrapped.CompletionFault = null; await f.Runtime.RetryPersistenceAsync();
        Assert.Equal(version, f.Snapshot(job.Id).Result!.VersionId);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version")); Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_node"));
        Assert.Equal(1, f.Transport.StartCount);
    }

    // Break caught: exposing the workbench before both job families recover, or replaying crashed summaries.
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Session_recovers_chat_and_frozen_summary_before_root_becomes_ready_without_dispatch()
    {
        using var directory = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", directory.Path);
        var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var conversation = (await store.CreateConversationAsync((await store.CreateAsync("recovery")).Id)).Id;
        var config = BranchStorageTests.Config(32768) with { CredentialReference = "v1" }; await store.SaveConfigurationAsync(config);
        var original = await BranchStorageTests.Answer(store, conversation, "parent question", "original parent");
        var child = await store.CreateBranchAsync(conversation, BranchStorageTests.Selection(original), Guid.NewGuid());
        await BranchStorageTests.Answer(store, child.Id, "child own", "child answer");
        var summary = (await store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), child.Id, config)).Value!;
        await store.PrepareKnowledgeSummaryRequestAsync(summary.Id, 0);
        await BranchStorageTests.Answer(store, conversation, null, "later parent retry");
        var reply = await store.AcceptReplyAsync(child.Id, "later child pending", config);
        var transport = new GenerationCoordinatorTests.ControlledTransport();
        Mindweave.Desktop.Services.DesktopChatSession? session = null;
        var root = new Mindweave.Desktop.ViewModels.RootViewModel(() => Task.FromResult<Mindweave.Application.Storage.IWorkspaceStore>(store),
            new Mindweave.Desktop.Services.DesktopActions(paths.DataDirectory, () => { }), paths.DataDirectory,
            s => session = new((IChatStore)s, transport, new GenerationCoordinatorTests.MemoryVault()));
        try
        {
            await root.InitializeAsync(); Assert.IsType<Mindweave.Desktop.ViewModels.MainViewModel>(root.Content);
            Assert.Same(session!.Runtime, session.Summaries.Runtime);
            Assert.Equal(GenerationStatus.Interrupted, Assert.Single(session.Summaries.Snapshots).Status);
            Assert.Equal(GenerationStatus.Interrupted, (await store.LoadGenerationAsync(reply.Task.Id))!.Status);
            Assert.Equal(JsonSerializer.Serialize(summary.InputPlan), JsonSerializer.Serialize((await store.LoadKnowledgeSummaryJobAsync(summary.Id))!.InputPlan));
            Assert.Equal(0, transport.StartCount); Assert.Equal(0L, ContextStorageTests.Sql(paths, "SELECT count(*) FROM summary_version"));
        }
        finally { transport.Close(); await root.CloseAsync(); if (!root.IsClosed) await root.ConfirmCloseCommand.ExecuteAsync(null); await store.DisposeAsync(); }
    }

    // Break caught: page/workspace navigation canceling app-owned work, or failed close disposing the readable store.
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Navigation_does_not_cancel_summary_and_failed_root_close_retains_store_for_save_repair()
    {
        using var directory = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", directory.Path);
        var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var workspace = await store.CreateAsync("owner"); var other = await store.CreateAsync("other");
        var conversation = (await store.CreateConversationAsync(workspace.Id)).Id;
        await BranchStorageTests.Answer(store, conversation, "own", "source");
        await store.SaveConfigurationAsync(BranchStorageTests.Config(32768) with { CredentialReference = "v1" });
        var transport = new GenerationCoordinatorTests.ControlledTransport();
        Mindweave.Desktop.Services.DesktopChatSession? session = null;
        var root = new Mindweave.Desktop.ViewModels.RootViewModel(() => Task.FromResult<Mindweave.Application.Storage.IWorkspaceStore>(store),
            new Mindweave.Desktop.Services.DesktopActions(paths.DataDirectory, () => { }), paths.DataDirectory,
            s => session = new((IChatStore)s, transport, new GenerationCoordinatorTests.MemoryVault()));
        try
        {
            await root.InitializeAsync(); var main = Assert.IsType<Mindweave.Desktop.ViewModels.MainViewModel>(root.Content);
            var accepted = await session!.Summaries.AcceptAsync(conversation); Assert.Equal(SummaryOutcomeKind.Applied, accepted.Kind);
            var call = await transport.NextAsync();
            await main.NavigateAsync(new(Mindweave.Application.Navigation.MainArea.Settings));
            await main.SwitchWorkspaceAsync(other.Id);
            Assert.False(call.Cancellation.IsCancellationRequested);
            ContextStorageTests.Sql(paths, "CREATE TRIGGER synthetic_exit_fault BEFORE INSERT ON summary_version BEGIN SELECT RAISE(ABORT,'synthetic'); END");
            call.Emit(Payload, "stop"); call.Finish();
            await Fixture.WaitFor(() => session!.Runtime.HasPersistenceFailure, h => session.Summaries.Changed += h, h => session.Summaries.Changed -= h);
            await root.CloseAsync(); Assert.True(root.IsCloseConfirmationVisible);
            await root.ConfirmCloseCommand.ExecuteAsync(null); Assert.False(root.IsClosed); Assert.NotNull(root.CloseError);
            Assert.NotNull(await store.LoadConversationAsync(conversation));
            Assert.Equal("Synthetic title", Assert.Single(session.Summaries.Snapshots).RetainedContent!.Title);
            root.ReturnToWorkbenchCommand.Execute(null); Assert.True(session!.Runtime.HasPersistenceFailure);
            ContextStorageTests.Sql(paths, "DROP TRIGGER synthetic_exit_fault"); await root.RetrySaveCommand.ExecuteAsync(null);
            Assert.False(session.Runtime.HasPersistenceFailure); Assert.Equal(1, transport.StartCount);
            await root.CloseAsync(); if (!root.IsClosed) await root.ConfirmCloseCommand.ExecuteAsync(null); Assert.True(root.IsClosed);
        }
        finally { transport.Close(); if (!root.IsClosed) { ContextStorageTests.Sql(paths, "DROP TRIGGER IF EXISTS synthetic_exit_fault"); await root.ConfirmCloseCommand.ExecuteAsync(null); } await store.DisposeAsync(); }
    }

    [Fact]
    public async Task Configuration_read_failure_is_verified_during_repair_before_admission_reopens()
    {
        await using var f = await Fixture.Open();
        f.Sql("ALTER TABLE active_ai_configuration RENAME TO synthetic_unavailable_configuration");
        try
        {
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => f.Summary.AcceptAsync(f.Conversation));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => f.Runtime.RetryPersistenceAsync());
            Assert.True(f.Runtime.HasPersistenceFailure); Assert.Equal(0, f.Transport.StartCount);
        }
        finally { f.Sql("ALTER TABLE synthetic_unavailable_configuration RENAME TO active_ai_configuration"); }
        await f.Runtime.RetryPersistenceAsync(); Assert.False(f.Runtime.HasPersistenceFailure);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly TestDirectory directory = new();
        public AppPaths Paths { get; }
        public SqliteWorkspaceStore Store { get; private set; } = null!;
        public AppGenerationRuntime Runtime { get; } = new();
        public GenerationCoordinatorTests.ControlledTransport Transport { get; }
        public GenerationCoordinatorTests.MemoryVault Vault { get; } = new();
        public GenerationCoordinator Chat { get; private set; } = null!;
        public KnowledgeSummaryCoordinator Summary { get; private set; } = null!;
        public FaultStore Wrapped { get; private set; } = null!;
        public AiConfiguration Config { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", "https://api.deepseek.com/v1", "v1", "old-model", 32768, 2048, SummaryThinking: SummaryThinkingMode.Disabled);
        public Guid Conversation { get; private set; }
        private Fixture(bool ignoreCancellation) { Paths = AppPaths.ForTrack("codex-steady", directory.Path); Transport = new(ignoreCancellation); }
        public static async Task<Fixture> Open(bool ignoreCancellation = false)
        {
            var f = new Fixture(ignoreCancellation); f.Store = await SqliteWorkspaceStore.OpenAsync(f.Paths);
            await f.Store.SaveConfigurationAsync(f.Config);
            f.Conversation = (await f.Store.CreateConversationAsync((await f.Store.CreateAsync("Synthetic summary coordinator")).Id)).Id;
            await BranchStorageTests.Answer(f.Store, f.Conversation, "own question", "own answer");
            f.Chat = new(f.Store, f.Transport, f.Vault, f.Runtime); f.Wrapped = new(f.Store);
            f.Summary = new(f.Wrapped, f.Store, f.Chat, f.Transport, f.Vault, f.Runtime);
            await f.Chat.InitializeAsync(); await f.Summary.InitializeAsync(); return f;
        }
        public async Task<KnowledgeSummaryJob> Accept() { var outcome = await Summary.AcceptAsync(Conversation); Assert.Equal(SummaryOutcomeKind.Applied, outcome.Kind); return outcome.Value!; }
        public KnowledgeSummarySnapshot Snapshot(Guid taskId) => Summary.Snapshots.Single(s => s.TaskId == taskId);
        public object? Sql(string sql, params (string, object?)[] args) => ContextStorageTests.Sql(Paths, sql, args);
        public Task ChatWait(Func<bool> condition) => WaitFor(condition, h => Chat.Changed += h, h => Chat.Changed -= h);
        public Task Wait(Func<bool> condition) => WaitFor(condition, h => Summary.Changed += h, h => Summary.Changed -= h);
        internal static async Task WaitFor(Func<bool> condition, Action<Action> subscribe, Action<Action> unsubscribe)
        {
            var signals = Channel.CreateUnbounded<bool>(); void Changed() => signals.Writer.TryWrite(true); subscribe(Changed);
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await signals.Reader.ReadAsync(timeout.Token); }
            finally { unsubscribe(Changed); }
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Close(); Wrapped.CompleteRelease?.TrySetResult(); Wrapped.CompletionFault = null; Wrapped.PrepareFault = false;
            await Runtime.RetryPersistenceAsync(); await Summary.DisposeAsync(); await Chat.DisposeAsync(); await Runtime.DisposeAsync(); await Store.DisposeAsync(); directory.Dispose();
        }
    }

    internal sealed class FaultStore(IKnowledgeSummaryStore inner) : IKnowledgeSummaryStore
    {
        public string? CompletionFault;
        public bool LoseAcceptance, LoseMetadata, PrepareFault, FindOperationFault, FailAcceptanceBeforeWrite;
        public List<SummaryGenerationCompletion> Completions { get; } = [];
        public TaskCompletionSource CompleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? CompleteRelease;
        public async Task<SummaryOutcome<KnowledgeSummaryJob>> AcceptKnowledgeSummaryAsync(Guid operationId, Guid conversationId, AiConfiguration configuration)
        { if (FailAcceptanceBeforeWrite) { FailAcceptanceBeforeWrite = false; throw new IOException("synthetic acceptance failure before commit"); } var result = await inner.AcceptKnowledgeSummaryAsync(operationId, conversationId, configuration); if (LoseAcceptance) { LoseAcceptance = false; throw new IOException("synthetic lost acceptance"); } return result; }
        public async Task<SummaryOutcome<SummaryGenerationResult>> CompleteKnowledgeSummaryAsync(SummaryGenerationCompletion completion)
        {
            Completions.Add(completion); if (CompletionFault == "before") throw new IOException("synthetic write failure");
            var result = await inner.CompleteKnowledgeSummaryAsync(completion); CompleteEntered.TrySetResult(); if (CompleteRelease is not null) await CompleteRelease.Task;
            if (CompletionFault == "after") throw new IOException("synthetic lost completion"); return result;
        }
        public Task<KnowledgeSummaryJob?> LoadKnowledgeSummaryJobAsync(Guid id) => inner.LoadKnowledgeSummaryJobAsync(id);
        public Task<KnowledgeSummaryJob?> FindKnowledgeSummaryOperationAsync(Guid id) => FindOperationFault ? Task.FromException<KnowledgeSummaryJob?>(new IOException("synthetic operation lookup failure")) : inner.FindKnowledgeSummaryOperationAsync(id);
        public Task<ImmutableArray<KnowledgeSummaryJob>> ListActiveKnowledgeSummaryJobsAsync() => inner.ListActiveKnowledgeSummaryJobsAsync();
        public Task<SummaryOutcome<PreparedKnowledgeSummaryRequest>> PrepareKnowledgeSummaryRequestAsync(Guid id, long revision) => PrepareFault ? Task.FromException<SummaryOutcome<PreparedKnowledgeSummaryRequest>>(new IOException("synthetic preparation read")) : inner.PrepareKnowledgeSummaryRequestAsync(id, revision);
        public Task<PreparedKnowledgeSummaryRequest?> LoadPreparedKnowledgeSummaryRequestAsync(Guid id) => inner.LoadPreparedKnowledgeSummaryRequestAsync(id);
        public async Task<SummaryOutcome<KnowledgeSummaryJob>> UpdateKnowledgeSummaryMetadataAsync(SummaryGenerationMetadata update)
        { var result = await inner.UpdateKnowledgeSummaryMetadataAsync(update); if (LoseMetadata) { LoseMetadata = false; throw new IOException("synthetic lost metadata"); } return result; }
        public Task RecoverInterruptedKnowledgeSummariesAsync() => inner.RecoverInterruptedKnowledgeSummariesAsync();
    }
}
