using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>The canvas colours of the current theme, all from the shared theme resources (D-022).</summary>
internal sealed class SummaryNetworkPalette
{
    private readonly Dictionary<(EdgeState, bool), IPen> _edges = [];
    private readonly Dictionary<IBrush, IPen> _rings = [];
    private IPen? _glowRim;
    private IPen? _recommendationRing;
    private IPen? _recommendationRingActive;
    private IPen? _recommendationLink;
    private IPen? _recommendationLinkActive;
    private IPen? _recommendationTag;
    private IPen? _link;
    private IPen? _linkActive;
    private IPen? _linkNoteFrame;

    public required IBrush Background { get; init; }

    public required IBrush GridDot { get; init; }

    public required IBrush Node { get; init; }

    public required IBrush NodeRead { get; init; }

    public required IBrush NodeTrail { get; init; }

    public required IBrush NodeHover { get; init; }

    public required IBrush UnreadDot { get; init; }

    public required IBrush HaloSelected { get; init; }

    public required IBrush HaloHover { get; init; }

    public required IPen RingSelected { get; init; }

    public required IPen RingMatch { get; init; }

    public required IBrush Label { get; init; }

    public required IBrush LabelRead { get; init; }

    public required IBrush LabelActive { get; init; }

    public required IBrush Caption { get; init; }

    public required IBrush Edge { get; init; }

    public required IBrush EdgeIndirect { get; init; }

    public required IBrush EdgeNear { get; init; }

    public required IBrush EdgeTrail { get; init; }

    public required IBrush DragShadow { get; init; }

    /// <summary>The glow around a lifted node; transparent in a theme that does not use one (light).</summary>
    public required IBrush DragGlow { get; init; }

    /// <summary>Whether <see cref="DragGlow"/> draws anything: a fully transparent glow is skipped altogether.</summary>
    public bool HasDragGlow => DragGlow is not ISolidColorBrush { Color.A: 0 } && DragGlow.Opacity > 0;

    public IPen DragGlowRim => _glowRim ??= new Pen(DragGlow, 1);

    /// <summary>A recommended node's ring, its tag and its line (requirements §10.3.2).</summary>
    public required IBrush Recommendation { get; init; }

    /// <summary>The pale inside of a recommended node's ring.</summary>
    public required IBrush RecommendationFill { get; init; }

    /// <summary>The ring: dashed, unlike every summary node.</summary>
    public IPen RecommendationRing(bool active) => active
        ? _recommendationRingActive ??= new Pen(Recommendation, 2, new DashStyle([2.2, 1.6], 0))
        : _recommendationRing ??= new Pen(Recommendation, 1.5, new DashStyle([2.2, 1.6], 0));

    /// <summary>
    /// The line to the basis summary: round dots, unlike the solid source edge and the dashed "经未总结会话" edge (and the
    /// planned content-link and link-suggestion edges, see the design note).
    /// </summary>
    public IPen RecommendationLink(bool active) => active
        ? _recommendationLinkActive ??= new Pen(Recommendation, 2, new DashStyle([0, 2.2], 0), PenLineCap.Round)
        : _recommendationLink ??= new Pen(Recommendation, 1.5, new DashStyle([0, 2.6], 0), PenLineCap.Round);

    /// <summary>The thin frame around the word "推荐".</summary>
    public IPen RecommendationTag => _recommendationTag ??= new Pen(Recommendation, 1);

    /// <summary>A content link's line and note (requirements §10.3.3).</summary>
    public required IBrush Link { get; init; }

    /// <summary>The pale inside of a link note's frame.</summary>
    public required IBrush LinkFill { get; init; }

    /// <summary>
    /// A content link: solid, in its own colour and a little thicker than a source edge (1.2), so it reads apart from the
    /// grey solid source edge, the dashed "经未总结会话" edge, the blue selection trail and the round-dotted recommendation line.
    /// </summary>
    public IPen LinkPen(bool active) => active
        ? _linkActive ??= new Pen(Link, 2.6, null, PenLineCap.Round)
        : _link ??= new Pen(Link, 1.8, null, PenLineCap.Round);

    /// <summary>The thin frame around a link's note.</summary>
    public IPen LinkNoteFrame => _linkNoteFrame ??= new Pen(Link, 1);

