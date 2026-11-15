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
}

/// <summary>
/// A content link as drawn (requirements §10.3.3): a solid line in the link colour between the two summaries as drawn,
/// stronger while either end is selected or hovered, and its note (null when none, or hidden at this zoom) in a small frame
/// at the middle.
/// </summary>
internal sealed record LinkPaint(LinkEdgeView Link, Point From, Point To, bool Active)
{
    public FormattedText? Note { get; init; }

    /// <summary>The frame around <see cref="Note"/>, centred on the line's middle.</summary>
    public Rect NoteBounds { get; init; }
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
