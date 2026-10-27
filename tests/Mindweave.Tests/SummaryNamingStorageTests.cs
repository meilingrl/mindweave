using System.Collections.Immutable;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryNamingStorageTests
{
    internal static INodeNamingStore Naming(SummaryStorageFixture f) => Assert.IsAssignableFrom<INodeNamingStore>(f.Store);
    internal static async Task<SummaryGenerationResult> Add(SummaryStorageFixture f, string title = "Ｆｉｒｓｔ", string body = "second evidence")
    {
        var conversation = (await f.Store.CreateConversationAsync(f.Workspace)).Id;
        await BranchStorageTests.Answer(f.Store, conversation, "second question", "second answer");
        var job = (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), conversation, BranchStorageTests.Config(32768))).Value!;
        await f.Store.PrepareKnowledgeSummaryRequestAsync(job.Id, job.Revision);
        return (await f.Store.CompleteKnowledgeSummaryAsync(new(job.Id, job.Revision, new(title, body), "synthetic"))).Value!;
    }
    internal static async Task<NodeNamingJob> Accept(SummaryStorageFixture f, bool prepare = true)
    {
        var store = Naming(f); var snapshot = await store.ReadNodeNamingGroupAsync(f.Workspace, "FIRST");
        var result = await store.AcceptNodeNamingAsync(Guid.NewGuid(), snapshot, BranchStorageTests.Config(32768));
        Assert.Equal(SummaryOutcomeKind.Applied, result.Kind);
        if (prepare) { var ready = (await store.PrepareNodeNamingRequestAsync(result.Value!.Id, result.Value.Revision)).Value!; return result.Value with { Revision = ready.Revision }; }
        return result.Value!;
    }
    internal static ImmutableArray<NodeNameProposal> Proposals(NodeNamingJob job) => job.Snapshot.Members.Where(m => !m.NameIsManual).Select(m => new NodeNameProposal(m.NodeId, "First－" + (m.NodeId == job.Snapshot.Members[0].NodeId ? "one" : "two"))).ToImmutableArray();

    [Fact]
    public async Task Normalized_whole_group_is_frozen_and_names_do_not_modify_versions_or_read_state()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await Add(f);
        var other = (await f.Store.CreateAsync("other workspace")).Id;
        var store = Naming(f); var job = await Accept(f);
        Assert.Equal(2, job.Snapshot.Members.Length); Assert.Empty((await store.ReadNodeNamingGroupAsync(other, "FIRST")).Members);
        var before = await f.Versions.LoadSummaryVersionAsync(f.First);
        var material = (await store.LoadNodeNamingMaterialsAsync(job.Id)).Value;
        Assert.Contains(material, m => m.Content.Markdown == "second evidence");
        var result = await store.CompleteNodeNamingAsync(new(job.Id, job.Revision, job.Snapshot.SnapshotHash, Proposals(job)));
        Assert.Equal(SummaryOutcomeKind.Applied, result.Kind); Assert.Equal(2, result.Value!.ChangedNodeIds.Length);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(await f.Versions.LoadSummaryVersionAsync(f.First)));
        Assert.Equal(0L, f.Sql("SELECT sum(is_read) FROM summary_read_state"));
        Assert.Equal(SummaryOutcomeKind.NoChange, (await store.CompleteNodeNamingAsync(new(job.Id, job.Revision, job.Snapshot.SnapshotHash, Proposals(job)))).Kind);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("current")]
    [InlineData("member")]
    [InlineData("delete")]
    public async Task Stale_group_rejects_every_name_without_partial_update_or_resurrection(string change)
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await Add(f); var store = Naming(f); var job = await Accept(f);
        if (change == "manual") await f.Versions.RenameSummaryNodeAsync(f.Node, 0, "Protected");
        if (change == "current") await f.Commit("First", "updated content");
        if (change == "member") await Add(f, " first ");
        if (change == "delete") await f.Versions.DeleteSummaryVersionsAsync((await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [f.First])).Value!);
        var secondBefore = (await f.Versions.LoadSummaryNodeAsync(second.NodeId))!.State.Node;
        Assert.False((await store.CompleteNodeNamingAsync(new(job.Id, job.Revision, job.Snapshot.SnapshotHash, Proposals(job)))).Kind is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange);
        Assert.Equal(secondBefore, (await f.Versions.LoadSummaryNodeAsync(second.NodeId))!.State.Node);
        if (change == "delete") { Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node)); Assert.Equal(GenerationStatus.Stopped, (await store.LoadNodeNamingJobAsync(job.Id))!.Status); }
    }

    [Fact]
    public async Task Manual_members_are_frozen_but_not_output_targets_and_auto_suffix_survives_current_switch()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await Add(f); var store = Naming(f);
        await f.Versions.RenameSummaryNodeAsync(second.NodeId, 0, "Protected"); var job = await Accept(f);
        Assert.Equal(2, job.Snapshot.Members.Length);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await store.CompleteNodeNamingAsync(new(job.Id, job.Revision, job.Snapshot.SnapshotHash, [new(second.NodeId, "overwrite")]))).Kind);
        var result = await store.CompleteNodeNamingAsync(new(job.Id, job.Revision, job.Snapshot.SnapshotHash, [new(f.Node, "First－one")]));
        Assert.Equal(SummaryOutcomeKind.Applied, result.Kind);
        var rev = (await f.Snapshot()).State.Node.NameRevision;
        await f.Commit("New base", "one is still the topic");
        var node = (await f.Snapshot()).State.Node;
        Assert.Equal("New base", node.BaseTitle); Assert.Equal("First－one", node.DisplayName); Assert.Equal(rev, node.NameRevision);
        Assert.Equal("Protected", (await f.Versions.LoadSummaryNodeAsync(second.NodeId))!.State.Node.DisplayName);
    }
    [Fact]
    public async Task Pending_latest_intent_is_identity_cleared_and_recovery_never_requeues()
    {
        await using var f = await SummaryStorageFixture.Open(); await Add(f); var store = Naming(f);
        var snapshot = await store.ReadNodeNamingGroupAsync(f.Workspace, "FIRST");
        var old = new NodeNamingIntent(Guid.NewGuid(), snapshot, BranchStorageTests.Config(32768), DateTimeOffset.UtcNow);
        var latest = old with { OperationId = Guid.NewGuid(), RequestedAt = old.RequestedAt.AddDays(-1) };
        Assert.Equal(SummaryOutcomeKind.Applied, (await store.SaveNodeNamingIntentAsync(old)).Kind);
        await store.SaveNodeNamingIntentAsync(latest);
        await store.ClearNodeNamingIntentAsync(f.Workspace, "FIRST", old.OperationId);
        Assert.Equal(latest.OperationId, Assert.Single(await store.ListNodeNamingIntentsAsync()).OperationId);
        var job = (await store.AcceptNodeNamingAsync(latest.OperationId, snapshot, latest.Configuration)).Value!;
        Assert.Empty(await store.ListNodeNamingIntentsAsync());
        await store.RecoverInterruptedNodeNamingAsync();
        Assert.Equal(GenerationStatus.Interrupted, (await store.LoadNodeNamingJobAsync(job.Id))!.Status);
    }
    [Fact]
    public async Task Prepared_request_tracks_full_material_names_default_purpose_budget_and_exact_hash()
    {
        await using var f = await SummaryStorageFixture.Open(); await Add(f); var store = Naming(f); var job = await Accept(f, false);
        var prepared = (await store.PrepareNodeNamingRequestAsync(job.Id, job.Revision)).Value!;
        Assert.Equal(job.Revision + 1, prepared.Revision);
        Assert.Contains("second evidence", prepared.Request.Messages[1].Content);
        Assert.Equal(Mindweave.Application.Chat.ChatRequestPurpose.Standard, prepared.Request.Purpose);
        Assert.Equal(prepared.RequestHash, (await store.PrepareNodeNamingRequestAsync(job.Id, prepared.Revision)).Value!.RequestHash);
        await f.Versions.RenameSummaryNodeAsync(f.Node, 0, "Manual");
        Assert.Equal(SummaryOutcomeKind.Obsolete, (await store.PrepareNodeNamingRequestAsync(job.Id, prepared.Revision)).Kind);
    }

    [Fact]
    public async Task Completion_requires_exact_prepared_request_metadata()
    {
        await using var f = await SummaryStorageFixture.Open(); await Add(f); var store = Naming(f); var job = await Accept(f, false);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await store.CompleteNodeNamingAsync(new(job.Id, job.Revision, job.Snapshot.SnapshotHash, Proposals(job)))).Kind);
    }

}
