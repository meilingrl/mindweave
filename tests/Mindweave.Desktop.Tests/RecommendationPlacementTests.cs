using Mindweave.Application.Storage;
using Mindweave.Desktop.Network;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Where recommended nodes go (requirements §10.3.2): next to their basis, clear of every saved node and of each other,
/// the same every time for the same network.
/// </summary>
public class RecommendationPlacementTests
{
    [Fact]
    public void The_nearest_free_place_below_the_basis_is_taken_first()
    {
        var basis = CanvasBox.OfNode(0, 0, 64);

        var place = Assert.Single(RecommendationPlacement.Place([basis], [(0, 0, 200)]));

        Assert.Equal(new NodePosition(0, RecommendationPlacement.StepY), place);
        Assert.False(basis.Overlaps(CanvasBox.OfNode(place.X, place.Y, 200)));
    }

    [Fact]
    public void Places_avoid_saved_nodes_and_each_other_and_are_the_same_each_time()
    {
        // The basis has a child right below it and another to its lower right: the first places are taken.
        CanvasBox[] nodes = [CanvasBox.OfNode(0, 0, 64), CanvasBox.OfNode(0, RecommendationPlacement.StepY, 120), CanvasBox.OfNode(100, -RecommendationPlacement.StepY, 120)];
        (double, double, double)[] items = [(0, 0, 220), (0, 0, 220), (0, 0, 220)];

        var places = RecommendationPlacement.Place(nodes, items);

        Assert.Equal(3, places.Count);
        var boxes = places.Select(p => CanvasBox.OfNode(p.X, p.Y, 220)).ToList();
        Assert.All(boxes, b => Assert.All(nodes, n => Assert.False(b.Overlaps(n), $"{b} over {n}")));
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                Assert.False(boxes[i].Overlaps(boxes[j]), $"{i} over {j}");
            }
        }

        Assert.All(places, p => Assert.True(Math.Abs(p.X) <= RecommendationPlacement.Reach * RecommendationPlacement.StepX && Math.Abs(p.Y) <= RecommendationPlacement.Reach * RecommendationPlacement.StepY));
        Assert.Equal(places, RecommendationPlacement.Place(nodes, items));
    }
}
