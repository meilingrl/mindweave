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
    public void Arranged_labels_do_not_overlap_and_groups_keep_apart()
    {
        var nodes = Tree(Trains, 25).Concat(Tree(Photos, 8, startOrder: 100)).ToList();

        var layout = NetworkLayout.Arrange(nodes);

        AssertNoOverlap(nodes, layout);
        AssertGroupsApart(nodes, layout, Trains, Photos);
    }

    [Fact]
    public void Small_groups_go_in_rows_so_the_whole_comes_close_to_the_canvas_shape()
    {
        // Side by side, two small groups would be about three times as wide as high; stacked they are near the square canvas.
        var nodes = Tree(Trains, 4).Concat(Tree(Photos, 4, startOrder: 100)).ToList();

        var layout = NetworkLayout.Arrange(nodes);

        AssertNoOverlap(nodes, layout);
        AssertGroupsApart(nodes, layout, Trains, Photos);
        var trainsBottom = nodes.Where(n => n.Group == Trains).Max(n => layout[n.Id].Y);
        var photosTop = nodes.Where(n => n.Group == Photos).Min(n => layout[n.Id].Y);
        Assert.True(photosTop - trainsBottom >= NetworkLayout.GroupGap + NetworkLayout.CaptionHeadroom, $"{photosTop} − {trainsBottom}: the second group is not in a row below with room for its caption");

        // Many groups make a grid, not one long row.
        var many = Enumerable.Range(0, 9).SelectMany(g => Tree(new Guid(g + 10, 0, 0, new byte[8]), 3, startOrder: g * 10)).ToList();
        var grid = NetworkLayout.Arrange(many);
        var width = many.Max(n => NetworkLayout.BoxRight(grid[n.Id].X, n.LabelWidth)) - many.Min(n => NetworkLayout.BoxLeft(grid[n.Id].X));
        var height = many.Max(n => grid[n.Id].Y) - many.Min(n => grid[n.Id].Y);
        Assert.InRange(width / height, 0.6, 2.4);
        Assert.Equal(grid, NetworkLayout.Arrange([.. many.AsEnumerable().Reverse()]));
    }

    [Fact]
    public void A_group_whose_root_has_no_summary_rings_its_nodes_around_an_empty_centre()
    {
        var a = Node(Trains, null, 0, "蒸汽机");
        var b = Node(Trains, null, 1, "铁轨");

        var layout = NetworkLayout.Arrange([a, b]);

        // Opposite each other on the first ring, the empty centre between them.
        var distance = Math.Sqrt(Math.Pow(layout[a.Id].X - layout[b.Id].X, 2) + Math.Pow(layout[a.Id].Y - layout[b.Id].Y, 2));
        Assert.InRange(distance, 2 * NetworkLayout.LevelSpacing * 0.9, 2 * NetworkLayout.LevelSpacing * 2);
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
        Assert.Equal(100 + NetworkLayout.NodeRadius + NetworkLayout.LabelGap + (4 * NetworkLayout.LabelEm), NetworkLayout.BoxRight(100, NetworkLayout.LabelWidth("火车起源")));
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

    /// <summary>The two groups' boxes, padding included, do not overlap: apart across or apart down.</summary>
    private static void AssertGroupsApart(List<LayoutNode> nodes, IReadOnlyDictionary<SummaryNodeId, NodePosition> layout, Guid first, Guid second)
    {
        (double Left, double Right, double Top, double Bottom) Box(Guid group)
        {
            var members = nodes.Where(n => n.Group == group).ToList();
            return (members.Min(n => NetworkLayout.BoxLeft(layout[n.Id].X)), members.Max(n => NetworkLayout.BoxRight(layout[n.Id].X, n.LabelWidth)),
                members.Min(n => layout[n.Id].Y), members.Max(n => layout[n.Id].Y));
        }

        var a = Box(first);
        var b = Box(second);
        var apartX = b.Left - a.Right >= NetworkLayout.GroupGap || a.Left - b.Right >= NetworkLayout.GroupGap;
        var apartY = b.Top - a.Bottom >= NetworkLayout.GroupGap || a.Top - b.Bottom >= NetworkLayout.GroupGap;
        Assert.True(apartX || apartY, $"groups overlap: {a} and {b}");
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
