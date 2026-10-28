using Mindweave.Desktop.Network;
using Mindweave.Desktop.ViewModels;
using Xunit;
using static Mindweave.Tests.NetworkSessionTests;

namespace Mindweave.Tests;

public sealed class NetworkViewModelTests
{
    // Searching must leave the full graph visible and cycle through matches in both title fields.
    [Fact]
    public async Task Search_highlights_without_filtering_and_cycles_camera_and_selection()
    {
        var session = new NetworkSession(Workspace, new ControlledStore(Snapshot(1, 2)));
        using var vm = new NetworkViewModel(session, _ => Task.CompletedTask); await session.ReloadAsync();
        vm.Search = "Base 2"; Assert.Equal(2, vm.Nodes.Count); Assert.Single(vm.Nodes, n => n.IsSearchMatch);
        vm.FindNextCommand.Execute(null); Assert.Equal(Id(12), session.SelectedNodeId); Assert.Equal(2000, session.Viewport.CenterX);
        vm.Search = "title"; vm.FindNextCommand.Execute(null); Assert.Equal(Id(11), session.SelectedNodeId);
        vm.FindNextCommand.Execute(null); Assert.Equal(Id(12), session.SelectedNodeId);
        vm.FindNextCommand.Execute(null); Assert.Equal(Id(11), session.SelectedNodeId);
        Assert.False(session.IsDirty);
    }

    // Selection presents source names and separate status text without routing or mutating stored read state.
    [Fact]
    public async Task Only_open_routes_and_selection_preserves_unread_with_source_metadata()
    {
        await using var f = await SummaryStorageFixture.Open();
        f.Sql("UPDATE conversation SET title='真实来源名称' WHERE id=$id", ("$id", f.Conversation));
        var session = new NetworkSession(f.Workspace, f.Store); Guid? opened = null;
        using var vm = new NetworkViewModel(session, id => { opened = id; return Task.CompletedTask; });
        await session.ReloadAsync(); var before = f.DatabaseSnapshot(); vm.SelectedNode = vm.Nodes[0];
        Assert.Equal("真实来源名称", vm.SelectedNode.ConversationTitle); Assert.True(vm.SelectedNode.IsUnread);
        Assert.Contains("未读", vm.SelectedNode.StatusText); Assert.Null(opened); Assert.Equal(before, f.DatabaseSnapshot());
        await vm.OpenSummaryCommand.ExecuteAsync(null); Assert.Equal(f.Node, opened); Assert.Equal(before, f.DatabaseSnapshot());
    }

    // Per-node metadata must survive reload and remain distinguishable without color.
    [Fact]
    public async Task Candidate_later_flags_and_path_are_text_and_selection_tracks_reload()
    {
        var store = new ControlledStore(Snapshot(1, 2)); var graph = store.Current.Graph;
        store.Current = store.Current with { Graph = graph with { Nodes = [graph.Nodes[0], graph.Nodes[1] with { RootConversationId = Id(1) }], Edges = [new(Id(11), Id(12), [new(Id(1), "parent"), new(Id(3), "hidden"), new(Id(2), "child")])] } };
        var session = new NetworkSession(Workspace, store); using var vm = new NetworkViewModel(session, _ => Task.CompletedTask);
        await session.ReloadAsync(); vm.SelectedNode = vm.Nodes.Single(n => n.Id == Id(12));
        Assert.Contains("候选", vm.SelectedNode.StatusText); Assert.Contains("后续内容", vm.SelectedNode.StatusText);
        Assert.Contains("hidden", vm.SelectedNode.PathDescription); Assert.Contains("1", vm.SelectedNode.PathDescription);
        store.Current = Snapshot(1); await session.ReloadAsync(); Assert.Null(vm.SelectedNode); Assert.Single(vm.Nodes);
    }
}
