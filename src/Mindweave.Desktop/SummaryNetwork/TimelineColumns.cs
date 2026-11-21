using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// Widens the timeline's columns to fit their labels (owner, 2026-11-21: every title shows whole). <see cref="TimelineLayout"/>
/// puts every x on the <see cref="TimelineLayout.Step"/> grid; a dot's column is its x divided by the step. Each column
/// becomes as wide as its widest label needs, never narrower than <see cref="MinColumn"/>, and every dot moves to the
/// start of its column. Columns keep their order and y never changes, so the layout's promises (no dot between a
/// connector's ends, dots a step apart in a lane) still hold. Pure and deterministic.
/// </summary>
public static class TimelineColumns
{
    /// <summary>
    /// Room for the widest dot and its summary ring, pen included, in canvas units at zoom 1
    /// (<see cref="SummaryNetworkLayout.DotFootprintRadius"/> at its clamp).
    /// </summary>
    public static readonly double DotRoom = SummaryNetworkLayout.DotFootprintRadius(int.MaxValue, hasSummary: true);

    /// <summary>Space between the end of a column's widest label and the next column's dots.</summary>
    public const double ColumnGap = 28;

    /// <summary>The narrowest column: the grid's own step, so short labels keep today's spacing.</summary>
    public const double MinColumn = TimelineLayout.Step;

    /// <summary>
    /// New positions for <paramref name="grid"/>: column <c>c</c> is
    /// <c>max(minColumn, max over its dots of DotRoom + LabelGap + labelWidth + ColumnGap)</c> wide, and a dot's x is the sum
    /// of the widths of the columns before its own. A column with no dots (a day gap) is <paramref name="minColumn"/> wide.
    /// A dot without a label width counts as an empty label.
    /// </summary>
    public static IReadOnlyDictionary<SummaryNodeId, NodePosition> Widen(
        IReadOnlyDictionary<SummaryNodeId, NodePosition> grid,
        IReadOnlyDictionary<SummaryNodeId, double> labelWidths,
        double minColumn)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(labelWidths);
        if (grid.Count == 0)
        {
            return new Dictionary<SummaryNodeId, NodePosition>();
        }

        var columnOf = grid.ToDictionary(p => p.Key, p => (int)Math.Round(p.Value.X / TimelineLayout.Step));
        var widths = new double[columnOf.Values.Max() + 1];
        Array.Fill(widths, minColumn);
        foreach (var (id, column) in columnOf)
        {
            var need = DotRoom + SummaryNetworkLayout.LabelGap + labelWidths.GetValueOrDefault(id) + ColumnGap;
            widths[column] = Math.Max(widths[column], need);
        }

        var starts = new double[widths.Length];
        for (var c = 1; c < widths.Length; c++)
        {
            starts[c] = starts[c - 1] + widths[c - 1];
        }

        return grid.ToDictionary(p => p.Key, p => new NodePosition(starts[columnOf[p.Key]], p.Value.Y));
    }
}
