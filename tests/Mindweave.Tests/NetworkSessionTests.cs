using System.Collections.Immutable;
using Mindweave.Application.Network;
using Mindweave.Core.Network;
using Mindweave.Desktop.Network;
using Xunit;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Mindweave.Tests;

public sealed class NetworkSessionTests
{
    internal static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    internal static readonly Guid Workspace = Id(99);
    internal static NetworkWorkspaceSnapshot Snapshot(params int[] nodes)
    {
        var time = DateTimeOffset.Parse("2026-10-28T00:00:00Z");
        var graph = NetworkProjection.Build(Workspace,
            nodes.Select(i => new NetworkConversation(Id(i), null, $"Source {i}", time)).ToImmutableArray(),
            nodes.Select(i => new NetworkNodeMetadata(Id(i + 10), Id(i), $"Title {i}", $"Base {i}", time, true, i == 2, i == 2)).ToImmutableArray());
        return new(graph, nodes.ToImmutableDictionary(i => Id(i + 10), i => new NodePosition(i * 1000, 500)));
    }
    internal static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static ImmutableDictionary<Guid, NodePosition> Point(Guid id, double x, double y) => ImmutableDictionary<Guid, NodePosition>.Empty.Add(id, new(x, y));

    // Removing either generation guard would let old reads/layout overwrite newer graph membership.
    [Fact]
    public async Task Reversed_reload_completion_and_late_arrange_cannot_restore_old_graph()
    {
        var store = new ControlledStore(Snapshot(1)); var layout = Gate<ImmutableDictionary<Guid, NodePosition>>();
        var session = new NetworkSession(Workspace, store, arrangeAsync: (_, _) => layout.Task);
        await session.ReloadAsync(); Assert.NotEmpty(session.Graph.Nodes); var arranging = session.ArrangeAsync();
        var older = Gate<NetworkWorkspaceSnapshot>(); var newer = Gate<NetworkWorkspaceSnapshot>();
        store.Load = () => older.Task; var first = session.ReloadAsync();
        store.Load = () => newer.Task; var second = session.ReloadAsync();
        newer.SetResult(Snapshot(2)); await second;
        older.SetResult(Snapshot(1)); await first;
        layout.SetResult(Point(Id(11), 999, 999)); await arranging;
        Assert.Equal(Id(12), Assert.Single(session.Graph.Nodes).Metadata.Id);
        Assert.Equal(new NodePosition(2000, 500), Assert.Single(session.Positions).Value);
        Assert.False(session.IsDirty);
    }

    // Old snapshots must not clear a later explicit arrangement, even if it computes identical points.
    [Fact]
    public async Task Save_keeps_newer_revision_dirty_and_busy_double_click_submits_once()
    {
        var store = new ControlledStore(Snapshot(1)); var session = new NetworkSession(Workspace, store);
        await session.ReloadAsync(); Assert.NotEmpty(session.Graph.Nodes); await session.ArrangeAsync();
        var gate = Gate<LayoutWriteResult>(); store.Save = (_, _) => gate.Task;
        var saving = session.SaveAsync(); var savedRevision = session.Revision;
        Assert.True(session.IsSaving); Assert.False(await session.SaveAsync());
        await session.ArrangeAsync(); Assert.True(session.Revision > savedRevision);
        gate.SetResult(new(LayoutWriteStatus.Applied)); Assert.True(await saving);
        Assert.True(session.IsDirty); Assert.False(session.IsSaving); Assert.Equal(1, store.SaveCalls);
    }

    // Initial writes must use persisted reference points, never the dirty arrangement, and commit only missing rows.
    [Fact]
    public async Task New_node_uses_saved_reference_and_discard_keeps_its_independent_commit()
    {
        var store = new ControlledStore(Snapshot(1)); var session = new NetworkSession(Workspace, store);
        await session.ReloadAsync(); Assert.NotEmpty(session.Graph.Nodes); await session.ArrangeAsync();
        Assert.Equal(new NodePosition(0, 0), session.Positions[Id(11)]);
        store.Current = Snapshot(1, 2) with { SavedPositions = Snapshot(1).SavedPositions };
        await session.ReloadAsync();
        Assert.Equal(new NodePosition(0, 0), session.Positions[Id(11)]);
        Assert.Single(store.InitialWrites); Assert.Equal(Id(12), store.InitialWrites[0].Id);
        Assert.Equal(new NodePosition(1000, 500), store.Current.SavedPositions[Id(11)]);
        Assert.Equal(new NodePosition(260, 0), session.SavedPositions[Id(12)]);
        session.Discard();
        Assert.Equal(new NodePosition(1000, 500), session.Positions[Id(11)]);
        Assert.Equal(new NodePosition(260, 0), session.Positions[Id(12)]); Assert.False(session.IsDirty);
    }

