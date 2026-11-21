using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;

namespace Mindweave.Desktop.Tests;

/// <summary>Measurements the packing tests take beyond <see cref="SpatialPacking.Measure"/>: sub-branches, line lengths, boxes.</summary>
public partial class SpatialPackingTests
{
    /// <summary>
    /// Every sub-branch of 8 or more nodes: the smallest circle around its footprints over its ideal radius, and how
    /// far its parent's footprint sits from that circle's centre, as a fraction of the radius.
    /// </summary>
    private static IEnumerable<(double Ratio, double HeadOffset)> Subtrees(List<PackNode> nodes, IReadOnlyDictionary<SummaryNodeId, NodePosition> dots)
    {
        var children = nodes.ToLookup(n => n.Parent);
        foreach (var head in nodes)
        {
            var members = new List<PackNode>();
            var stack = new Stack<PackNode>([head]);
            while (stack.TryPop(out var n))
            {
                members.Add(n);
                foreach (var c in children[n.Id])
                {
                    stack.Push(c);
                }
            }

            if (members.Count < 8)
            {
                continue;
            }

            var circles = members.Select(n => SpatialPacking.FootprintOf(n, dots[n.Id])).Select(f => new Circle(f.Centre.X, f.Centre.Y, f.Radius)).ToList();
            var enclosing = SpatialPacking.Enclose(circles);
            var ideal = Math.Sqrt(circles.Sum(c => c.R * c.R));
            var centre = FootprintCentre(head, dots[head.Id]);
            yield return (enclosing.R / ideal, Distance(centre, (enclosing.X, enclosing.Y)) / enclosing.R);
        }
    }

    /// <summary>Each branch line's length between footprint centres, over the distance at which the footprints touch.</summary>
    private static IEnumerable<double> EdgeLengths(List<PackNode> nodes, IReadOnlyDictionary<SummaryNodeId, NodePosition> dots)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        foreach (var child in nodes.Where(n => n.Parent is { } p && byId.ContainsKey(p)))
        {
            var parent = byId[child.Parent!.Value];
            var (a, b) = (SpatialPacking.FootprintOf(parent, dots[parent.Id]), SpatialPacking.FootprintOf(child, dots[child.Id]));
            yield return Distance(a.Centre, (b.Centre.X, b.Centre.Y)) / (a.Radius + b.Radius);
        }
    }

    private static (double Left, double Top, double Right, double Bottom) Box(PackNode node, NodePosition dot)
    {
        var (centre, radius) = SpatialPacking.FootprintOf(node, dot);
        return (centre.X - radius, centre.Y - radius, centre.X + radius, centre.Y + radius);
    }

    private static NodePosition FootprintCentre(PackNode node, NodePosition dot) => SpatialPacking.FootprintOf(node, dot).Centre;

    private static double Distance(NodePosition a, (double X, double Y) b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static bool IsMonotone(List<double> xs) =>
        xs.Zip(xs.Skip(1)).All(p => p.Second >= p.First) || xs.Zip(xs.Skip(1)).All(p => p.Second <= p.First);
}
