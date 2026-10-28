using System.Collections.Immutable;
using System.Diagnostics;
using Mindweave.Application.Network;
using Mindweave.Core.Network;
using Mindweave.Desktop.Network;
using Xunit;

namespace Mindweave.Tests;

public sealed class NetworkLayoutTests(ITestOutputHelper output)
{
    private readonly NetworkLayoutEngine engine = new();
    private static readonly ImmutableDictionary<Guid, NodeExtent> DefaultExtents = ImmutableDictionary<Guid, NodeExtent>.Empty;
    private static Guid Id(int id) => Guid.Parse($"00000000-0000-0000-0000-{id:D12}");
    private static NetworkNode Node(int id, int root = 1, int? conversation = null) => new(
        new(Id(id), Id(conversation ?? id), "中文标题", "base", DateTimeOffset.UnixEpoch.AddMinutes(id), false, false, false), Id(root));
    private static NetworkGraphSnapshot Graph(NetworkNode[] nodes, params (int Parent, int Child)[] edges) => new(Id(9999), "test",
        nodes.ToImmutableArray(), edges.Select(e => new NetworkEdge(Id(e.Parent), Id(e.Child), [])).ToImmutableArray());
    private static KeyValuePair<Guid, NodePosition>[] Ordered(ImmutableDictionary<Guid, NodePosition> positions) => positions.OrderBy(p => p.Key).ToArray();

    // Catches accidental dependence on collection/hash enumeration in sector allocation or collision repair.
    [Fact]
    public void Arrange_is_identical_for_shuffled_nodes_edges_and_extents()
    {
        var graph = Graph([Node(1), Node(2), Node(3), Node(4), Node(5, 5), Node(6, 5)], (1, 2), (1, 3), (2, 4), (5, 6));
        var extents = graph.Nodes.ToImmutableDictionary(n => n.Metadata.Id, n => new NodeExtent(200 + n.Metadata.Id.ToByteArray()[15], 112));
        var expected = engine.Arrange(graph, extents);
        var random = new Random(734);
        for (var i = 0; i < 30; i++)
        {
            var shuffled = graph with { Nodes = graph.Nodes.OrderBy(_ => random.Next()).ToImmutableArray(), Edges = graph.Edges.OrderBy(_ => random.Next()).ToImmutableArray() };
            var shuffledExtents = extents.OrderBy(_ => random.Next()).ToImmutableDictionary();
            Assert.Equal(Ordered(expected), Ordered(engine.Arrange(shuffled, shuffledExtents)));
        }
    }

    // Catches equal-child-angle layouts that ignore the larger descendant subtree.
    [Fact]
    public void Radial_sector_width_tracks_subtree_size_before_collision_repair()
    {
        var graph = Graph([Node(1), Node(2), Node(3), Node(4), Node(5), Node(6)], (1, 2), (1, 3), (2, 4), (2, 5), (2, 6));
        var positions = engine.Arrange(graph, DefaultExtents, new(CollisionPasses: 0));
        var origin = positions[Id(1)];
        var large = positions[Id(2)]; var small = positions[Id(3)];
        var dot = (large.X - origin.X) * (small.X - origin.X) + (large.Y - origin.Y) * (small.Y - origin.Y);
        var cross = (large.X - origin.X) * (small.Y - origin.Y) - (large.Y - origin.Y) * (small.X - origin.X);
        // Weights 4:1 yield child sector centers 180 degrees apart; grandchildren span the larger 288-degree sector.
        Assert.True(dot < 0, $"Child vectors: ({large.X - origin.X}, {large.Y - origin.Y}), ({small.X - origin.X}, {small.Y - origin.Y}); dot={dot}, cross={cross}");
        Assert.InRange(Math.Abs(cross), 0, 1e-8);
        var grandchildren = new[] { 4, 5, 6 }.Select(id => positions[Id(id)]).ToArray();
        Assert.True(grandchildren.Max(p => p.X) - grandchildren.Min(p => p.X) > 300);
        Assert.True(grandchildren.Max(p => p.Y) - grandchildren.Min(p => p.Y) > 300);
    }