    // Quiet automatic failure must not create leave blockers, nor retry on every metadata notification.
    [Fact]
    public async Task Automatic_write_failure_is_quiet_and_retries_only_on_next_entry()
    {
        var store = new ControlledStore(Snapshot(1) with { SavedPositions = ImmutableDictionary<Guid, NodePosition>.Empty });
        store.Initial = (_, _) => throw new IOException("synthetic");
        var session = new NetworkSession(Workspace, store); await session.ReloadAsync();
        Assert.Single(session.Positions); Assert.Empty(session.SavedPositions); Assert.False(session.IsDirty); Assert.Null(session.ErrorMessage);
        await session.ReloadAsync(); Assert.Single(store.InitialWrites);
        store.Initial = null; await session.ReloadAsync(fitAll: true);
        Assert.Equal(2, store.InitialWrites.Count); Assert.Single(session.SavedPositions); Assert.False(session.IsDirty);
    }

    // A metadata refresh must preserve pending points and selection and still expose the new metadata.
    [Fact]
    public async Task Metadata_refresh_preserves_dirty_positions_and_selection()
    {
        var store = new ControlledStore(Snapshot(1)); var session = new NetworkSession(Workspace, store);
        await session.ReloadAsync(); session.SelectedNodeId = Id(11); await session.ArrangeAsync(); var revision = session.Revision;
        var node = store.Current.Graph.Nodes[0];
        store.Current = store.Current with { Graph = store.Current.Graph with { DataVersion = "renamed", Nodes = [node with { Metadata = node.Metadata with { DisplayName = "renamed", IsUnread = false } }] } };
        await session.ReloadAsync();
        Assert.Equal("renamed", session.Graph.Nodes[0].Metadata.DisplayName); Assert.False(session.Graph.Nodes[0].Metadata.IsUnread);
        Assert.Equal(new NodePosition(0, 0), session.Positions[Id(11)]); Assert.Equal(Id(11), session.SelectedNodeId);
        Assert.Equal(revision, session.Revision); Assert.True(session.IsDirty);
    }

    // A late save rejected for deletion must prune both maps and selection instead of resurrecting the node.
    [Fact]
    public async Task Membership_rejection_refreshes_deleted_nodes_without_losing_remaining_dirty_layout()
    {
        var store = new ControlledStore(Snapshot(1, 2)); var session = new NetworkSession(Workspace, store);
        await session.ReloadAsync(); session.SelectedNodeId = Id(11); await session.ArrangeAsync();
        var retained = session.Positions[Id(12)]; var gate = Gate<LayoutWriteResult>(); store.Save = (_, _) => gate.Task;
        var saving = session.SaveAsync(); store.Current = Snapshot(2); gate.SetResult(new(LayoutWriteStatus.MembershipChanged));
        Assert.False(await saving); Assert.Null(session.SelectedNodeId);
        Assert.Equal(new NodePosition(retained.X, retained.Y), session.Positions[Id(12)]);
        Assert.False(session.Positions.ContainsKey(Id(11))); Assert.False(session.SavedPositions.ContainsKey(Id(11))); Assert.True(session.IsDirty);
    }

    // Real SQLite rollback plus session retention proves an IO failure cannot silently discard either version.
    [Fact]
    public async Task Explicit_save_failure_preserves_real_database_and_memory_then_retries()
    {
        await using var f = await SummaryStorageFixture.Open();
        await f.Store.TrySaveInitialNodePositionAsync(f.Workspace, f.Node, new(1234, 5678));
        var session = new NetworkSession(f.Workspace, f.Store); await session.ReloadAsync(); Assert.NotEmpty(session.Graph.Nodes); await session.ArrangeAsync();
        f.Sql("CREATE TRIGGER reject_layout BEFORE UPDATE ON node_layout BEGIN SELECT RAISE(ABORT,'synthetic'); END");
        Assert.False(await session.SaveAsync()); Assert.True(session.IsDirty);
        Assert.Equal("布局保存失败，改动仍保留在当前窗口。", session.ErrorMessage);
        Assert.Equal(new NodePosition(0, 0), session.Positions[f.Node]);
        Assert.Equal(new NodePosition(1234, 5678), (await f.Store.LoadNetworkAsync(f.Workspace)).SavedPositions[f.Node]);
        f.Sql("DROP TRIGGER reject_layout"); Assert.True(await session.SaveAsync()); Assert.False(session.IsDirty); Assert.Null(session.ErrorMessage);
        Assert.Equal(new NodePosition(0, 0), (await f.Store.LoadNetworkAsync(f.Workspace)).SavedPositions[f.Node]);
    }

