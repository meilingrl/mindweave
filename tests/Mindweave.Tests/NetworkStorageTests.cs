using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Network;
using Mindweave.Core.Network;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class NetworkStorageTests
{
    private static ImmutableDictionary<Guid, NodePosition> Positions(params (Guid Id, double X, double Y)[] rows) => rows.ToImmutableDictionary(r => r.Id, r => new NodePosition(r.X, r.Y));
    private static async Task<SummaryGenerationResult> Generate(SqliteWorkspaceStore store, Guid conversation, string title = "Summary")
    {
        var job = (await store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), conversation, BranchStorageTests.Config())).Value!;
        Assert.Equal(SummaryOutcomeKind.Applied, (await store.PrepareKnowledgeSummaryRequestAsync(job.Id, job.Revision)).Kind);
        var result = await store.CompleteKnowledgeSummaryAsync(new(job.Id, job.Revision, new(title, "# synthetic body"), "synthetic"));
        Assert.Equal(SummaryOutcomeKind.Applied, result.Kind); return result.Value!;
    }
    private static async Task<Guid> AddNode(SummaryStorageFixture f)
    {
        var conversation = (await f.Store.CreateConversationAsync(f.Workspace)).Id;
        await BranchStorageTests.Answer(f.Store, conversation, "q", "a");
        return (await Generate(f.Store, conversation)).NodeId;
    }

    // Catches migration data loss, absent backup, and old/new software accepting an unsupported schema.
    [Fact]
    public async Task Upgrade_from_six_keeps_all_rows_and_backup_and_rejects_newer_software_formats()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using (var old = await SqliteWorkspaceStore.OpenAsync(paths, DatabaseSchema.Migrations.Take(6).ToArray()))
        {
            var conversation = (await old.CreateConversationAsync((await old.CreateAsync("v6")).Id)).Id;
            await BranchStorageTests.Answer(old, conversation, "q", "a"); await Generate(old, conversation);
        }
        var before = SummaryMigrationTests.Snapshot(paths.DatabasePath); var tables = SummaryMigrationTests.Tables(before);
        await using (var upgraded = await SqliteWorkspaceStore.OpenAsync(paths))
            Assert.Equal(7L, ContextStorageTests.Sql(paths, "PRAGMA user_version"));
        Assert.Equal(before, SummaryMigrationTests.Snapshot(paths.DatabasePath, tables));
        Assert.Equal(before, SummaryMigrationTests.Snapshot(Assert.Single(Directory.GetFiles(paths.BackupDirectory, "*.db"))));
        var bytes = File.ReadAllBytes(paths.DatabasePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWorkspaceStore.OpenAsync(paths, DatabaseSchema.Migrations.Take(6).ToArray()));
        Assert.Equal(bytes, File.ReadAllBytes(paths.DatabasePath));
        ContextStorageTests.Sql(paths, "PRAGMA user_version=8"); bytes = File.ReadAllBytes(paths.DatabasePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWorkspaceStore.OpenAsync(paths));
        Assert.Equal(bytes, File.ReadAllBytes(paths.DatabasePath));
    }

    // Catches metadata reads consuming bodies, marking read, losing candidate/later flags, or including another workspace.
    [Fact]
    public async Task Load_projects_real_hidden_branch_metadata_without_changing_any_business_row()
    {
        await using var f = await SummaryStorageFixture.Open();
        var source = (await f.Store.ReadMessagesAsync(f.Conversation)).Last();
        var middle = await f.Store.CreateBranchAsync(f.Conversation, BranchStorageTests.Selection(source), Guid.NewGuid());
        var middleAnswer = await BranchStorageTests.Answer(f.Store, middle.Id, "middle q", "middle a");
        var child = await f.Store.CreateBranchAsync(middle.Id, BranchStorageTests.Selection(middleAnswer), Guid.NewGuid());
        await BranchStorageTests.Answer(f.Store, child.Id, "child q", "child a"); var childNode = (await Generate(f.Store, child.Id)).NodeId;
        await BranchStorageTests.Answer(f.Store, f.Conversation, "later", "later terminal");
        var other = (await f.Store.CreateConversationAsync((await f.Store.CreateAsync("other")).Id)).Id;
        await BranchStorageTests.Answer(f.Store, other, "other", "answer"); await Generate(f.Store, other);
        var before = f.DatabaseSnapshot(); var snapshot = await f.Store.LoadNetworkAsync(f.Workspace);
        Assert.Equal(before, f.DatabaseSnapshot()); Assert.Empty(snapshot.SavedPositions);
        Assert.Equal(new[] { f.Conversation, middle.Id, child.Id }, Assert.Single(snapshot.Graph.Edges).Path.Select(p => p.ConversationId));
        Assert.Equal(new[] { f.Node, childNode }.Order(), snapshot.Graph.Nodes.Select(n => n.Metadata.Id).Order());
        var metadata = snapshot.Graph.Nodes.Single(n => n.Metadata.Id == f.Node).Metadata;
        Assert.Equal("First", metadata.DisplayName); Assert.Equal("First", metadata.BaseTitle);
        Assert.True(metadata.IsUnread); Assert.False(metadata.HasCandidate); Assert.True(metadata.HasLaterContent);
        Assert.False(snapshot.Graph.Nodes.Single(n => n.Metadata.Id == childNode).Metadata.HasLaterContent);
        await f.Generate("Candidate");
        f.Sql("UPDATE summary_read_state SET is_read=1 WHERE version_id=$id", ("$id", f.First));
        before = f.DatabaseSnapshot();
        var current = (await f.Store.LoadNetworkAsync(f.Workspace)).Graph.Nodes.Single(n => n.Metadata.Id == f.Node).Metadata;
        Assert.Equal(before, f.DatabaseSnapshot());
        Assert.False(current.IsUnread); Assert.True(current.HasCandidate); Assert.True(current.HasLaterContent);
        Assert.Equal("First", current.BaseTitle); // Candidate coverage/title/read state must not replace the current metadata.
    }

    // Catches initial placement overwriting existing rows or persisting someone else's pending arrangement.
    [Fact]
    public async Task Initial_position_is_insert_only_and_independent_and_survives_reopen()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await AddNode(f);
        Assert.Equal(LayoutWriteStatus.Applied, (await f.Store.TrySaveInitialNodePositionAsync(f.Workspace, f.Node, new(10, 20))).Status);
        Assert.Equal(LayoutWriteStatus.Applied, (await f.Store.TrySaveInitialNodePositionAsync(f.Workspace, second, new(30, 40))).Status);
        var result = await f.Store.TrySaveInitialNodePositionAsync(f.Workspace, f.Node, new(999, 999));
        Assert.Equal(LayoutWriteStatus.AlreadyExists, result.Status); Assert.Equal(new NodePosition(10, 20), result.ExistingPosition);
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(f.Paths);
        Assert.Equal(Positions((f.Node, 10, 20), (second, 30, 40)).OrderBy(p => p.Key), (await reopened.LoadNetworkAsync(f.Workspace)).SavedPositions.OrderBy(p => p.Key));
    }

    // Catches per-row commits and swallowed IO exceptions.
    [Fact]
    public async Task Full_layout_failure_rolls_back_every_row_then_can_commit_atomically()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await AddNode(f);
        var ids = ImmutableArray.Create(f.Node, second); var old = Positions((f.Node, 1, 2), (second, 3, 4));
        Assert.Equal(LayoutWriteStatus.Applied, (await f.Store.SaveLayoutAsync(f.Workspace, old, ids)).Status);
        // Abort whichever row is updated second, independent of application iteration order.
        f.Sql("CREATE TABLE layout_test_counter(value INTEGER); INSERT INTO layout_test_counter VALUES(0); CREATE TRIGGER fail_second_layout BEFORE UPDATE ON node_layout BEGIN UPDATE layout_test_counter SET value=value+1; SELECT CASE WHEN (SELECT value FROM layout_test_counter)=2 THEN RAISE(ABORT,'synthetic layout failure') END; END;");
        var changed = Positions((f.Node, 11, 12), (second, 13, 14));
        await Assert.ThrowsAsync<SqliteException>(() => f.Store.SaveLayoutAsync(f.Workspace, changed, ids));
        Assert.Equal(old.OrderBy(p => p.Key), (await f.Store.LoadNetworkAsync(f.Workspace)).SavedPositions.OrderBy(p => p.Key));
        Assert.Equal(0L, f.Sql("SELECT value FROM layout_test_counter"));
        f.Sql("DROP TRIGGER fail_second_layout; DROP TABLE layout_test_counter");
        Assert.Equal(LayoutWriteStatus.Applied, (await f.Store.SaveLayoutAsync(f.Workspace, changed, ids)).Status);
        Assert.Equal(changed.OrderBy(p => p.Key), (await f.Store.LoadNetworkAsync(f.Workspace)).SavedPositions.OrderBy(p => p.Key));
    }

    // Catches stale writes recreating deleted nodes, missing cascade, or partial application after membership changes.
    [Fact]
    public async Task Last_version_deletion_cascades_layout_and_late_writes_cannot_restore_it()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await AddNode(f);
        var positions = Positions((f.Node, 1, 2), (second, 3, 4));
        await f.Store.SaveLayoutAsync(f.Workspace, positions, [f.Node, second]);
        var preview = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [f.First])).Value!;
        Assert.Equal(SummaryOutcomeKind.Applied, (await f.Versions.DeleteSummaryVersionsAsync(preview)).Kind);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM node_layout WHERE summary_node_id=$id", ("$id", f.Node)));
        Assert.Equal(LayoutWriteStatus.NodeMissing, (await f.Store.TrySaveInitialNodePositionAsync(f.Workspace, f.Node, new(9, 9))).Status);
        Assert.Equal(LayoutWriteStatus.MembershipChanged, (await f.Store.SaveLayoutAsync(f.Workspace, positions, [f.Node, second])).Status);
        Assert.Equal(new NodePosition(3, 4), Assert.Single((await f.Store.LoadNetworkAsync(f.Workspace)).SavedPositions).Value);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM pragma_foreign_key_check"));
    }

    // Catches checking a stale graph fingerprint rather than the actual node membership.
    [Fact]
    public async Task Membership_changes_reject_save_but_metadata_only_changes_allow_it()
    {
        await using var f = await SummaryStorageFixture.Open(); var old = await f.Store.LoadNetworkAsync(f.Workspace);
        f.Sql("UPDATE summary_node SET display_name='renamed' WHERE id=$id", ("$id", f.Node));
        f.Sql("UPDATE summary_read_state SET is_read=1 WHERE version_id=$id", ("$id", f.First));
        Assert.NotEqual(old.Graph.DataVersion, (await f.Store.LoadNetworkAsync(f.Workspace)).Graph.DataVersion);
        Assert.Equal(LayoutWriteStatus.Applied, (await f.Store.SaveLayoutAsync(f.Workspace, Positions((f.Node, 1, 2)), [f.Node])).Status);
        await AddNode(f);
        Assert.Equal(LayoutWriteStatus.MembershipChanged, (await f.Store.SaveLayoutAsync(f.Workspace, Positions((f.Node, 8, 9)), [f.Node])).Status);
        Assert.Equal(new NodePosition(1, 2), (await f.Store.LoadNetworkAsync(f.Workspace)).SavedPositions[f.Node]);
    }

    [Theory]
    [InlineData(double.NaN, 0)][InlineData(double.PositiveInfinity, 0)][InlineData(0, double.NegativeInfinity)]
    [InlineData(1_000_000_001, 0)][InlineData(0, -1_000_000_001)]
    public async Task Invalid_coordinates_are_rejected_before_any_write(double x, double y)
    {
        await using var f = await SummaryStorageFixture.Open(); var before = f.DatabaseSnapshot();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Store.TrySaveInitialNodePositionAsync(f.Workspace, f.Node, new(x, y)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Store.SaveLayoutAsync(f.Workspace, Positions((f.Node, x, y)), [f.Node]));
        Assert.Equal(before, f.DatabaseSnapshot());
    }

    [Fact]
    public async Task Database_constraints_reject_invalid_positions_and_foreign_workspace_ownership()
    {
        await using var f = await SummaryStorageFixture.Open(); var other = (await f.Store.CreateAsync("other")).Id;
        Assert.Equal(LayoutWriteStatus.NodeMissing, (await f.Store.TrySaveInitialNodePositionAsync(other, f.Node, new(1, 2))).Status);
        Assert.Equal(LayoutWriteStatus.MembershipChanged, (await f.Store.SaveLayoutAsync(other, Positions((f.Node, 1, 2)), [f.Node])).Status);
        foreach (var coordinate in new[] { "1000000001", "-1000000001", "1e999", "NULL", "'wrong'" })
            Assert.Throws<SqliteException>(() => f.Sql($"INSERT INTO node_layout VALUES($workspace,$node,{coordinate},0)", ("$workspace", f.Workspace), ("$node", f.Node)));
        Assert.Throws<SqliteException>(() => f.Sql("INSERT INTO node_layout VALUES($workspace,$node,0,0)", ("$workspace", other), ("$node", f.Node)));
        Assert.Equal(LayoutWriteStatus.Applied, (await f.Store.TrySaveInitialNodePositionAsync(f.Workspace, f.Node, new(-1e9, 1e9))).Status);
    }

    [Fact]
    public async Task Duplicate_or_mismatched_save_members_are_rejected_without_partial_rows()
    {
        await using var f = await SummaryStorageFixture.Open();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.SaveLayoutAsync(f.Workspace, Positions((f.Node, 1, 2)), [f.Node, f.Node]));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.SaveLayoutAsync(f.Workspace, Positions((f.Node, 1, 2)), []));
        Assert.Empty((await f.Store.LoadNetworkAsync(f.Workspace)).SavedPositions);
        var empty = (await f.Store.CreateAsync("empty")).Id;
        Assert.Equal(LayoutWriteStatus.Applied, (await f.Store.SaveLayoutAsync(empty, ImmutableDictionary<Guid, NodePosition>.Empty, [])).Status);
    }

    // SQL6 is a frozen migration contract, while SQL7 adds rather than rewrites it.
    [Fact]
    public void Schema_six_remains_frozen()
    {
        Assert.Equal("ECA38643CEE6137AF4E6B01BCE2BF52C2B5024BE823167FBC5E37DC909D1B0F9", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DatabaseSchema.Migrations[5].Sql))));
    }
}