    // Catches dropping invisible-root siblings, false graph roots, and center-only group separation.
    [Fact]
    public void Independent_roots_and_unsummarized_root_groups_have_extent_gap()
    {
        var graph = Graph([Node(2, 1), Node(3, 1), Node(8, 8), Node(9, 8)], (8, 9));
        var extents = ImmutableDictionary<Guid, NodeExtent>.Empty.Add(Id(3), new(1200, 180));
        var positions = engine.Arrange(graph, extents);
        Assert.Equal(4, positions.Count);
        var firstRight = new[] { 2, 3 }.Max(id => positions[Id(id)].X + Extent(Id(id), extents).Width / 2);
        var secondLeft = new[] { 8, 9 }.Min(id => positions[Id(id)].X - Extent(Id(id), extents).Width / 2);
        Assert.True(secondLeft - firstRight >= 320 - 1e-8);
        AssertNoCollisions(positions, extents);
    }

    // Catches treating long measured Chinese titles as the default node width.
    [Fact]
    public void Measured_title_extents_and_custom_collision_gap_prevent_rectangle_overlap()
    {
        var graph = Graph(Enumerable.Range(1, 20).Select(i => Node(i)).ToArray(), Enumerable.Range(2, 19).Select(i => (1, i)).ToArray());
        var extents = graph.Nodes.ToImmutableDictionary(n => n.Metadata.Id, _ => new NodeExtent(1600, 240));
        var positions = engine.Arrange(graph, extents, new(CollisionGap: 65));
        AssertNoCollisions(positions, extents, 65);
    }

    // Catches re-arranging any saved value or placing new descendants around the global origin.
    [Fact]
    public void PlaceMissing_preserves_each_saved_value_and_uses_nearest_saved_source()
    {
        var graph = Graph([Node(1), Node(2), Node(3), Node(4)], (1, 2), (2, 3), (2, 4));
        var saved = ImmutableDictionary<Guid, NodePosition>.Empty.Add(Id(1), new(-7000, -9000)).Add(Id(2), new(12345.25, 6789.5));
        var result = engine.PlaceMissing(graph, saved, DefaultExtents);
        foreach (var pair in saved) Assert.Equal(pair.Value, result[pair.Key]);
        foreach (var id in new[] { 3, 4 })
        {
            Assert.InRange(Math.Abs(result[Id(id)].X - saved[Id(2)].X), 0, 1000);
            Assert.InRange(Math.Abs(result[Id(id)].Y - saved[Id(2)].Y), 0, 1000);
        }
        AssertNoCollisions(result, DefaultExtents);
        Assert.Equal(Ordered(result), Ordered(engine.PlaceMissing(graph with { Nodes = graph.Nodes.Reverse().ToImmutableArray(), Edges = graph.Edges.Reverse().ToImmutableArray() }, saved.Reverse().ToImmutableDictionary(), DefaultExtents)));
        Assert.Equal(Ordered(result), Ordered(engine.PlaceMissing(graph, result, DefaultExtents)));
    }

    // Catches shifting fixed saved nodes to resolve collisions, and an unbounded source search when blocked.
    [Fact]
    public void Blocked_missing_node_uses_finite_fallback_without_moving_saved_nodes()
    {
        var graph = Graph([Node(1), Node(2), Node(3)], (1, 3));
        var saved = ImmutableDictionary<Guid, NodePosition>.Empty.Add(Id(1), new(0, 0)).Add(Id(2), new(0, 0));
        var extents = DefaultExtents.Add(Id(2), new(20000, 20000));
        var positions = engine.PlaceMissing(graph, saved, extents);
        Assert.Equal(saved[Id(1)], positions[Id(1)]); Assert.Equal(saved[Id(2)], positions[Id(2)]);
        Assert.True(Math.Abs(positions[Id(3)].X) >= 10124 || Math.Abs(positions[Id(3)].Y) >= 10080);
        Assert.True(positions[Id(3)].IsValid);
    }

    // Catches overflow from outward placement around a valid coordinate at the inclusive limit.
    [Fact]
    public void Missing_node_near_coordinate_limit_finds_an_inward_position()
    {
        var graph = Graph([Node(1), Node(2)], (1, 2));
        var saved = ImmutableDictionary<Guid, NodePosition>.Empty.Add(Id(1), new(1e9, 1e9));
        var positions = engine.PlaceMissing(graph, saved, DefaultExtents);
        Assert.Equal(saved[Id(1)], positions[Id(1)]);
        Assert.All(positions.Values, p => Assert.True(p.IsValid));
        AssertNoCollisions(positions, DefaultExtents);
    }

