using Mindweave.Application.Storage;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>Something on the canvas a recommended node must not cover: a box in canvas units.</summary>
public readonly record struct CanvasBox(double Left, double Top, double Right, double Bottom)
{
    public bool Overlaps(CanvasBox other) => Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;

    /// <summary>The box of a dot at (<paramref name="x"/>, <paramref name="y"/>) with its label to the right, as the layout reserves it.</summary>
    public static CanvasBox OfNode(double x, double y, double labelWidth) => new(
        SummaryNetworkLayout.BoxLeft(x) - (SummaryNetworkLayout.LabelPadding / 2),
        y - (SummaryNetworkLayout.NodeHeight / 2),
        SummaryNetworkLayout.BoxRight(x, labelWidth) + (SummaryNetworkLayout.LabelPadding / 2),
        y + (SummaryNetworkLayout.NodeHeight / 2));
}

/// <summary>
/// Where recommended nodes go (requirements §10.3.2, architecture §11 "首版后"): next to their basis summary, worked out
/// again on every drawing from the positions in memory, never stored, never part of the layout or its unsaved state.
/// Pure and deterministic: the same network and the same recommendations give the same places.
/// </summary>
public static class RecommendationPlacement
{
    /// <summary>Grid steps around the basis: a node's height plus a gap down, a short hop across.</summary>
    public const double StepX = 64;

    public const double StepY = SummaryNetworkLayout.NodeHeight + 12;

    /// <summary>How many steps out in each direction are tried before giving up on a free place.</summary>
    public const int Reach = 6;

    /// <summary>
    /// Candidate offsets from the basis, nearest first (in grid steps); among equals below before above, straight down
    /// before sideways, and left before right, since every label runs to the right.
    /// </summary>
    internal static IReadOnlyList<(double Dx, double Dy)> Offsets { get; } = [.. (
        from i in Enumerable.Range(-Reach, (2 * Reach) + 1)
        from j in Enumerable.Range(-Reach, (2 * Reach) + 1)
        where i != 0 || j != 0
        orderby (i * i) + (j * j), j > 0 ? 0 : 1, Math.Abs(i), i, Math.Abs(j)
        select (i * StepX, j * StepY))];

    /// <summary>
    /// A place for each item, in order: the nearest candidate around its basis whose box overlaps no obstacle (saved
    /// nodes, captions) and no item placed before it; the nearest candidate when none is free.
    /// </summary>
    public static IReadOnlyList<NodePosition> Place(IReadOnlyList<CanvasBox> obstacles, IReadOnlyList<(double BasisX, double BasisY, double LabelWidth)> items)
    {
        ArgumentNullException.ThrowIfNull(obstacles);
        ArgumentNullException.ThrowIfNull(items);
        var taken = new List<CanvasBox>(obstacles);
        var result = new List<NodePosition>(items.Count);
        foreach (var (bx, by, width) in items)
        {
            var chosen = Offsets.Select(o => new NodePosition(bx + o.Dx, by + o.Dy))
                .FirstOrDefault(p => !taken.Any(t => t.Overlaps(CanvasBox.OfNode(p.X, p.Y, width))), new NodePosition(bx + Offsets[0].Dx, by + Offsets[0].Dy));
            taken.Add(CanvasBox.OfNode(chosen.X, chosen.Y, width));
            result.Add(chosen);
        }

        return result;
    }
}
