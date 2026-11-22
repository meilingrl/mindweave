using Avalonia;
using Avalonia.Media;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>How an edge is drawn; the first matching state wins (s7-prototype-visual-language.md, Edges).</summary>
internal enum EdgeState
{
    Normal,
    Indirect,
    Near,
    Trail,
}

/// <summary>Which outer ring a node gets: the selection wins over a search match.</summary>
internal enum NodeRing
{
    None,
    Match,
    Selected,
}

/// <summary>
/// Everything about one node's drawing, in control coordinates. Read nodes are a hollow ring (stroke only), unread
/// nodes a solid dot with a small badge at the upper right: shape, not only colour, in both themes.
/// </summary>
internal sealed record NodePaint(
    NodeView Node,
    Point Center,
    double Radius,
    bool Hollow,
    bool UnreadDot,
    bool Hovered,
    bool OnTrail,
    NodeRing Ring,
    bool Halo,
    bool LabelVisible,
    bool LabelBold,
    double LabelSize,
    string Label)
{
    /// <summary>Being dragged: drawn on top, a little larger, over a soft shadow.</summary>
    public bool Lifted { get; init; }

    /// <summary>The label as drawn (null when hidden), and where: to the right of the dot.</summary>
    public FormattedText? Text { get; init; }

    public Rect LabelBounds { get; init; }

    /// <summary>
    /// A hovered session dot's date on a line of its own, under (or over) its title, when after the title it would cover
    /// another dot; null otherwise.
    /// </summary>
    public FormattedText? DateText { get; init; }

    public Rect DateBounds { get; init; }

    /// <summary>The session network: the conversation has a summary, drawn as a thin ring around the dot.</summary>
    public bool SummaryRing { get; init; }

    /// <summary>
    /// Faded: a dot with links is selected and this node is neither it, its link partners nor on its path to the root
    /// (design §8: selecting a dot with links brings out both ends and dims the rest).
    /// </summary>
    public bool Dimmed { get; init; }

    public const double UnreadDotRadius = 2.5;

    /// <summary>
    /// Where the unread badge sits: at the dot's upper right, moved out by the ring's gap when the dot wears a summary
    /// ring, so the badge never touches the ring.
    /// </summary>
    public Point UnreadDotCenter
    {
        get
        {
            var offset = Radius + 3 + (SummaryRing ? SummaryNetworkCanvas.SummaryRingGap : 0);
            return new Point(Center.X + offset, Center.Y - offset);
        }
    }

    /// <summary>The faint backdrop under the label: resting edges pass behind it, highlighted ones above it.</summary>
    public Rect Backdrop => LabelBounds.Inflate(new Thickness(2, 0));
}

/// <summary>
/// An edge as drawn. <paramref name="Label"/> ("经 N 个未总结会话") is shown on every edge through unsummarized
/// conversations: quiet at rest, emphasised while the edge is highlighted (architecture §11).
/// </summary>
internal sealed record EdgePaint(EdgeView Edge, Point From, Point To, EdgeState State, bool Dashed, string? Label, bool LabelEmphasized)
{
    public bool Highlighted => State is EdgeState.Trail or EdgeState.Near;

    /// <summary>The time thread in the spatial view: dotted, with a small open arrowhead at the later exploration's dot.</summary>
    public bool Arrow { get; init; }

    /// <summary>Faded while a dot with links is selected and this edge joins none of the nodes brought out.</summary>
    public bool Dimmed { get; init; }

    /// <summary>How far short of <see cref="To"/> the arrow's tip stops: the target dot's edge (its ring included).</summary>
    public double ArrowInset { get; init; }

    /// <summary>A timeline branch to another lane: a cubic curve leaving and arriving horizontally.</summary>
    public bool Curved { get; init; }

    /// <summary>Drawn on the timeline, in its bolder pens (<see cref="SummaryNetworkPalette.TimelinePen"/>).</summary>
    public bool Timeline { get; init; }
}

/// <summary>
/// A content link as drawn (requirements §10.3.3): a solid line in the link colour between the two summaries as drawn,
/// stronger while either end is selected or hovered, and its note (or "关联" when it has none; null when hidden at this zoom)
/// in a small frame at the middle.
/// </summary>
internal sealed record LinkPaint(LinkEdgeView Link, Point From, Point To, bool Active)
{
    /// <summary>The session network's quiet link: a thin neutral dashed line, with its note only while active (design §8).</summary>
    public bool Quiet { get; init; }

    /// <summary>Faded while a dot with links is selected and this link does not touch it.</summary>
    public bool Dimmed { get; init; }

    public FormattedText? Note { get; init; }

    /// <summary>The words of <see cref="Note"/>: the note, or "关联"; null when nothing is written at this zoom.</summary>
    public string? Label { get; init; }

    /// <summary>The frame around <see cref="Note"/>, centred on the line's middle.</summary>
    public Rect NoteBounds { get; init; }
}

/// <summary>
/// A pending link suggestion as drawn (requirements §10.3.4): a light dash-dot line between the two summaries as drawn,
/// stronger while either end or the suggestion itself is active, and "建议" (with the note) in a dashed frame at the middle
/// (null when hidden at this zoom). The frame is what a click selects.
/// </summary>
internal sealed record SuggestionPaint(SuggestionEdgeView Suggestion, Point From, Point To, bool Active)
{
    public FormattedText? Text { get; init; }

    /// <summary>The words of <see cref="Text"/>; null when nothing is written at this zoom.</summary>
    public string? Label { get; init; }

    /// <summary>The frame around <see cref="Text"/>, centred on the line's middle.</summary>
    public Rect LabelBounds { get; init; }
}

/// <summary>An exploration's caption, above the bounding box of its nodes.</summary>
internal sealed record CaptionPaint(GroupCaption Caption, FormattedText Text, Point Origin)
{
    public Rect Bounds => new(Origin, new Size(Text.Width, Text.Height));
}

/// <summary>
/// A recommended node as drawn (requirements §10.3.2): a pale dashed ring, the word "推荐" in a small frame (never colour
/// alone), the start of its question, and a dotted line to its basis summary.
/// </summary>
internal sealed record RecommendationPaint(RecommendedNodeView Node, Point Center, double Radius, Point Basis, bool Selected, bool Hovered)
{
    public required FormattedText Tag { get; init; }

    /// <summary>The frame around <see cref="Tag"/>.</summary>
    public Rect TagBounds { get; init; }

    /// <summary>The question's start (null when labels are hidden at this zoom), and where.</summary>
    public FormattedText? Text { get; init; }

    public Rect LabelBounds { get; init; }

    /// <summary>What a click selects: the ring and everything written beside it.</summary>
    public Rect HitBounds
    {
        get
        {
            var ring = new Rect(Center.X - Radius - 4, Center.Y - Math.Max(Radius + 4, 11), (Radius + 4) * 2, Math.Max(Radius + 4, 11) * 2);
            var union = ring.Union(TagBounds);
            return Text is null ? union : union.Union(LabelBounds);
        }
    }
}