    // Quiesce stops accepted reads from spawning new writes and drain includes their still-running work.
    [Fact]
    public async Task Quiesce_drains_accepted_read_and_resume_does_not_revive_its_result()
    {
        var store = new ControlledStore(Snapshot(1)); var gate = Gate<NetworkWorkspaceSnapshot>(); store.Load = () => gate.Task;
        var session = new NetworkSession(Workspace, store); var loading = session.ReloadAsync(); session.Quiesce();
        var drained = session.WaitForPendingOperationsAsync(); Assert.False(drained.IsCompleted);
        await session.ReloadAsync(); await session.ArrangeAsync(); Assert.False(await session.SaveAsync());
        session.Resume(); gate.SetResult(Snapshot(1) with { SavedPositions = ImmutableDictionary<Guid, NodePosition>.Empty });
        await loading; await drained; Assert.Empty(session.Graph.Nodes); Assert.Empty(store.InitialWrites); Assert.Equal(1, store.LoadCalls);
        store.Load = null; await session.ReloadAsync(); Assert.Single(session.Graph.Nodes);
    }

    // Accepted insert IO must finish before disposal; quiescing may not silently abandon it.
    [Fact]
    public async Task Quiesce_waits_for_accepted_initial_write_and_prevents_next_node_write()
    {
        var store = new ControlledStore(Snapshot(1, 2) with { SavedPositions = ImmutableDictionary<Guid, NodePosition>.Empty });
        var gate = Gate<LayoutWriteResult>(); var started = Gate<bool>();
        store.Initial = (_, _) => { started.TrySetResult(true); return gate.Task; };
        var session = new NetworkSession(Workspace, store); var loading = session.ReloadAsync(); Assert.False(loading.IsCompleted); await started.Task;
        session.Quiesce(); var drained = session.WaitForPendingOperationsAsync(); Assert.False(drained.IsCompleted);
        gate.SetResult(new(LayoutWriteStatus.Applied)); await loading; await drained; Assert.Single(store.InitialWrites);
    }

    // Delayed fit intent must survive zero dimensions, while resize and ordinary reload preserve the camera.
    [Fact]
    public async Task Fit_is_deferred_until_measured_and_resize_metadata_and_selection_do_not_dirty()
    {
        var store = new ControlledStore(Snapshot(1)); var session = new NetworkSession(Workspace, store);
        await session.ReloadAsync(fitAll: true); Assert.True(session.IsFitPending);
        session.SetViewportSize(800, 600); Assert.False(session.IsFitPending);
        Assert.Equal(new NetworkViewport(1000, 500, 2.5), session.Viewport);
        session.Viewport = new(300, 400, 1); session.SelectedNodeId = Id(11);
        session.SetViewportSize(400, 300); await session.ReloadAsync();
        Assert.Equal(new NetworkViewport(300, 400, 1), session.Viewport); Assert.False(session.IsDirty);
        session.ShowAll(); Assert.Equal(new NetworkViewport(1000, 500, 1.52), session.Viewport);
        await session.ArrangeAsync(); Assert.Equal(0, session.Viewport.CenterX); Assert.Equal(0, session.Viewport.CenterY);
    }

    // An automatic write failure must not turn ordinary metadata/new-node refresh into implicit rearrange.
    [Fact]
    public async Task Refresh_keeps_existing_fallback_position_and_camera_when_another_node_arrives()
    {
        var store = new ControlledStore(Snapshot(1) with { SavedPositions = ImmutableDictionary<Guid, NodePosition>.Empty });
        store.Initial = (_, _) => throw new IOException("synthetic");
        var session = new NetworkSession(Workspace, store); await session.ReloadAsync();
        Assert.Equal(new NodePosition(0, 0), session.Positions[Id(11)]);
        session.Viewport = new(123, 456, .75);
        store.Current = Snapshot(1, 2) with { SavedPositions = ImmutableDictionary<Guid, NodePosition>.Empty };
        store.Initial = null; await session.ReloadAsync();
        Assert.Equal(new NodePosition(0, 0), session.Positions[Id(11)]);
        Assert.Equal(new NetworkViewport(123, 456, .75), session.Viewport); Assert.False(session.IsDirty);
        Assert.Equal(new NodePosition(260, 0), store.Current.SavedPositions[Id(12)]);
        Assert.False(store.Current.SavedPositions.ContainsKey(Id(11)));
    }

