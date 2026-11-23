using System.Text;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// One session as the spatial view lays it out: its parent (none for a root), its rank by creation time, its weight
/// (questions asked in it and all its branches), the radius of its dot as drawn (summary ring included), its label as
/// shown, and whether a date line goes under the label (a root that starts a new day, spec §4.4).
/// </summary>
/// <remarks><paramref name="Conversation"/> names a root's exploration across loads (its dot's id changes when it gets a summary); the dot's id when not given.</remarks>
public sealed record SpatialNode(SummaryNodeId Id, SummaryNodeId? Parent, int Order, int Weight, double DotRadius, string Label, bool Dated = false, Guid? Conversation = null);


/// <summary>Where a label sits round its dot (an index into <see cref="SpatialLayout.Sides"/>) and its wrapped lines.</summary>
public sealed record SpatialLabel(int Side, IReadOnlyList<string> Lines);

/// <summary>A spatial layout: every dot's position and every label's place.</summary>
public sealed record SpatialArrangement(IReadOnlyDictionary<SummaryNodeId, NodePosition> Positions, IReadOnlyDictionary<SummaryNodeId, SpatialLabel> Labels)
{
    /// <summary>Each exploration's place, keyed by its root conversation (or root dot): what is saved, so the next layout keeps it.</summary>
    public IReadOnlyDictionary<Guid, ExplorationPlace> Places { get; init; } = new Dictionary<Guid, ExplorationPlace>();

    public static SpatialArrangement Empty { get; } = new(new Dictionary<SummaryNodeId, NodePosition>(), new Dictionary<SummaryNodeId, SpatialLabel>());
}

/// <summary>How a label's lines are aligned: from its left edge, centred, or to its right edge (the side nearer the dot).</summary>
public enum LabelAnchor
{
    Start,
    Middle,
    End,
}

/// <summary>
/// The spatial view as a molecule layout (docs/design/session-network.md §6.1). Structure first, compactness second:
/// <list type="bullet">
/// <item>Each exploration is one molecule. At every dot its lines spread round it, each taking room by the size of the
/// branch behind it, so equal branches sit at equal angles (two lines straight, three at 120°) and a big branch pushes
/// a single dot aside. Each dot turns its heaviest branch the other way from the turn that led to it, so chains
/// zigzag gently and never curl. Sibling branches keep gaps that grow with their size.</item>
/// <item>Titles are whole, wrapped to a narrow column, and placed in each dot's widest free angle, off other dots,
/// titles and lines.</item>
/// <item>Molecules are packed as drawn: the heaviest in the middle, the others outward from it in time order, each
/// beside its neighbour in time, so things close in time are close in space.</item>
/// </list>
/// Pure and deterministic: inputs sorted by creation order, fixed step counts, no randomness.
/// </summary>
public static partial class SpatialLayout
{
    /// <summary>The widest a label line may be, in canvas units: nine wide characters.</summary>
    public const double MaxLineWidth = 9 * SummaryNetworkLayout.LabelEm;

    /// <summary>The room one label line takes up and down, in canvas units (a drawn line is at most 13 px × 1.6).</summary>
    public const double LineHeight = 21;

    /// <summary>The room a date line under a label takes, in canvas units.</summary>
    public const double DateLineHeight = SummaryNetworkLayout.CaptionLineHeight;

    /// <summary>The gap between a dot's reach (its summary ring included) and its label.</summary>
    public const double LabelGap = 6;

    /// <summary>
    /// The places a label may take round its dot: the direction it lies in (degrees, clockwise from the right; used to keep
    /// it off the dot's own lines), a small cost that prefers beside the dot first, and how its lines are aligned.
    /// </summary>
    public static IReadOnlyList<(double Angle, double Cost, LabelAnchor Anchor)> Sides { get; } =
    [
        (0, 0, LabelAnchor.Start), (45, 10, LabelAnchor.Start), (90, 14, LabelAnchor.Middle), (135, 10, LabelAnchor.End),
        (180, 3, LabelAnchor.End), (225, 10, LabelAnchor.End), (270, 14, LabelAnchor.Middle), (315, 10, LabelAnchor.Start),
        (340, 4, LabelAnchor.Start), (20, 4, LabelAnchor.Start), (200, 6, LabelAnchor.End), (160, 6, LabelAnchor.End),
        (290, 12, LabelAnchor.Start), (250, 12, LabelAnchor.End), (70, 12, LabelAnchor.Start), (110, 12, LabelAnchor.End),
    ];

