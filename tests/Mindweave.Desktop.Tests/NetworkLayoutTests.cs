using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Network;

namespace Mindweave.Desktop.Tests;

/// <summary>The layout module alone: deterministic, no overlapping labels, new nodes placed without moving old ones.</summary>
public class NetworkLayoutTests
{
    private static readonly Guid Trains = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Photos = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void The_same_input_always_gives_the_same_layout()
    {
        var nodes = Tree(Trains, 25);

        Assert.Equal(NetworkLayout.Arrange(nodes), NetworkLayout.Arrange(nodes));
        Assert.Equal(NetworkLayout.Arrange(nodes), NetworkLayout.Arrange([.. nodes.AsEnumerable().Reverse()]));
    }

    [Fact]
    public void Arranged_labels_do_not_overlap_and_groups_sit_side_by_side()
    {
        var nodes = Tree(Trains, 25).Concat(Tree(Photos, 8, startOrder: 100)).ToList();

        var layout = NetworkLayout.Arrange(nodes);

        AssertNoOverlap(nodes, layout);
        var trainsRight = nodes.Where(n => n.Group == Trains).Max(n => NetworkLayout.BoxRight(layout[n.Id].X, n.LabelWidth));
        var photosLeft = nodes.Where(n => n.Group == Photos).Min(n => NetworkLayout.BoxLeft(layout[n.Id].X));
        Assert.True(photosLeft > trainsRight, $"{photosLeft} <= {trainsRight}");
    }

    [Fact]
    public void A_group_whose_root_has_no_summary_rings_its_nodes_around_an_empty_centre()
    {
        var a = Node(Trains, null, 0, "蒸汽机");
        var b = Node(Trains, null, 1, "铁轨");

        var layout = NetworkLayout.Arrange([a, b]);

        Assert.All(new[] { layout[a.Id], layout[b.Id] }, p => Assert.InRange(Math.Sqrt((p.X * p.X) + (p.Y * p.Y)), 100, 400));
    }

    [Fact]
    public void A_new_node_goes_beside_its_parent_and_every_existing_position_stays()
    {
        var nodes = Tree(Trains, 10);
        var existing = NetworkLayout.Arrange(nodes);
        var parent = nodes[3];
        var added = Node(Trains, parent.Id, 50, "新的知识点");

        var placed = NetworkLayout.PlaceNew([.. nodes, added], existing);

        var position = Assert.Single(placed).Value;
        Assert.Equal(added.Id, placed.Keys.Single());
        var p = existing[parent.Id];
        var distance = Math.Sqrt(Math.Pow(position.X - p.X, 2) + Math.Pow(position.Y - p.Y, 2));
        Assert.InRange(distance, 100, 500);
        var all = existing.ToDictionary(x => x.Key, x => x.Value);
        all[added.Id] = position;
        AssertNoOverlap([.. nodes, added], all);

        // Same input, same result: a failed direct save is recomputed identically next time (MW-C12).
        Assert.Equal(placed, NetworkLayout.PlaceNew([.. nodes, added], existing));
    }

    [Fact]
    public void A_new_exploration_goes_to_the_right_of_everything()
    {
        var nodes = Tree(Trains, 6);
        var existing = NetworkLayout.Arrange(nodes);
        var other = Node(Photos, null, 60, "摄影入门");

        var placed = NetworkLayout.PlaceNew([.. nodes, other], existing);

        Assert.True(placed[other.Id].X > existing.Values.Max(p => p.X));
    }

    [Fact]
    public void Without_any_saved_position_new_nodes_get_the_full_layout()
    {
        var nodes = Tree(Trains, 6);

        Assert.Equal(NetworkLayout.Arrange(nodes), NetworkLayout.PlaceNew(nodes, new Dictionary<SummaryNodeId, NodePosition>()));
    }

    [Fact]
    public void The_label_box_runs_from_the_dot_to_the_end_of_the_label_on_its_right()
    {
        // The label is drawn to the right of the dot (S7): origin x + r + 9, so the box is asymmetric.
        Assert.Equal(100 - NetworkLayout.NodeRadius, NetworkLayout.BoxLeft(100));
        Assert.Equal(100 + NetworkLayout.NodeRadius + NetworkLayout.LabelGap + 56, NetworkLayout.BoxRight(100, NetworkLayout.LabelWidth("火车起源")));
    }

    /// <summary>Label boxes (dot on the left, label to its right) keep at least the padding apart on one axis.</summary>
    private static void AssertNoOverlap(List<LayoutNode> nodes, IReadOnlyDictionary<SummaryNodeId, NodePosition> layout)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var a = layout[nodes[i].Id];
                var b = layout[nodes[j].Id];
                var apartX = NetworkLayout.BoxLeft(b.X) - NetworkLayout.BoxRight(a.X, nodes[i].LabelWidth) >= NetworkLayout.LabelPadding - 0.01
                    || NetworkLayout.BoxLeft(a.X) - NetworkLayout.BoxRight(b.X, nodes[j].LabelWidth) >= NetworkLayout.LabelPadding - 0.01;
                var apartY = Math.Abs(a.Y - b.Y) >= NetworkLayout.NodeHeight;
                Assert.True(apartX || apartY, $"Nodes {i} and {j} overlap at {a} and {b}.");
            }
        }
    }

    /// <summary>A tree where node k's parent is node (k − 1) / 3, so three children per node.</summary>
    private static List<LayoutNode> Tree(Guid group, int count, int startOrder = 0)
    {
        var nodes = new List<LayoutNode>();
        for (var k = 0; k < count; k++)
        {
            nodes.Add(Node(group, k == 0 ? null : nodes[(k - 1) / 3].Id, startOrder + k, $"知识点{k}"));
        }

        return nodes;
    }

    private static LayoutNode Node(Guid group, SummaryNodeId? parent, int order, string label) =>
        new(new SummaryNodeId(new Guid(order + 1, 0, 0, group.ToByteArray()[8..])), parent, group, order, NetworkLayout.LabelWidth(label));
}
