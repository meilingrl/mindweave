using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// The guards on a spatial layout (docs/design/session-network.md §6.4): things that must not happen, counted, plus the
/// time thread's lengths. They are checks, not what the layout aims for.
/// </summary>
/// <param name="DotOverlaps">Pairs of dots (summary rings included) that overlap.</param>
/// <param name="LabelsOnDots">Label blocks (as reserved) that cover another dot.</param>
/// <param name="LabelOverlaps">Pairs of label blocks that overlap.</param>
/// <param name="BranchCrossings">Pairs of branch lines that cross.</param>
/// <param name="LinesThroughDots">Branch lines that pass through a dot other than their own two.</param>
/// <param name="LinesThroughLabels">Branch lines that pass through a label block other than their own two dots'.</param>
/// <param name="ThreadMean">The time thread's mean length between consecutive roots, in canvas units (0 with one root).</param>
/// <param name="ThreadLongest">Its longest step.</param>
public sealed record SpatialReport(int DotOverlaps, int LabelsOnDots, int LabelOverlaps, int BranchCrossings, int LinesThroughDots, int LinesThroughLabels, double ThreadMean, double ThreadLongest);

public static partial class SpatialLayout
{
    /// <summary>Measures an arrangement of the given nodes against the guards.</summary>
    public static SpatialReport Measure(IReadOnlyList<SpatialNode> nodes, SpatialArrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(arrangement);
        var placed = nodes.Where(node => arrangement.Positions.ContainsKey(node.Id)).OrderBy(node => node.Order).ThenBy(node => node.Id.Value).ToList();
        var at = arrangement.Positions;
        var blocks = placed.ToDictionary(node => node.Id, node => Block(node, arrangement));
        var bonds = placed.Where(node => node.Parent is { } p && at.ContainsKey(p)).Select(node => (A: node.Parent!.Value, B: node.Id)).ToList();
        int dotOverlaps = 0, labelsOnDots = 0, labelOverlaps = 0, crossings = 0, throughDots = 0, throughLabels = 0;
        for (var i = 0; i < placed.Count; i++)
        {
            var u = placed[i];
            var (ux, uy) = (at[u.Id].X, at[u.Id].Y);
            var block = blocks[u.Id];
            for (var j = 0; j < placed.Count; j++)
            {
                var v = placed[j];
                var (vx, vy) = (at[v.Id].X, at[v.Id].Y);
                if (j > i)
                {
                    if (Math.Sqrt(((ux - vx) * (ux - vx)) + ((uy - vy) * (uy - vy))) < u.DotRadius + v.DotRadius - 0.01)
                    {
                        dotOverlaps++;
                    }

                    var other = blocks[v.Id];
                    if (Math.Min(block.R, other.R) - Math.Max(block.L, other.L) > 0.01 && Math.Min(block.B, other.B) - Math.Max(block.T, other.T) > 0.01)
                    {
                        labelOverlaps++;
                    }
                }

                if (j != i)
                {
                    var qx = Math.Clamp(vx, block.L, block.R) - vx;
                    var qy = Math.Clamp(vy, block.T, block.B) - vy;
                    if ((qx * qx) + (qy * qy) < (v.DotRadius - 0.01) * (v.DotRadius - 0.01))
                    {
                        labelsOnDots++;
                    }
                }
            }

            foreach (var (a, b) in bonds)
            {
                if (a != u.Id && b != u.Id)
                {
                    if (SegmentHitsRect(at[a].X, at[a].Y, at[b].X, at[b].Y, block))
                    {
                        throughLabels++;
                    }

                    if (SegmentDistance(ux, uy, at[a], at[b]) < u.DotRadius - 0.01)
                    {
                        throughDots++;
                    }
                }
            }
        }

        for (var i = 0; i < bonds.Count; i++)
        {
            for (var j = i + 1; j < bonds.Count; j++)
            {
                var (a, b) = bonds[i];
                var (c, e) = bonds[j];
                if (a != c && a != e && b != c && b != e && Cross(at[a], at[b], at[c], at[e]))
                {
                    crossings++;
                }
            }
        }

        var roots = placed.Where(node => node.Parent is not { } p || !at.ContainsKey(p)).Select(node => at[node.Id]).ToList();
        var steps = roots.Zip(roots.Skip(1), (a, b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)))).ToList();
        return new SpatialReport(dotOverlaps, labelsOnDots, labelOverlaps, crossings, throughDots, throughLabels, steps.Count == 0 ? 0 : steps.Average(), steps.Count == 0 ? 0 : steps.Max());
    }

    /// <summary>A node's label block as reserved, in canvas units.</summary>
    internal static (double L, double T, double R, double B) Block(SpatialNode node, SpatialArrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(arrangement);
        var label = arrangement.Labels[node.Id];
        var dot = arrangement.Positions[node.Id];
        var (w, h) = BlockSize(label.Lines, node.Dated);
        var (l, t) = LabelOffset(label.Side, node.DotRadius, w, h);
        return (dot.X + l, dot.Y + t, dot.X + l + w, dot.Y + t + h);
    }

    private static double SegmentDistance(double px, double py, NodePosition a, NodePosition b)
    {
        var ex = b.X - a.X;
        var ey = b.Y - a.Y;
        var t = Math.Clamp((((px - a.X) * ex) + ((py - a.Y) * ey)) / Math.Max(1e-9, (ex * ex) + (ey * ey)), 0, 1);
        var qx = a.X + (t * ex) - px;
        var qy = a.Y + (t * ey) - py;
        return Math.Sqrt((qx * qx) + (qy * qy));
    }

    private static bool Cross(NodePosition a, NodePosition b, NodePosition c, NodePosition e)
    {
        static double O(NodePosition p, NodePosition q, NodePosition r) =>
            ((q.X - p.X) * (r.Y - p.Y)) - ((q.Y - p.Y) * (r.X - p.X));
        return O(a, b, c) * O(a, b, e) < 0 && O(c, e, a) * O(c, e, b) < 0;
    }
}
