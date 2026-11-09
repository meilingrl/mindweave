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

/// <summary>An exploration's caption, above the bounding box of its nodes.</summary>
internal sealed record CaptionPaint(GroupCaption Caption, FormattedText Text, Point Origin)
{
    public Rect Bounds => new(Origin, new Size(Text.Width, Text.Height));
}
