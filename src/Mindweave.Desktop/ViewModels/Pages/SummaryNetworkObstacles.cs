using Mindweave.Desktop.SummaryNetwork;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// What a recommended node must not cover (requirements §10.3.2): every node with its label, and every caption, as the
/// canvas draws them at zoom 1. In the session network a dot is as large as its weight and summary ring make it, and a
/// date sits above a timeline root or beside a spatial one (spec §4.4).
/// </summary>
internal static class SummaryNetworkObstacles
{
    /// <summary>A spatial date's line sits this far above the centre of its root's title line (the title's half line plus 2 px).</summary>
    private const double TitleHalfLine = 9;

    /// <summary>The boxes, in canvas units at zoom 1, of <paramref name="nodes"/> and <paramref name="captions"/>.</summary>
    internal static List<CanvasBox> Of(IReadOnlyList<NodeView> nodes, IReadOnlyList<GroupCaption> captions, bool sessionNetwork, bool timeline)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var obstacles = new List<CanvasBox>(nodes.Select(n => sessionNetwork ? OfDot(n) : CanvasBox.OfNode(n.X, n.Y, n.LabelWidth)));
        foreach (var caption in captions)
        {
            var members = caption.Members.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var width = SummaryNetworkLayout.LabelWidth(caption.Text);
            if (sessionNetwork && !timeline)
            {
                // Beside the root, on its label's side: above its title, or below it when an edge lifted the title.
                var root = members[0];
                var left = root.X + SummaryNetworkLayout.DotRadius(root.Weight) + SummaryNetworkLayout.LabelGap;
                obstacles.Add(new CanvasBox(
                    left,
                    root.Y - TitleHalfLine - 2 - SummaryNetworkLayout.CaptionLineHeight,
                    left + width,
                    root.Y + 3 + SummaryNetworkLayout.CaptionLineHeight));
                continue;
            }

            // Above the group's topmost node, from its leftmost dot's edge (SummaryNetworkLayout.CaptionGap).
            var top = members.Min(n => n.Y);
            var start = sessionNetwork
                ? members.Min(n => n.X - SummaryNetworkLayout.DotRadius(n.Weight))
                : members.Min(n => n.X) - SummaryNetworkLayout.NodeRadius;
            var headroom = sessionNetwork ? SummaryNetworkLayout.CaptionGap + SummaryNetworkLayout.CaptionLineHeight : SummaryNetworkLayout.CaptionHeadroom;
            obstacles.Add(new CanvasBox(start, top - headroom, start + width, top - (SummaryNetworkLayout.NodeHeight / 2)));
        }

        return obstacles;
    }

    /// <summary>
    /// A session dot: its footprint (the dot and its summary ring) with half the layout's padding round it, and its label
    /// from where the canvas starts it, after the dot.
    /// </summary>
    private static CanvasBox OfDot(NodeView node)
    {
        var footprint = SummaryNetworkLayout.DotFootprintRadius(node.Weight, node.HasSummary);
        var half = Math.Max(SummaryNetworkLayout.NodeHeight / 2, footprint + (SummaryNetworkLayout.LabelPadding / 2));
        return new CanvasBox(
            node.X - footprint - (SummaryNetworkLayout.LabelPadding / 2),
            node.Y - half,
            node.X + SummaryNetworkLayout.DotRadius(node.Weight) + SummaryNetworkLayout.LabelGap + node.LabelWidth + (SummaryNetworkLayout.LabelPadding / 2),
            node.Y + half);
    }
}