    // With no nodes there is no editable layout to save or prompt about.
    [Fact]
    public async Task Empty_arrange_does_not_create_a_leave_blocker()
    {
        var session = new NetworkSession(Workspace, new ControlledStore(Snapshot()));
        await session.ReloadAsync(); await session.ArrangeAsync();
        Assert.False(session.IsDirty); Assert.Equal(0, session.Revision);
    }

    // Read snapshots that predate a successful save must never roll back its in-memory saved baseline.
    [Fact]
    public async Task Read_started_before_save_reloads_persisted_positions_after_commit()
    {
        var store = new ControlledStore(Snapshot(1)); var session = new NetworkSession(Workspace, store);
        await session.ReloadAsync(); await session.ArrangeAsync();
        var old = store.Current; var read = Gate<NetworkWorkspaceSnapshot>(); store.Load = () => read.Task;
        var loading = session.ReloadAsync(); Assert.True(await session.SaveAsync());
        store.Load = null; read.SetResult(old); await loading;
        Assert.Equal(new NodePosition(0, 0), session.SavedPositions[Id(11)]);
        Assert.Equal(new NodePosition(0, 0), session.Positions[Id(11)]); Assert.False(session.IsDirty);
    }

    // Close drains accepted full-layout writes, even after Quiesce rejects new operations.
    [Fact]
    public async Task Quiesce_drains_accepted_save_and_keeps_committed_baseline_for_cancelled_close()
    {
        var store = new ControlledStore(Snapshot(1)); var session = new NetworkSession(Workspace, store);
        await session.ReloadAsync(); await session.ArrangeAsync();
        var write = Gate<LayoutWriteResult>(); store.Save = (_, _) => write.Task;
        var saving = session.SaveAsync(); session.Quiesce(); var drained = session.WaitForPendingOperationsAsync();
        Assert.False(drained.IsCompleted); write.SetResult(new(LayoutWriteStatus.Applied));
        Assert.True(await saving); await drained; session.Resume();
        Assert.False(session.IsDirty); Assert.Equal(new NodePosition(0, 0), session.SavedPositions[Id(11)]);
        Assert.False(session.IsQuiesced);
    }

    // A cancelled close permits new user work but must not revive follow-up reads from an older save.
    [Fact]
    public async Task Old_save_membership_failure_does_not_restart_loading_after_quiesce_and_resume()
    {
        var store = new ControlledStore(Snapshot(1)); var session = new NetworkSession(Workspace, store);
        await session.ReloadAsync(); await session.ArrangeAsync();
        var write = Gate<LayoutWriteResult>(); store.Save = (_, _) => write.Task;
        var saving = session.SaveAsync(); session.Quiesce(); session.Resume(); store.Current = Snapshot();
        write.SetResult(new(LayoutWriteStatus.MembershipChanged)); Assert.False(await saving);
        Assert.Equal(1, store.LoadCalls); Assert.True(session.IsDirty);
        await session.ReloadAsync(); Assert.Empty(session.Graph.Nodes);
    }

