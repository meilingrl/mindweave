using System.Collections.Immutable;

using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Xunit;
namespace Mindweave.Tests;

public sealed partial class SummaryNamingStorageTests
{
    [Fact]
    public async Task Final_metadata_failure_rolls_back_all_names_and_identical_completion_can_repair()
    {
        await using var f = await SummaryStorageFixture.Open(); await Add(f); var store = Naming(f); var job = await Accept(f); var before = f.DatabaseSnapshot();
        f.Sql("CREATE TRIGGER synthetic_naming_commit BEFORE UPDATE ON node_naming_job WHEN NEW.status='Completed' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END");
        var completion = new NodeNamingCompletion(job.Id, job.Revision, job.Snapshot.SnapshotHash, Proposals(job));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.CompleteNodeNamingAsync(completion));
        f.Sql("DROP TRIGGER synthetic_naming_commit"); Assert.Equal(before, f.DatabaseSnapshot());
        Assert.Equal(SummaryOutcomeKind.Applied, (await store.CompleteNodeNamingAsync(completion)).Kind);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM node_naming_job WHERE snapshot_json LIKE '%second evidence%' OR configuration_json LIKE '%second evidence%'"));
    }
    [Fact]
    public async Task Equal_base_names_are_allowed_and_rename_completion_does_not_change_group_members()
    {
        await using var f = await SummaryStorageFixture.Open(); await Add(f); var store = Naming(f); var job = await Accept(f);
        var names = job.Snapshot.Members.Select(m => new NodeNameProposal(m.NodeId, "First")).ToImmutableArray();
        Assert.Equal(SummaryOutcomeKind.Applied, (await store.CompleteNodeNamingAsync(new(job.Id, job.Revision, job.Snapshot.SnapshotHash, names))).Kind);
        Assert.All(await f.Store.ListSummaryNodesAsync(f.Workspace), n => Assert.Equal("First", n.State.Node.DisplayName));
        Assert.Equal(2, (await store.ReadNodeNamingGroupAsync(f.Workspace, "FIRST")).Members.Length);
    }
    [Fact]
    public async Task Candidate_then_adopt_and_restore_keep_existing_auto_distinction_and_invalidate_old_group()
    {
        await using var f = await SummaryStorageFixture.Open(); await Add(f); var store = Naming(f); var job = await Accept(f);
        await store.CompleteNodeNamingAsync(new(job.Id, job.Revision, job.Snapshot.SnapshotHash, Proposals(job)));
        var named = (await f.Snapshot()).State.Node; var snapshot = await store.ReadNodeNamingGroupAsync(f.Workspace, "FIRST");
        await f.Generate("New title"); Assert.Equal(snapshot.SnapshotHash, (await store.ReadNodeNamingGroupAsync(f.Workspace, "FIRST")).SnapshotHash);
        var adopted = await f.Versions.AdoptSummaryCandidateAsync((await f.Snapshot()).State.Token); Assert.Equal(SummaryOutcomeKind.Applied, adopted.Kind);
        Assert.Equal("New title", adopted.Value!.State.Node.BaseTitle); Assert.Equal(named.DisplayName, adopted.Value.State.Node.DisplayName); Assert.Equal(named.NameRevision, adopted.Value.State.Node.NameRevision);
        var restored = await f.Versions.SetCurrentSummaryVersionAsync(adopted.Value.State.Token, f.First); Assert.Equal(named.DisplayName, restored.Value!.State.Node.DisplayName);
        Assert.NotEqual(snapshot.SnapshotHash, (await store.ReadNodeNamingGroupAsync(f.Workspace, "FIRST")).SnapshotHash);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"NodeId\":\"bad\",\"DisplayName\":\"name\"}]")]
    public async Task Strict_protocol_rejects_incomplete_or_outside_member_set(string payload)
    {
        await using var f = await SummaryStorageFixture.Open(); await Add(f); var job = await Accept(f);
        Assert.Throws<SummaryProtocolException>(() => NodeNamingProtocol.ParseComplete(payload, job.Snapshot));
    }
    [Fact]
    public async Task Protocol_rejects_duplicate_field_or_node_invalid_unicode_and_overlong_name()
    {
        await using var f = await SummaryStorageFixture.Open(); await Add(f); var job = await Accept(f); var id = job.Snapshot.Members[0].NodeId;
        Assert.Throws<SummaryProtocolException>(() => NodeNamingProtocol.ParseComplete("[{\"NodeId\":\"" + id + "\",\"NodeId\":\"" + id + "\",\"DisplayName\":\"name\"}]", job.Snapshot));
        Assert.Throws<SummaryProtocolException>(() => NodeNamingProtocol.ValidateNames([new(id, "x"), new(id, "y")], job.Snapshot));
        foreach (var bad in new[] { "\ud800", new string('x', 161), "line\nbreak" })
            Assert.Throws<SummaryProtocolException>(() => NodeNamingProtocol.ValidateNames([new(id, bad), new(job.Snapshot.Members[1].NodeId, "valid")], job.Snapshot));
    }
    [Fact]
    public async Task Member_row_corruption_and_prepared_hash_corruption_are_not_silently_ignored()
    {
        await using var f = await SummaryStorageFixture.Open(); await Add(f); var store = Naming(f); var job = await Accept(f);
        f.Sql("UPDATE node_naming_job SET request_hash='wrong',revision=revision+1 WHERE id=$id", ("$id", job.Id));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareNodeNamingRequestAsync(job.Id, job.Revision + 1));
        f.Sql("DELETE FROM node_naming_member WHERE task_id=$id AND ordinal=0", ("$id", job.Id));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadNodeNamingMaterialsAsync(job.Id));
    }
    [Fact]
    public async Task Accepted_operation_replay_clears_only_its_matching_pending_intent()
    {
        await using var f = await SummaryStorageFixture.Open(); await Add(f); var store = Naming(f); var job = await Accept(f, false);
        await store.SaveNodeNamingIntentAsync(new(job.OperationId, job.Snapshot, job.Configuration, DateTimeOffset.UtcNow));
        Assert.Equal(SummaryOutcomeKind.NoChange, (await store.AcceptNodeNamingAsync(job.OperationId, job.Snapshot, job.Configuration)).Kind);
        Assert.Empty(await store.ListNodeNamingIntentsAsync());
    }

}