    /// <summary>
    /// The top-left corner of a label block relative to its dot's centre, for a side, the dot's reach and the block's size.
    /// The layout uses it with reserved sizes; the canvas with drawn ones, so a drawn block always lies inside the reserved one.
    /// </summary>
    public static (double Left, double Top) LabelOffset(int side, double reach, double width, double height)
    {
        var f = reach;
        var g = LabelGap;
        var c = (f * 0.62) + 3;
        return side switch
        {
            0 => (f + g, -height / 2),
            1 => (c, c),
            2 => (-width / 2, f + g - 2),
            3 => (-c - width, c),
            4 => (-f - g - width, -height / 2),
            5 => (-c - width, -c - height),
            6 => (-width / 2, -f - g - height + 2),
            7 => (c, -c - height),
            8 => (f + g, -height + 5),
            9 => (f + g, -5),
            10 => (-f - g - width, -height + 5),
            11 => (-f - g - width, -5),
            12 => (-6, -f - g - height + 2),
            13 => (6 - width, -f - g - height + 2),
            14 => (-6, f + g - 2),
            _ => (6 - width, f + g - 2),
        };
    }

    /// <summary>
    /// Lays out every conversation. Conversations whose parent is unknown are roots; those caught in a parent cycle (which the data
    /// cannot hold) are left out.
    /// </summary>
    /// <param name="places">
    /// Where explorations were (design session-network §16, option C): each one with a place returns to it, moving only if it
    /// changed and now runs into another; without places, everything is packed afresh.
    /// </param>
    public static SpatialArrangement Arrange(IReadOnlyList<SpatialNode> nodes, SpatialLayoutSettings? settings = null, IReadOnlyDictionary<Guid, ExplorationPlace>? places = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            return SpatialArrangement.Empty;
        }

        var model = new Model(nodes, settings ?? SpatialLayoutSettings.Default, places);
        model.Run();
        return model.Result();
    }

    /// <summary>Breaks a label into lines no wider than <see cref="MaxLineWidth"/>: wide text anywhere, other text at spaces.</summary>
    public static IReadOnlyList<string> Wrap(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (SummaryNetworkLayout.LabelWidth(label) <= MaxLineWidth * 1.15)
        {
            return [label];
        }

        var units = Units(label);
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var unit in units)
        {
            var piece = unit;
            if (line.Length > 0 && SummaryNetworkLayout.LabelWidth(line + piece.TrimEnd()) > MaxLineWidth)
            {
                lines.Add(line.ToString().TrimEnd());
                line.Clear();
                piece = piece.TrimStart();
            }

            line.Append(piece);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString().TrimEnd());
        }

        // No lonely last wide character or two: move a few wide characters from the end of the line before. Only wide
        // characters move, whole runes at a time, so words and characters outside the basic plane are never split.
        if (lines.Count >= 2)
        {
            var last = lines[^1].EnumerateRunes().ToList();
            var before = lines[^2].EnumerateRunes().ToList();
            var wideTail = before.AsEnumerable().Reverse().TakeWhile(r => !r.IsAscii).Count();
            if (last.Count <= 2 && last.All(r => !r.IsAscii) && before.Count > 4)
            {
                var take = Math.Min(wideTail, (before.Count - last.Count) / 2);
                if (take > 0)
                {
                    lines[^1] = string.Concat(before.Skip(before.Count - take).Select(r => r.ToString())) + lines[^1];
                    lines[^2] = string.Concat(before.Take(before.Count - take).Select(r => r.ToString()));
                }
            }
        }

        return lines;
    }

    /// <summary>Breakable units: each wide character alone; a run of narrow characters with its trailing space.</summary>
    private static List<string> Units(string label)
    {
        var units = new List<string>();
        var runes = label.EnumerateRunes().ToList();
        var i = 0;
        while (i < runes.Count)
        {
            var unit = new StringBuilder();
            if (!runes[i].IsAscii)
            {
                unit.Append(runes[i].ToString());
                i++;
            }
            else
            {
                while (i < runes.Count && runes[i].IsAscii && runes[i].Value != ' ')
                {
                    unit.Append(runes[i].ToString());
                    i++;
                }

                while (i < runes.Count && runes[i].Value == ' ')
                {
                    unit.Append(' ');
                    i++;
                }
            }

            units.Add(unit.ToString());
        }

        return units;
    }

    /// <summary>A placed label's reserved block round its dot, as offsets from the dot's centre in canvas units.</summary>
    public static (double Left, double Top, double Right, double Bottom) BlockAround(SpatialLabel label, double reach, bool dated)
    {
        ArgumentNullException.ThrowIfNull(label);
        var (w, h) = BlockSize(label.Lines, dated);
        var (l, t) = LabelOffset(label.Side, reach, w, h);
        return (l, t, l + w, t + h);
    }

    /// <summary>The widest a date line under a label may be ("Nov 16, 2025" at caption size), in canvas units.</summary>
    public const double DateLineWidth = 84;

    /// <summary>The reserved size of a label block: its widest line (or the date line), its lines and the date line under it.</summary>
    internal static (double Width, double Height) BlockSize(IReadOnlyList<string> lines, bool dated) =>
        (Math.Max(lines.Max(SummaryNetworkLayout.LabelWidth), dated ? DateLineWidth : 0), (lines.Count * LineHeight) + (dated ? DateLineHeight : 0));
}