    [Fact]
    public void Empty_and_single_node_graphs_return_only_real_nodes()
    {
        Assert.Empty(engine.Arrange(Graph([]), DefaultExtents));
        Assert.Empty(engine.PlaceMissing(Graph([]), ImmutableDictionary<Guid, NodePosition>.Empty, DefaultExtents));
        var positions = engine.Arrange(Graph([Node(1)]), DefaultExtents);
        Assert.Equal(new NodePosition(0, 0), Assert.Single(positions).Value);
    }

    // Catches recursive stack risk, unbounded collision loops, non-finite results or silently dropped dense nodes.
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Five_hundred_node_chain_or_wide_tree_finishes_with_all_finite_nonoverlapping_nodes(bool wide)
    {
        var graph = Graph(Enumerable.Range(1, 500).Select(i => Node(i)).ToArray(), Enumerable.Range(2, 499).Select(i => (wide ? 1 : i - 1, i)).ToArray());
        var watch = Stopwatch.StartNew();
        var arranged = engine.Arrange(graph, DefaultExtents);
        watch.Stop(); output.WriteLine($"500-node {(wide ? "wide" : "chain")} Arrange: {watch.Elapsed.TotalMilliseconds:F2} ms");
        Assert.Equal(500, arranged.Count); Assert.All(arranged.Values, p => Assert.True(p.IsValid));
        AssertNoCollisions(arranged, DefaultExtents);
        watch.Restart();
        var missing = engine.PlaceMissing(graph, ImmutableDictionary<Guid, NodePosition>.Empty.Add(Id(1), new(5000, 5000)), DefaultExtents);
        watch.Stop(); output.WriteLine($"500-node {(wide ? "wide" : "chain")} PlaceMissing: {watch.Elapsed.TotalMilliseconds:F2} ms");
        Assert.Equal(500, missing.Count); Assert.Equal(new(5000, 5000), missing[Id(1)]);
        Assert.All(missing.Values, p => Assert.True(p.IsValid)); AssertNoCollisions(missing, DefaultExtents);
    }

    [Theory]
    [InlineData(double.NaN, 112)][InlineData(double.PositiveInfinity, 112)][InlineData(0, 112)][InlineData(200, -1)]
    public void Invalid_extents_are_rejected(double width, double height) => Assert.Throws<ArgumentOutOfRangeException>(() => engine.Arrange(Graph([Node(1)]), DefaultExtents.Add(Id(1), new(width, height))));

    [Fact]
    public void Invalid_options_or_saved_coordinates_are_rejected()
    {
        var graph = Graph([Node(1)]);
        foreach (var options in new[] { new NetworkLayoutOptions(RadialGap: double.NaN), new(RadialGap: 0), new(GroupGap: -1), new(CollisionGap: double.PositiveInfinity), new(CollisionPasses: -1), new(CollisionPasses: 25) })
            Assert.Throws<ArgumentOutOfRangeException>(() => engine.Arrange(graph, DefaultExtents, options));
        foreach (var position in new[] { new NodePosition(double.NaN, 0), new(0, double.NegativeInfinity), new(1e9 + 1, 0) })
            Assert.Throws<ArgumentOutOfRangeException>(() => engine.PlaceMissing(graph, ImmutableDictionary<Guid, NodePosition>.Empty.Add(Id(1), position), DefaultExtents));
    }

    [Fact]
    public void Cyclic_input_is_rejected_instead_of_looping() => Assert.Throws<ArgumentException>(() => engine.Arrange(Graph([Node(1), Node(2)], (1, 2), (2, 1)), DefaultExtents));

    private static NodeExtent Extent(Guid id, ImmutableDictionary<Guid, NodeExtent> extents) => extents.GetValueOrDefault(id, new(200, 112));
    private static void AssertNoCollisions(ImmutableDictionary<Guid, NodePosition> positions, ImmutableDictionary<Guid, NodeExtent> extents, double gap = 24)
    {
        var pairs = positions.ToArray();
        for (var i = 0; i < pairs.Length; i++)
            for (var j = i + 1; j < pairs.Length; j++)
            {
                var a = pairs[i]; var b = pairs[j]; var ae = Extent(a.Key, extents); var be = Extent(b.Key, extents);
                Assert.True(Math.Abs(a.Value.X - b.Value.X) + 1e-7 >= (ae.Width + be.Width) / 2 + gap || Math.Abs(a.Value.Y - b.Value.Y) + 1e-7 >= (ae.Height + be.Height) / 2 + gap, $"Collision {a.Key} / {b.Key}");
            }
    }
}
