using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>The canvas colours of the current theme, all from the shared theme resources (D-022).</summary>
internal sealed class NetworkPalette
{
    private readonly Dictionary<(EdgeState, bool), IPen> _edges = [];
    private readonly Dictionary<IBrush, IPen> _rings = [];
    private IPen? _glowRim;

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

    /// <summary>Every theme resource the canvas draws with; both themes must define each one as a brush.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "NetworkBackground", "NetworkGridDot", "NetworkNode", "NetworkNodeRead", "NetworkNodeTrail", "NetworkNodeHover",
        "NetworkUnreadDot", "NetworkHaloSelected", "NetworkHaloHover", "NetworkRingSelected", "NetworkRingMatch",
        "NetworkLabel", "NetworkLabelRead", "NetworkLabelActive", "NetworkCaption", "NetworkEdge", "NetworkEdgeIndirect",
        "NetworkEdgeNear", "NetworkEdgeTrail", "NetworkDragShadow", "NetworkDragGlow",
    ];

    /// <summary>The brush for a key in the control's current theme; null when the theme does not define it.</summary>
    public static IBrush? Resolve(Control control, string key)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.TryFindResource(key, control.ActualThemeVariant, out var value) ? value as IBrush : null;
    }

    public static NetworkPalette From(Control control)
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

        return new NetworkPalette
        {
            Background = brushes["NetworkBackground"],
            GridDot = brushes["NetworkGridDot"],
            Node = brushes["NetworkNode"],
            NodeRead = brushes["NetworkNodeRead"],
            NodeTrail = brushes["NetworkNodeTrail"],
            NodeHover = brushes["NetworkNodeHover"],
            UnreadDot = brushes["NetworkUnreadDot"],
            HaloSelected = brushes["NetworkHaloSelected"],
            HaloHover = brushes["NetworkHaloHover"],
            RingSelected = new Pen(brushes["NetworkRingSelected"], 2),
            RingMatch = new Pen(brushes["NetworkRingMatch"], 2),
            Label = brushes["NetworkLabel"],
            LabelRead = brushes["NetworkLabelRead"],
            LabelActive = brushes["NetworkLabelActive"],
            Caption = brushes["NetworkCaption"],
            Edge = brushes["NetworkEdge"],
            EdgeIndirect = brushes["NetworkEdgeIndirect"],
            EdgeNear = brushes["NetworkEdgeNear"],
            EdgeTrail = brushes["NetworkEdgeTrail"],
            DragShadow = brushes["NetworkDragShadow"],
            DragGlow = brushes["NetworkDragGlow"],
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