    // Superseding a reload queued behind a save must not consume a node's automatic insert attempt.
    [Fact]
    public async Task Superseded_initial_write_waiter_does_not_block_latest_reload_from_saving_new_node()
    {
        var store = new ControlledStore(Snapshot(1)); var session = new NetworkSession(Workspace, store);
        await session.ReloadAsync(); await session.ArrangeAsync();
        var write = Gate<LayoutWriteResult>(); store.Save = (_, _) => write.Task;
        var saving = session.SaveAsync(); store.Current = Snapshot(1, 2) with { SavedPositions = Snapshot(1).SavedPositions };
        var applied = Gate<bool>();
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NetworkSession.Graph) && session.Graph.Nodes.Length == 2) applied.TrySetResult(true);
        };
        var older = session.ReloadAsync(); await applied.Task;
        var newer = session.ReloadAsync();
        // Complete the full save with real storage's membership rejection, then let queued reads/inserts drain.
        write.SetResult(new(LayoutWriteStatus.MembershipChanged));
        await saving; await older; await newer; await session.WaitForPendingOperationsAsync();
        Assert.True(store.Current.SavedPositions.ContainsKey(Id(12)));
        Assert.Single(store.InitialWrites, row => row.Id == Id(12));
    }

    // Busy notification is observable: a close triggered there must already see the accepted save in its drain.
    [Fact]
    public async Task Save_is_registered_before_busy_notification_can_trigger_close()
    {
        var store = new ControlledStore(Snapshot(1)); var session = new NetworkSession(Workspace, store);
        await session.ReloadAsync(); await session.ArrangeAsync();
        var write = Gate<LayoutWriteResult>(); store.Save = (_, _) => write.Task; Task? drained = null;
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NetworkSession.IsSaving) && session.IsSaving)
            { session.Quiesce(); drained = session.WaitForPendingOperationsAsync(); }
        };
        var saving = session.SaveAsync(); Assert.NotNull(drained); var completedEarly = drained.IsCompleted;
        write.SetResult(new(LayoutWriteStatus.Applied)); await saving; await drained;
        Assert.False(completedEarly);
    }

    // An insert rejected for a concurrently deleted node must refresh instead of displaying a ghost.
    [Fact]
    public async Task Rejected_initial_insert_prunes_deleted_node_and_selection()
    {
        var store = new ControlledStore(Snapshot(1) with { SavedPositions = ImmutableDictionary<Guid, NodePosition>.Empty });
        store.Initial = (_, _) => { store.Current = Snapshot(); return Task.FromResult(new LayoutWriteResult(LayoutWriteStatus.NodeMissing)); };
        var session = new NetworkSession(Workspace, store); await session.ReloadAsync();
        Assert.Empty(session.Graph.Nodes); Assert.Empty(session.Positions); Assert.Empty(session.SavedPositions); Assert.Null(session.SelectedNodeId);
        Assert.False(session.IsDirty); Assert.Null(session.ErrorMessage); await session.WaitForPendingOperationsAsync();
    }

    // Observable state must be applied on the UI owner after store and pure engine background awaits.
    [AvaloniaFact]
    public async Task Background_completions_notify_only_on_the_owning_ui_thread()
    {
        var store = new ControlledStore(Snapshot(1)); store.Load = () => Task.Run(() => store.Current);
        var session = new NetworkSession(Workspace, store); var threads = new List<bool>();
        session.PropertyChanged += (_, _) => threads.Add(Dispatcher.UIThread.CheckAccess());
        await session.ReloadAsync(); await session.ArrangeAsync(); await session.SaveAsync();
        Assert.NotEmpty(threads); Assert.All(threads, Assert.True); Assert.False(session.IsDirty);
    }

    internal sealed class ControlledStore(NetworkWorkspaceSnapshot current) : INetworkStore
    {
        public NetworkWorkspaceSnapshot Current = current;
        public Func<Task<NetworkWorkspaceSnapshot>>? Load;
        public Func<Guid, NodePosition, Task<LayoutWriteResult>>? Initial;
        public Func<ImmutableDictionary<Guid, NodePosition>, ImmutableArray<Guid>, Task<LayoutWriteResult>>? Save;
        public readonly List<(Guid Id, NodePosition Position)> InitialWrites = [];
        public int LoadCalls; public int SaveCalls;
        public Task<NetworkWorkspaceSnapshot> LoadNetworkAsync(Guid workspaceId) { Assert.Equal(Current.Graph.WorkspaceId, workspaceId); LoadCalls++; return Load?.Invoke() ?? Task.FromResult(Current); }
        public async Task<LayoutWriteResult> TrySaveInitialNodePositionAsync(Guid workspaceId, Guid nodeId, NodePosition position)
        {
            Assert.Equal(Current.Graph.WorkspaceId, workspaceId); InitialWrites.Add((nodeId, position));
            if (Initial is not null) return await Initial(nodeId, position);
            if (!Current.Graph.Nodes.Any(n => n.Metadata.Id == nodeId)) return new(LayoutWriteStatus.NodeMissing);
            if (Current.SavedPositions.TryGetValue(nodeId, out var existing)) return new(LayoutWriteStatus.AlreadyExists, existing);
            Current = Current with { SavedPositions = Current.SavedPositions.Add(nodeId, position) }; return new(LayoutWriteStatus.Applied);
        }
        public async Task<LayoutWriteResult> SaveLayoutAsync(Guid workspaceId, ImmutableDictionary<Guid, NodePosition> positions, ImmutableArray<Guid> expectedNodeIds)
        {
            Assert.Equal(Current.Graph.WorkspaceId, workspaceId); SaveCalls++;
            if (Save is not null) return await Save(positions, expectedNodeIds);
            if (!Current.Graph.Nodes.Select(n => n.Metadata.Id).ToHashSet().SetEquals(expectedNodeIds)) return new(LayoutWriteStatus.MembershipChanged);
            Current = Current with { SavedPositions = positions }; return new(LayoutWriteStatus.Applied);
        }
    }
}