/// <summary>Tuning knobs, in canvas units. Defaults are what the owner saw in the prototype (prototypes/molecule-layout).</summary>
public sealed record SpatialLayoutSettings
{
    public static SpatialLayoutSettings Default { get; } = new();

    /// <summary>Bond length between two dots' edges, and how much it grows with the size of the branch it leads to.</summary>
    public double Bond { get; init; } = 58;

    public double BondGrow { get; init; } = 10;

    /// <summary>The most dots behind a line that still add to the room it takes at a dot.</summary>
    public int ShareCap { get; init; } = 8;

    /// <summary>From this many lines at one dot, bonds lengthen so the lines and titles fit round it, and leaves stagger.</summary>
    public int CrowdFrom { get; init; } = 6;

    public double CrowdSpacing { get; init; } = 10;

    public double CrowdStagger { get; init; } = 1.45;

    /// <summary>How strictly a branch's outline must fit inside its wedge round its parent (1 = all of it).</summary>
    public double FitShare { get; init; } = 1;

    /// <summary>Gaps kept between dots that are not bonded: along one line of descent, and between sibling branches.</summary>
    public double GapNested { get; init; } = 18;

    public double GapSibling { get; init; } = 16;

    public double GapSiblingGrow { get; init; } = 12;

    /// <summary>The gap round a whole exploration when packing, growing with its size.</summary>
    public double GapTree { get; init; } = 54;

    public double GapTreeGrow { get; init; } = 14;

    /// <summary>Settling steps for one exploration; titles take part from this fraction of them on.</summary>
    public int Iterations { get; init; } = 150;

    public double LabelsFrom { get; init; } = 0.4;

    public int SettleIterations { get; init; } = 30;

    public double AngleStiffness { get; init; } = 0.22;

    public double BondStiffness { get; init; } = 0.25;

    public double Repulsion { get; init; } = 0.5;

    /// <summary>How hard a line crossing a title pushes, per step.</summary>
    public double LineShove { get; init; } = 4;

    /// <summary>Packing: turns tried, rings searched past the first fit, the pull to the middle against nearness in time, and the cost of a time thread over a molecule.</summary>
    public int Rotations { get; init; } = 8;

    public int SearchBand { get; init; } = 25;

    public double CentrePull { get; init; } = 1;

    public double ThreadOverShape { get; init; } = 12;

    /// <summary>How much of its gap an exploration back in its saved place must keep before it is moved (design §16).</summary>
    public double KeptGapShare { get; init; } = 0.4;

    /// <summary>Room to grow, in canvas units, that a newly placed exploration keeps beyond its gap (design §16).</summary>
    public double GrowRoom { get; init; } = 120;
}