    /// <summary>Every theme resource the canvas draws with; both themes must define each one as a brush.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "SummaryNetworkBackground", "SummaryNetworkGridDot", "SummaryNetworkNode", "SummaryNetworkNodeRead", "SummaryNetworkNodeTrail", "SummaryNetworkNodeHover",
        "SummaryNetworkUnreadDot", "SummaryNetworkHaloSelected", "SummaryNetworkHaloHover", "SummaryNetworkRingSelected", "SummaryNetworkRingMatch",
        "SummaryNetworkLabel", "SummaryNetworkLabelRead", "SummaryNetworkLabelActive", "SummaryNetworkCaption", "SummaryNetworkEdge", "SummaryNetworkEdgeIndirect",
        "SummaryNetworkEdgeNear", "SummaryNetworkEdgeTrail", "SummaryNetworkDragShadow", "SummaryNetworkDragGlow", "SummaryNetworkRecommendation",
        "SummaryNetworkRecommendationFill", "SummaryNetworkLink", "SummaryNetworkLinkFill",
    ];

    /// <summary>The brush for a key in the control's current theme; null when the theme does not define it.</summary>
    public static IBrush? Resolve(Control control, string key)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.TryFindResource(key, control.ActualThemeVariant, out var value) ? value as IBrush : null;
    }

    public static SummaryNetworkPalette From(Control control)
    {
        var brushes = Keys.ToDictionary(key => key, key =>
        {
            if (Resolve(control, key) is { } brush)
            {
                return brush;
            }

            // A missing key is a bug in App.axaml: loud while developing, a visible grey rather than a crash in use.
            Debug.Fail($"Theme resource {key} is missing or not a brush.");
            return Brushes.Gray;
        });

        return new SummaryNetworkPalette
        {
            Background = brushes["SummaryNetworkBackground"],
            GridDot = brushes["SummaryNetworkGridDot"],
            Node = brushes["SummaryNetworkNode"],
            NodeRead = brushes["SummaryNetworkNodeRead"],
            NodeTrail = brushes["SummaryNetworkNodeTrail"],
            NodeHover = brushes["SummaryNetworkNodeHover"],
            UnreadDot = brushes["SummaryNetworkUnreadDot"],
            HaloSelected = brushes["SummaryNetworkHaloSelected"],
            HaloHover = brushes["SummaryNetworkHaloHover"],
            RingSelected = new Pen(brushes["SummaryNetworkRingSelected"], 2),
            RingMatch = new Pen(brushes["SummaryNetworkRingMatch"], 2),
            Label = brushes["SummaryNetworkLabel"],
            LabelRead = brushes["SummaryNetworkLabelRead"],
            LabelActive = brushes["SummaryNetworkLabelActive"],
            Caption = brushes["SummaryNetworkCaption"],
            Edge = brushes["SummaryNetworkEdge"],
            EdgeIndirect = brushes["SummaryNetworkEdgeIndirect"],
            EdgeNear = brushes["SummaryNetworkEdgeNear"],
            EdgeTrail = brushes["SummaryNetworkEdgeTrail"],
            DragShadow = brushes["SummaryNetworkDragShadow"],
            DragGlow = brushes["SummaryNetworkDragGlow"],
            Recommendation = brushes["SummaryNetworkRecommendation"],
            RecommendationFill = brushes["SummaryNetworkRecommendationFill"],
            Link = brushes["SummaryNetworkLink"],
            LinkFill = brushes["SummaryNetworkLinkFill"],
        };
    }

    public IPen EdgePen(EdgeState state, bool dashed)
    {
        if (!_edges.TryGetValue((state, dashed), out var pen))
        {
            var (brush, width) = state switch
            {
                EdgeState.Trail => (EdgeTrail, 2.3),
                EdgeState.Near => (EdgeNear, 1.8),
                EdgeState.Indirect => (EdgeIndirect, 1.2),
                _ => (Edge, 1.2),
            };
            pen = new Pen(brush, width, dashed ? DashStyle.Dash : null);
            _edges[(state, dashed)] = pen;
        }

        return pen;
    }

    /// <summary>The outline of a read (hollow) node.</summary>
    public IPen RingPen(IBrush brush)
    {
        if (!_rings.TryGetValue(brush, out var pen))
        {
            pen = new Pen(brush, 2);
            _rings[brush] = pen;
        }

        return pen;
    }
}
