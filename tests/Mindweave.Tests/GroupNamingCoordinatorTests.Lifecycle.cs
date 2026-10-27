using System.Collections.Immutable;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
using Xunit;
namespace Mindweave.Tests;

public sealed partial class GroupNamingCoordinatorTests
{
    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task Completed_output_repair_retries_exact_commit_after_failed_close_and_return(string fault)
    {
        await using var f = await Fixture.Open(); f.Wrapped.CompletionFault = fault;
        await f.Naming.NotifyCurrentChangedAsync(f.Data.Node); var call = await f.Transport.NextAsync(); call.Emit(f.Payload(), "stop"); call.Finish();
        await f.Wait(() => f.Runtime.HasPersistenceFailure); Assert.True(f.Naming.Snapshots.Single().HasUnsavedChanges);
        await Assert.ThrowsAsync<IOException>(() => f.Runtime.ShutdownAsync()); f.Runtime.ResumeAccepting();
        f.Wrapped.CompletionFault = null; await f.Runtime.RetryPersistenceAsync(); await f.Wait(() => !f.Naming.HasActiveOrPendingWork);
        Assert.Equal("First－one", (await f.Data.Snapshot()).State.Node.DisplayName); Assert.Equal(1, f.Transport.StartCount);
        Assert.Single(f.Wrapped.Completions.Select(c => System.Text.Json.JsonSerializer.Serialize(c)).Distinct());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_acceptance_keeps_stop_intent_across_failed_lookup_return_and_repair(bool before)
    {
        await using var f = await Fixture.Open(); f.Wrapped.AcceptFault = before ? "before" : "after";
        await Assert.ThrowsAsync<IOException>(() => f.Naming.NotifyCurrentChangedAsync(f.Data.Node)); f.Wrapped.FindFault = true;
        await Assert.ThrowsAsync<IOException>(() => f.Runtime.ShutdownAsync()); f.Runtime.ResumeAccepting();
        f.Wrapped.FindFault = false; await f.Runtime.RetryPersistenceAsync(); await f.Wait(() => !f.Naming.HasActiveOrPendingWork);
        Assert.Equal(0, f.Transport.StartCount); Assert.Empty(await f.Data.Store.ListActiveNodeNamingJobsAsync()); Assert.Empty(await f.Data.Store.ListNodeNamingIntentsAsync());
        await f.Naming.NotifyCurrentChangedAsync(f.Data.Node); var call = await f.Transport.NextAsync(); call.Emit(f.Payload(), "stop"); call.Finish(); await f.Wait(() => !f.Naming.HasActiveOrPendingWork); Assert.Equal(1, f.Transport.StartCount);
    }
    [Fact]
    public async Task Pending_burst_clear_failure_cannot_restart_old_work_after_return()
    {
        await using var f = await Fixture.Open(); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node); var call = await f.Transport.NextAsync();
        await f.Data.Commit("First", "new current"); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node); f.Wrapped.ClearFault = true;
        await Assert.ThrowsAsync<IOException>(() => f.Runtime.ShutdownAsync()); f.Runtime.ResumeAccepting(); f.Wrapped.ClearFault = false;
        await f.Runtime.RetryPersistenceAsync(); await f.Wait(() => !f.Naming.HasActiveOrPendingWork);
        Assert.Equal(1, f.Transport.StartCount); Assert.Empty(await f.Data.Store.ListNodeNamingIntentsAsync()); Assert.Equal("First", (await f.Data.Snapshot()).State.Node.DisplayName);
    }
    [Fact]
    public async Task Deleted_source_stops_durable_task_immediately_and_late_payload_cannot_partially_name_group()
    {
        await using var f = await Fixture.Open(); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node); var call = await f.Transport.NextAsync(); var payload = f.Payload();
        await f.Data.Versions.DeleteSummaryVersionsAsync((await f.Data.Versions.PreviewDeleteSummaryVersionsAsync(f.Data.Node, [f.Data.First])).Value!);
        Assert.Empty(await f.Data.Store.ListActiveNodeNamingJobsAsync()); call.Emit(payload, "stop"); call.Finish(); await f.Wait(() => !f.Naming.HasActiveOrPendingWork);
        Assert.Null(await f.Data.Versions.LoadSummaryNodeAsync(f.Data.Node)); Assert.Equal("Ｆｉｒｓｔ", Assert.Single(await f.Data.Store.ListSummaryNodesAsync(f.Data.Workspace)).State.Node.DisplayName); Assert.False(f.Runtime.HasPersistenceFailure);
    }
    [Fact]
    public async Task Overbudget_full_material_fails_locally_without_truncation_and_paused_gate_defers_cleanly()
    {
        await using var f = await Fixture.Open(); await f.Data.Commit("First", new string('x', 34000));
        var other = f.Runtime.CreateFailureSource(); var epoch = other.Report(); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node);
        Assert.Equal(0, f.Transport.StartCount); other.TryClear(epoch); await f.Wait(() => !f.Naming.HasActiveOrPendingWork);
        Assert.Equal(0, f.Transport.StartCount); Assert.False(f.Runtime.HasPersistenceFailure); Assert.Equal("First", (await f.Data.Snapshot()).State.Node.DisplayName);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task Failed_latest_pending_save_does_not_leave_an_older_intent_after_shutdown(string fault)
    {
        await using var f = await Fixture.Open(); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node); var call = await f.Transport.NextAsync();
        await f.Data.Commit("First", "first pending"); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node);
        await f.Data.Commit("First", "latest pending"); f.Wrapped.SaveFault = fault;
        await Assert.ThrowsAsync<IOException>(() => f.Naming.NotifyCurrentChangedAsync(f.Data.Node));
        await f.Runtime.ShutdownAsync(); f.Runtime.ResumeAccepting(); await f.Runtime.RetryPersistenceAsync();
        Assert.Empty(await f.Data.Store.ListNodeNamingIntentsAsync()); Assert.Equal(1, f.Transport.StartCount);
    }
    // Break caught: a normally rejected replacement loses its durable predecessor's cleanup ownership.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_latest_pending_keeps_predecessor_owned_until_conditional_cleanup_succeeds(bool clearFailure)
    {
        await using var f = await Fixture.Open();
        await f.Naming.NotifyCurrentChangedAsync(f.Data.Node);
        var call = await f.Transport.NextAsync(); var payload = f.Payload();
        await f.Data.Commit("First", "durable predecessor");
        await f.Naming.NotifyCurrentChangedAsync(f.Data.Node);
        var predecessor = Assert.Single(await f.Data.Store.ListNodeNamingIntentsAsync());
        await f.Data.Commit("First", "replacement snapshot about to become stale");
        f.Wrapped.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Wrapped.SaveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = f.Naming.NotifyCurrentChangedAsync(f.Data.Node);
        try
        {
            await f.Wrapped.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var node = (await f.Data.Snapshot()).State.Node;
            await f.Data.Versions.RenameSummaryNodeAsync(node.Id, node.NameRevision, "Protected manual name");
            f.Wrapped.ClearFault = clearFailure;
        }
        finally { f.Wrapped.SaveRelease.TrySetResult(); }
        if (clearFailure)
        {
            await Assert.ThrowsAsync<IOException>(() => replacement);
            Assert.Equal(SummaryOutcomeKind.Obsolete, f.Wrapped.LastSaveKind);
            Assert.Equal(predecessor.OperationId, Assert.Single(await f.Data.Store.ListNodeNamingIntentsAsync()).OperationId);
            Assert.True(f.Runtime.HasPersistenceFailure); Assert.True(f.Naming.HasActiveOrPendingWork);
            await Assert.ThrowsAsync<IOException>(() => f.Runtime.ShutdownAsync());
            f.Runtime.ResumeAccepting(); f.Wrapped.ClearFault = false;
            await f.Runtime.RetryPersistenceAsync();
        }
        else
        {
            await replacement;
            Assert.Equal(SummaryOutcomeKind.Obsolete, f.Wrapped.LastSaveKind);
            Assert.False(f.Runtime.HasPersistenceFailure);
            call.Emit(payload, "stop"); call.Finish();
        }
        await f.Wait(() => !f.Naming.HasActiveOrPendingWork);
        Assert.Empty(await f.Data.Store.ListNodeNamingIntentsAsync());
        await f.Runtime.ShutdownAsync();
        Assert.Empty(await f.Data.Store.ListNodeNamingIntentsAsync());
        Assert.Equal(1, f.Transport.StartCount);
        Assert.Equal("Protected manual name", (await f.Data.Snapshot()).State.Node.DisplayName);
    }
    internal sealed class FaultStore(INodeNamingStore inner) : INodeNamingStore
    {
        public string? CompletionFault, AcceptFault, SaveFault; public bool FindFault, ClearFault;
        public List<NodeNamingCompletion> Completions = [];
        public TaskCompletionSource? SaveEntered, SaveRelease;
        public SummaryOutcomeKind? LastSaveKind;
        public Task<NodeNamingSnapshot> ReadNodeNamingGroupAsync(Guid w, string g) => inner.ReadNodeNamingGroupAsync(w, g);
        public async Task<SummaryOutcome<NodeNamingJob>> AcceptNodeNamingAsync(Guid op, NodeNamingSnapshot s, AiConfiguration c)
        { var fault = AcceptFault; AcceptFault = null; if (fault == "before") throw new IOException("synthetic accept"); var r = await inner.AcceptNodeNamingAsync(op, s, c); if (fault == "after") throw new IOException("synthetic lost accept"); return r; }
        public Task<NodeNamingJob?> LoadNodeNamingJobAsync(Guid id) => inner.LoadNodeNamingJobAsync(id);
        public Task<NodeNamingJob?> FindNodeNamingOperationAsync(Guid id) => FindFault ? Task.FromException<NodeNamingJob?>(new IOException("synthetic lookup")) : inner.FindNodeNamingOperationAsync(id);
        public Task<ImmutableArray<NodeNamingJob>> ListActiveNodeNamingJobsAsync() => inner.ListActiveNodeNamingJobsAsync();
        public Task<SummaryOutcome<ImmutableArray<NodeNamingMaterial>>> LoadNodeNamingMaterialsAsync(Guid id) => inner.LoadNodeNamingMaterialsAsync(id);
        public Task<SummaryOutcome<NodeNamingJob>> UpdateNodeNamingMetadataAsync(SummaryGenerationMetadata u) => inner.UpdateNodeNamingMetadataAsync(u);
        public async Task<SummaryOutcome<NodeNamingResult>> CompleteNodeNamingAsync(NodeNamingCompletion c)
        { Completions.Add(c); if (CompletionFault == "before") throw new IOException("synthetic completion"); var r = await inner.CompleteNodeNamingAsync(c); if (CompletionFault == "after") throw new IOException("synthetic lost completion"); return r; }
        public Task RecoverInterruptedNodeNamingAsync() => inner.RecoverInterruptedNodeNamingAsync();
        public async Task<SummaryOutcome<NodeNamingIntent>> SaveNodeNamingIntentAsync(NodeNamingIntent i)
        {
            var fault = SaveFault; SaveFault = null;
            if (fault == "before") throw new IOException("synthetic intent");
            if (SaveRelease is { } release) { SaveEntered!.TrySetResult(); await release.Task; }
            var result = await inner.SaveNodeNamingIntentAsync(i); LastSaveKind = result.Kind;
            if (fault == "after") throw new IOException("synthetic lost intent");
            return result;
        }
        public Task ClearNodeNamingIntentAsync(Guid w, string g, Guid op) => ClearFault ? Task.FromException(new IOException("synthetic clear")) : inner.ClearNodeNamingIntentAsync(w, g, op);
        public Task<ImmutableArray<NodeNamingIntent>> ListNodeNamingIntentsAsync() => inner.ListNodeNamingIntentsAsync();
        public Task<SummaryOutcome<PreparedNodeNamingRequest>> PrepareNodeNamingRequestAsync(Guid id, long r) => inner.PrepareNodeNamingRequestAsync(id, r);
    }
}
