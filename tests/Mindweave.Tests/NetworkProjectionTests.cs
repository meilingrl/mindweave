using System.Collections.Immutable;
using Mindweave.Application.Network;
using Xunit;

namespace Mindweave.Tests;

public sealed class NetworkProjectionTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-28T00:00:00Z");
    private static Guid Id(int i) => Guid.Parse($"00000000-0000-0000-0000-{i:D12}");
    private static NetworkConversation Conversation(int id, int? parent = null) => new(Id(id), parent is { } p ? Id(p) : null, $"C{id}", Time);
    private static NetworkNodeMetadata Node(int id, int conversation) => new(Id(id), Id(conversation), "same name", "base", Time, true, false, false);

    // Catches flattening a hidden source path into a false direct conversation relationship.
    [Fact]
    public void Nearest_summary_ancestor_keeps_unsummarized_path_and_endpoints()
    {
        var graph = NetworkProjection.Build(Id(99), [Conversation(1), Conversation(2, 1), Conversation(3, 2), Conversation(4, 3)], [Node(11, 1), Node(13, 3), Node(14, 4)]);
        Assert.Equal(3, graph.Nodes.Length);
        Assert.Equal(new[] { Id(1), Id(2), Id(3) }, graph.Edges.Single(e => e.ChildNodeId == Id(13)).Path.Select(p => p.ConversationId));
        var next = graph.Edges.Single(e => e.ChildNodeId == Id(14));
        Assert.Equal(Id(13), next.ParentNodeId);
        Assert.Equal(new[] { "C3", "C4" }, next.Path.Select(p => p.Title));
        Assert.All(graph.Nodes, n => Assert.Equal(Id(1), n.RootConversationId));
    }

    // Catches invented nodes/edges for an unsummarized root or grouping all roots together.
    [Fact]
    public void Unsummarized_root_groups_siblings_without_fake_nodes_or_edges_and_separates_roots()
    {
        var graph = NetworkProjection.Build(Id(99), [Conversation(1), Conversation(2, 1), Conversation(3, 1), Conversation(4)], [Node(12, 2), Node(13, 3), Node(14, 4)]);
        Assert.Equal(3, graph.Nodes.Length); Assert.Empty(graph.Edges);
        Assert.Equal(new[] { Id(1), Id(1), Id(4) }, graph.Nodes.Select(n => n.RootConversationId));
    }

    // Catches unstable enumeration order and a fingerprint that ignores metadata/path changes.
    [Fact]
    public void Reordered_input_has_identical_output_and_fingerprint_but_metadata_and_path_changes_do_not()
    {
        ImmutableArray<NetworkConversation> conversations = [Conversation(1), Conversation(2, 1), Conversation(3, 2)];
        ImmutableArray<NetworkNodeMetadata> nodes = [Node(11, 1), Node(13, 3)];
        var graph = NetworkProjection.Build(Id(99), conversations, nodes);
        var reordered = NetworkProjection.Build(Id(99), conversations.Reverse().ToImmutableArray(), nodes.Reverse().ToImmutableArray());
        Assert.Equal(graph.DataVersion, reordered.DataVersion); Assert.Equal(graph.Nodes, reordered.Nodes);
        Assert.Equal(graph.Edges.Select(e => (e.ParentNodeId, e.ChildNodeId)), reordered.Edges.Select(e => (e.ParentNodeId, e.ChildNodeId)));
        Assert.Equal(graph.Edges[0].Path, reordered.Edges[0].Path);
        foreach (var changed in new[] { nodes[0] with { DisplayName = "renamed" }, nodes[0] with { IsUnread = false }, nodes[0] with { HasCandidate = true }, nodes[0] with { HasLaterContent = true } })
        {
            var updated = NetworkProjection.Build(Id(99), conversations, [changed, nodes[1]]);
            Assert.NotEqual(graph.DataVersion, updated.DataVersion);
            Assert.Equal(graph.Nodes.Select(n => n.Metadata.Id), updated.Nodes.Select(n => n.Metadata.Id));
        }
        Assert.NotEqual(graph.DataVersion, NetworkProjection.Build(Id(99), [conversations[0], conversations[1] with { Title = "new path" }, conversations[2]], nodes).DataVersion);
    }

    // Missing parents also represent a parent outside the workspace's supplied conversation set.
    [Theory]
    [InlineData("parent")][InlineData("cycle")][InlineData("foreign-node")][InlineData("duplicate-conversation")][InlineData("duplicate-node")][InlineData("two-summaries")]
    public void Invalid_or_foreign_relationships_are_rejected_instead_of_guessed(string invalid)
    {
        ImmutableArray<NetworkConversation> conversations = invalid switch
        {
            "parent" => [Conversation(1, 2)], "cycle" => [Conversation(1, 2), Conversation(2, 1)],
            "duplicate-conversation" => [Conversation(1), Conversation(1)], _ => [Conversation(1)]
        };
        ImmutableArray<NetworkNodeMetadata> nodes = invalid switch
        {
            "foreign-node" => [Node(11, 2)], "duplicate-node" => [Node(11, 1), Node(11, 1)],
            "two-summaries" => [Node(11, 1), Node(12, 1)], _ => [Node(11, 1)]
        };
        Assert.Throws<InvalidDataException>(() => NetworkProjection.Build(Id(99), conversations, nodes));
    }
}
