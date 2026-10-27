using System.Text.Json;
using Mindweave.Application.Generation;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Xunit;
namespace Mindweave.Tests;

public sealed partial class GroupNamingCoordinatorTests
{
    [Fact]
    public async Task Naming_waits_for_reply_demand_and_complete_disposal_then_commits_without_self_trigger()
    {
        await using var f = await Fixture.Open(); using var reply = f.Runtime.Scheduler.RegisterLogicalWork(GenerationPriority.Reply);
        await f.Naming.NotifyCurrentChangedAsync(f.Data.Node);
        Assert.Equal(0, f.Transport.StartCount); reply.Dispose(); var call = await f.Transport.NextAsync();
        Assert.Equal(Mindweave.Application.Chat.ChatRequestPurpose.Standard, call.Request.Purpose);
        Assert.Contains("second evidence", call.Request.Messages[1].Content);
        call.DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        call.Emit(f.Payload(), "stop"); call.Finish(); await call.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("First", (await f.Data.Snapshot()).State.Node.DisplayName); Assert.True(f.Naming.HasActiveOrPendingWork);
        call.DisposeRelease.SetResult(); await f.Wait(() => !f.Naming.HasActiveOrPendingWork);
        Assert.Equal("First－one", (await f.Data.Snapshot()).State.Node.DisplayName); Assert.Equal(1, f.Transport.StartCount);
    }
    [Theory]
    [InlineData("length")]
    [InlineData("bad")]
    [InlineData("dispose")]
    public async Task Failed_or_partial_response_never_renames_or_retries(string mode)
    {
        await using var f = await Fixture.Open(); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node); var call = await f.Transport.NextAsync();
        if (mode == "dispose") call.DisposeFailure = new IOException("synthetic");
        call.Emit(mode == "bad" ? "{}" : f.Payload(), mode == "length" ? "length" : "stop"); call.Finish();
        await f.Wait(() => !f.Naming.HasActiveOrPendingWork);
        Assert.Equal("First", (await f.Data.Snapshot()).State.Node.DisplayName); Assert.Equal(1, f.Transport.StartCount); Assert.False(f.Runtime.HasPersistenceFailure);
    }
    [Fact]
    public async Task Burst_keeps_latest_intent_with_only_one_followup_and_deletion_does_not_revive_nodes()
    {
        await using var f = await Fixture.Open(); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node); var first = await f.Transport.NextAsync();
        await f.Data.Commit("First", "new evidence one"); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node);
        await f.Data.Commit("First", "latest evidence two"); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node);
        Assert.Single(await SummaryNamingStorageTests.Naming(f.Data).ListNodeNamingIntentsAsync());
        first.Emit(f.Payload(), "stop"); first.Finish(); var second = await f.Transport.NextAsync(); Assert.Contains("latest evidence two", second.Request.Messages[1].Content);
        await f.Data.Commit("First", "third change in same burst"); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node);
        second.Emit(f.Payload(), "stop"); second.Finish(); await f.Wait(() => !f.Naming.HasActiveOrPendingWork);
        Assert.Equal(2, f.Transport.StartCount); Assert.Empty(await SummaryNamingStorageTests.Naming(f.Data).ListNodeNamingIntentsAsync());
        Assert.Equal("First", (await f.Data.Snapshot()).State.Node.DisplayName);
    }
    [Fact]
    public async Task Singleton_without_suffix_and_all_manual_groups_need_no_request_and_restart_is_passive()
    {
        await using var f = await Fixture.Open(false); await f.Naming.NotifyCurrentChangedAsync(f.Data.Node); await f.Wait(() => !f.Naming.HasActiveOrPendingWork); Assert.Equal(0, f.Transport.StartCount);
        await SummaryNamingStorageTests.Add(f.Data); await f.Data.Versions.RenameSummaryNodeAsync(f.Data.Node, 0, "manual");
        foreach (var n in await f.Data.Store.ListSummaryNodesAsync(f.Data.Workspace)) if (!n.State.Node.NameIsManual) await f.Data.Versions.RenameSummaryNodeAsync(n.State.Node.Id, 0, "also manual");
        await f.Naming.NotifyCurrentChangedAsync(f.Data.Node); await f.Wait(() => !f.Naming.HasActiveOrPendingWork); Assert.Equal(0, f.Transport.StartCount);
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public SummaryStorageFixture Data = null!;
        public AppGenerationRuntime Runtime = new();
        public GenerationCoordinatorTests.ControlledTransport Transport = new();
        public GroupNamingCoordinator Naming = null!;
        public FaultStore Wrapped = null!;
        public static async Task<Fixture> Open(bool pair = true)
        {
            var f = new Fixture { Data = await SummaryStorageFixture.Open() }; if (pair) await SummaryNamingStorageTests.Add(f.Data);
            await f.Data.Store.SaveConfigurationAsync(BranchStorageTests.Config(32768) with { CredentialReference = "v1", BaseUrl = "https://api.deepseek.com/v1", SummaryThinking = SummaryThinkingMode.Disabled });
            f.Wrapped = new(SummaryNamingStorageTests.Naming(f.Data));
            f.Naming = new(f.Wrapped, f.Data.Store, f.Data.Store, f.Transport, new GenerationCoordinatorTests.MemoryVault(), f.Runtime);
            await f.Naming.InitializeAsync(); return f;
        }
        public string Payload() => JsonSerializer.Serialize(((System.Collections.Generic.IEnumerable<Mindweave.Core.Summaries.SummaryNodeSnapshot>)Data.Store.ListSummaryNodesAsync(Data.Workspace).GetAwaiter().GetResult()).Where(n => !n.State.Node.NameIsManual).Select(n => new { NodeId = n.State.Node.Id, DisplayName = n.State.Node.Id == Data.Node ? "First－one" : "First－two" }));
        public Task Wait(Func<bool> condition) => KnowledgeSummaryCoordinatorTests.Fixture.WaitFor(condition, h => Naming.Changed += h, h => Naming.Changed -= h);
        public async ValueTask DisposeAsync() { Transport.Close(); Wrapped.CompletionFault = null; Wrapped.FindFault = false; Wrapped.ClearFault = false; await Runtime.RetryPersistenceAsync(); await Naming.DisposeAsync(); await Runtime.DisposeAsync(); await Data.DisposeAsync(); }
    }
}
