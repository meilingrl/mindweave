namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// The measures the layouts (<see cref="SpatialLayout"/>, <see cref="TimelineLayout"/>) and the canvas share: dots, labels
/// and captions. A label's width is an estimate from the text, never a measured font size, so the same input lays out the
/// same on every machine (MW-C12).
/// </summary>
public static class SummaryNetworkLayout
{
    /// <summary>Screen px between a timeline date and the centre of its root's dot.</summary>
    public const double CaptionGap = 26;

    /// <summary>The size of captions and dates, in px; a spatial date shrinks below zoom 1.</summary>
    public const double CaptionSize = 11;

    /// <summary>The most room a caption or date line takes up and down: 1.5 em of <see cref="CaptionSize"/>.</summary>
    public const double CaptionLineHeight = CaptionSize * 1.5;

    public const double NodeHeight = 44;
    public const double LabelPadding = 24;

    /// <summary>Half the box a label to the right reserves for its dot, and the gap between a dot and its label on the right (S7).</summary>
    public const double NodeRadius = 10;
    public const double LabelGap = 9;

    /// <summary>The gap between a dot and the thin ring a summary draws around it.</summary>
    public const double SummaryRingGap = 3.5;

    /// <summary>
    /// A dot's radius from its weight (the questions asked in it and all its branches):
    /// clamp(5 + 1.3765·√(w − 1), 5, 14), so one question is 5 px, twenty about 11 px, and from about 44 on 14 px.
    /// </summary>
    public static double DotRadius(int weight) => Math.Clamp(5 + (1.3765 * Math.Sqrt(Math.Max(0, weight - 1))), 5, 14);

    /// <summary>A dot with the summary ring around it, pen included: what its footprint must cover and an arrow must stop short of.</summary>
    public static double DotFootprintRadius(int weight, bool hasSummary) => DotRadius(weight) + (hasSummary ? SummaryRingGap + 1 : 0);

    /// <summary>Left edge of a node's box: the dot.</summary>
    public static double BoxLeft(double x) => x - NodeRadius;

    /// <summary>Right edge of a node's box: the end of the label drawn to the right of the dot.</summary>
    public static double BoxRight(double x, double labelWidth) => x + NodeRadius + LabelGap + labelWidth;

    /// <summary>
    /// Canvas units reserved per wide (CJK) character. The canvas draws labels at <c>LabelEm × zoom</c> px (at most
    /// 13 px), so a drawn label never needs more room than its reserved box at any zoom (S7 round-1 fix: was 14,
    /// with labels drawn wider than reserved below zoom ≈ 0.93).
    /// </summary>
    public const double LabelEm = 16;

    /// <summary>Canvas units reserved per ASCII character: 0.625 em, a little over the average Latin glyph and digit.</summary>
    public const double AsciiWidth = 10;

    /// <summary>The label drawn beside a node: its short name, with " ↻" when a candidate update waits.</summary>
    public static string Label(string shortTitle, bool hasCandidate) => hasCandidate ? shortTitle + " ↻" : shortTitle;

    /// <summary>Estimated label width: CJK and other wide characters one em, ASCII 0.625 em.</summary>
    public static double LabelWidth(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return label.EnumerateRunes().Sum(r => r.IsAscii ? AsciiWidth : LabelEm);
    }
}
