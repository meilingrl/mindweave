using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Network;
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
    string Label);

internal sealed record EdgePaint(EdgeView Edge, Point From, Point To, EdgeState State, bool Dashed);

/// <summary>
/// Draws the network and turns pointer input into selection, opening and viewport changes (architecture §11.1). All
/// drawing uses one canvas → screen transform, so nodes and edges always agree. Positions are never changed here;
/// the viewport lives in the workspace's <see cref="NetworkSession"/>. The look follows the confirmed network sample
/// (S7): dot grid, labels to the right, hover halo, the selected node's source trail, search rings. Every colour is a
/// theme resource, read again when the theme changes.
/// </summary>
public sealed class NetworkCanvas : Control
{
    public const double RootRadius = NetworkLayout.NodeRadius;
    public const double ChildRadius = 7;
    public const double HoverGrowth = 2;

    /// <summary>Labels other than the selected, hovered or matching ones are hidden at this zoom and below.</summary>
    public const double LabelHideScale = 0.45;

    /// <summary>Decorative pull towards the pointer: fixed at the sample's default 30% (1.5 px at most), drawing only.</summary>
    internal const double AttractionStrength = 0.3;
    private const double AttractionReach = 90;
    private const double GridSpacing = 28;

    private static readonly Typeface Face = new(FontFamily.Default);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly Dictionary<SummaryNodeId, Vector> _offsets = [];
    private readonly DispatcherTimer _motion;
    private NetworkHomeViewModel? _page;
    private Palette? _palette;
    private Point? _panFrom;
    private Viewport _panStart;
    private bool _dragged;
    private Point? _pointer;

    public NetworkCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        _motion = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => StepMotion());
        ActualThemeVariantChanged += (_, _) =>
        {
            _palette = null;
            InvalidateVisual();
        };
    }

    /// <summary>The viewport in use, fitted on first use.</summary>
    internal Viewport Current => _page?.Session.Viewport ?? Fit();

    /// <summary>The node under the pointer, if any (drawn larger, with a halo and its edges brightened).</summary>
    internal SummaryNodeId? Hovered { get; private set; }

    /// <summary>The decorative pull is running (it stops by itself once every node has settled).</summary>
    internal bool IsMoving => _motion.IsEnabled;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_page is not null)
        {
            _page.ViewChanged -= OnViewChanged;
            _page.ZoomRequested -= OnZoomRequested;
        }

        _page = DataContext as NetworkHomeViewModel;
        if (_page is not null)
        {
            _page.ViewChanged += OnViewChanged;
            _page.ZoomRequested += OnZoomRequested;
        }

        Hovered = null;
        _offsets.Clear();
        InvalidateVisual();
        base.OnDataContextChanged(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _motion.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Canvas coordinates to control coordinates.</summary>
    internal Point ToScreen(double x, double y)
    {
        var v = Current;
        return new Point(((x - v.CenterX) * v.Zoom) + (Bounds.Width / 2), ((y - v.CenterY) * v.Zoom) + (Bounds.Height / 2));
    }

    internal (double X, double Y) ToCanvas(Point screen)
    {
        var v = Current;
        return (((screen.X - (Bounds.Width / 2)) / v.Zoom) + v.CenterX, ((screen.Y - (Bounds.Height / 2)) / v.Zoom) + v.CenterY);
    }

    /// <summary>Label size: 13 px from zoom 0.65 up, shrinking linearly below.</summary>
    internal static double LabelSize(double zoom) => 13 * Math.Min(1, zoom / 0.65);

    /// <summary>
    /// The node under a control point: a box from just left of its dot to the end of its label, tested at both the
    /// drawn (attracted) and the layout position, as in the sample.
    /// </summary>
    internal NodeView? HitTest(Point screen)
    {
        if (_page is null)
        {
            return null;
        }

        var size = LabelSize(Current.Zoom);
        foreach (var node in _page.Nodes.Reverse())
        {
            var layout = ToScreen(node.X, node.Y);
            var drawn = layout + _offsets.GetValueOrDefault(node.Id);
            if (Hit(screen, layout, node, size) || Hit(screen, drawn, node, size))
            {
                return node;
            }
        }

        return null;
    }

    private static bool Hit(Point screen, Point p, NodeView node, double labelSize)
    {
        var dx = screen.X - p.X;
        var dy = screen.Y - p.Y;
        var right = RadiusOf(node) + NetworkLayout.LabelGap + (node.LabelWidth * labelSize / 13) + 12;
        return dx >= -16 && dx <= right && dy >= -20 && dy <= 20;
    }

    private static double RadiusOf(NodeView node) => node.IsRoot ? RootRadius : ChildRadius;

    /// <summary>What each node looks like now, in drawing order. The render pass and the tests read the same list.</summary>
    internal IReadOnlyList<NodePaint> PaintNodes()
    {
        if (_page is null)
        {
            return [];
        }

        var zoom = Current.Zoom;
        var size = LabelSize(zoom);
        var selected = _page.Session.Selected;
        var trail = _page.Trail;
        var matches = _page.Matches;
        return [.. _page.Nodes.Select(node =>
        {
            var hovered = node.Id == Hovered;
            var isSelected = node.Id == selected;
            var matched = matches.Contains(node.Id);
            var radius = RadiusOf(node) + (hovered ? HoverGrowth : 0);
            var center = ToScreen(node.X, node.Y) + _offsets.GetValueOrDefault(node.Id);
            var ring = isSelected ? NodeRing.Selected : matched ? NodeRing.Match : NodeRing.None;
            var label = node.HasCandidate ? node.ShortTitle + " ↻" : node.ShortTitle;
            return new NodePaint(
                node,
                center,
                radius,
                Hollow: !node.Unread,
                UnreadDot: node.Unread,
                hovered,
                trail.Contains(node.Id),
                ring,
                Halo: hovered || isSelected,
                LabelVisible: zoom > LabelHideScale || isSelected || matched || hovered,
                LabelBold: node.Unread,
                size,
                label);
        })];
    }

    /// <summary>Each edge from the parent's drawn position to the child's, with its state.</summary>
    internal IReadOnlyList<EdgePaint> PaintEdges()
    {
        if (_page is null)
        {
            return [];
        }

        var trail = _page.Trail;
        return [.. _page.Edges.Select(edge =>
        {
            var from = ToScreen(edge.FromX, edge.FromY) + _offsets.GetValueOrDefault(edge.From);
            var to = ToScreen(edge.ToX, edge.ToY) + _offsets.GetValueOrDefault(edge.To);
            var state = trail.Contains(edge.To) ? EdgeState.Trail
                : Hovered is { } h && (h == edge.To || h == edge.From) ? EdgeState.Near
                : !edge.IsDirect ? EdgeState.Indirect
                : EdgeState.Normal;
            return new EdgePaint(edge, from, to, state, !edge.IsDirect);
        })];
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var palette = _palette ??= Palette.From(this);
        context.FillRectangle(palette.Background, new Rect(Bounds.Size));
        DrawGrid(context, palette);
        if (_page is null || _page.Nodes.Count == 0)
        {
            return;
        }

        foreach (var edge in PaintEdges())
        {
            context.DrawLine(palette.EdgePen(edge.State, edge.Dashed), edge.From, edge.To);
            if (edge.State is EdgeState.Trail or EdgeState.Near && edge.Edge.Label is { } via)
            {
                // "经 1 个未总结会话" only on a highlighted edge, so the resting network stays as quiet as the sample.
                var text = Text(via, 11, palette.Caption);
                var origin = new Point(((edge.From.X + edge.To.X) / 2) - (text.Width / 2), ((edge.From.Y + edge.To.Y) / 2) - text.Height - 2);
                context.DrawText(text, origin);
            }
        }

        var captions = _page.Captions.ToDictionary(c => c.Anchor, c => c.Text);
        foreach (var node in PaintNodes())
        {
            DrawNode(context, palette, node);
            if (captions.TryGetValue(node.Node.Id, out var caption))
            {
                // Always drawn, even when labels are hidden: it names the exploration.
                context.DrawText(Text(caption, 11, palette.Caption), new Point(node.Center.X - 25, node.Center.Y - 42));
            }
        }
    }

    private void DrawGrid(DrawingContext context, Palette palette)
    {
        // Dots every 28 screen px; the grid pans with the view but its spacing does not scale.
        var origin = _page is { Nodes.Count: > 0 } ? ToScreen(0, 0) : default;
        var startX = ((origin.X % GridSpacing) + GridSpacing) % GridSpacing;
        var startY = ((origin.Y % GridSpacing) + GridSpacing) % GridSpacing;
        for (var x = startX; x < Bounds.Width; x += GridSpacing)
        {
            for (var y = startY; y < Bounds.Height; y += GridSpacing)
            {
                context.DrawEllipse(palette.GridDot, null, new Point(x, y), 0.8, 0.8);
            }
        }
    }

    private static void DrawNode(DrawingContext context, Palette palette, NodePaint node)
    {
        var p = node.Center;
        var r = node.Radius;
        var surface = palette.Background;
        if (node.Halo)
        {
            surface = node.Ring == NodeRing.Selected ? palette.HaloSelected : palette.HaloHover;
            context.DrawEllipse(surface, null, p, r + 11, r + 11);
        }

        if (node.Ring != NodeRing.None)
        {
            context.DrawEllipse(null, node.Ring == NodeRing.Selected ? palette.RingSelected : palette.RingMatch, p, r + 5, r + 5);
        }

        var tone = node.Hovered ? palette.NodeHover : node.OnTrail ? palette.NodeTrail : node.Hollow ? palette.NodeRead : palette.Node;
        if (node.Hollow)
        {
            // A ring: filled with what lies under it, so edges ending at the centre do not show through.
            context.DrawEllipse(surface, palette.RingPen(tone), p, r - 1, r - 1);
        }
        else
        {
            context.DrawEllipse(tone, null, p, r, r);
        }

        if (node.UnreadDot)
        {
            context.DrawEllipse(palette.UnreadDot, null, new Point(p.X + r + 3, p.Y - r - 3), 2.5, 2.5);
        }

        if (node.LabelVisible)
        {
            var brush = node.Hovered || node.Ring == NodeRing.Selected ? palette.LabelActive : node.Hollow ? palette.LabelRead : palette.Label;
            var text = Text(node.Label, node.LabelSize, brush, node.LabelBold ? FontWeight.SemiBold : FontWeight.Normal);
            var origin = new Point(p.X + r + NetworkLayout.LabelGap, p.Y - 9);

            // Edges pass behind labels: a label on a node left of its source would otherwise sit on that edge.
            using (context.PushOpacity(0.85))
            {
                context.FillRectangle(palette.Background, new Rect(origin.X - 2, origin.Y, text.Width + 4, text.Height), 3);
            }

            context.DrawText(text, origin);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        // A size change keeps the centre and zoom; only an unset viewport is fitted (§11.3).
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        if (HitTest(point) is { } node)
        {
            _page?.Select(node.Id);
            if (e.ClickCount >= 2)
            {
                _page?.OpenCommand.Execute(node.Id);
            }

            e.Handled = true;
            return;
        }

        _panFrom = point;
        _panStart = Current;
        _dragged = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_panFrom is not { } from || _page is null)
        {
            SetPointer(point);
            return;
        }

        if (!_dragged && Math.Abs(point.X - from.X) + Math.Abs(point.Y - from.Y) < 3)
        {
            return;
        }

        _dragged = true;
        _page.Session.Viewport = _panStart with
        {
            CenterX = _panStart.CenterX - ((point.X - from.X) / _panStart.Zoom),
            CenterY = _panStart.CenterY - ((point.Y - from.Y) / _panStart.Zoom),
        };
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_panFrom is null)
        {
            SetPointer(null);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_panFrom is not null && !_dragged)
        {
            // A click on empty canvas clears the selection.
            _page?.Select(null);
        }

        _panFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerWheelChanged(e);
        if (_page is null)
        {
            return;
        }

        var zoomKey = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (zoomKey || _page.WheelZooms)
        {
            ZoomAround(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        }
        else
        {
            // Two-finger scrolling pans, as in the network sample.
            var v = Current;
            _page.Session.Viewport = v with { CenterX = v.CenterX - (e.Delta.X * 40 / v.Zoom), CenterY = v.CenterY - (e.Delta.Y * 40 / v.Zoom) };
            InvalidateVisual();
        }

        e.Handled = true;
    }

    /// <summary>Zooms keeping the canvas point under <paramref name="screen"/> where it is.</summary>
    internal void ZoomAround(Point screen, double factor)
    {
        if (_page is null)
        {
            return;
        }

        var v = Current;
        var zoom = Viewport.Clamp(v.Zoom * factor);
        var (x, y) = ToCanvas(screen);
        _page.Session.Viewport = new Viewport(
            x - ((screen.X - (Bounds.Width / 2)) / zoom),
            y - ((screen.Y - (Bounds.Height / 2)) / zoom),
            zoom);
        InvalidateVisual();
    }

    /// <summary>The pointer moved over the canvas (or left it): hover, the hand cursor and the decorative pull follow.</summary>
    internal void SetPointer(Point? point)
    {
        _pointer = point;
        var hovered = point is { } p ? HitTest(p)?.Id : null;
        if (hovered != Hovered)
        {
            Hovered = hovered;
            Cursor = hovered is null ? Cursor.Default : HandCursor;
            InvalidateVisual();
        }

        if (!_motion.IsEnabled)
        {
            _motion.Start();
        }
    }

    /// <summary>Where the pull wants a node, in screen px: towards the pointer within 90 px, at most 5 × strength.</summary>
    internal static Vector Attraction(Point node, Point? pointer)
    {
        if (pointer is not { } p)
        {
            return default;
        }

        var delta = p - node;
        var distance = Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y));
        if (distance < 0.5 || distance >= AttractionReach)
        {
            return default;
        }

        var pull = 5 * AttractionStrength * Math.Sin(Math.PI * distance / AttractionReach);
        return delta / distance * pull;
    }

    /// <summary>One 16 ms easing step; stops the timer once every node has settled.</summary>
    internal void StepMotion()
    {
        if (_page is null)
        {
            _motion.Stop();
            return;
        }

        var moving = false;
        foreach (var node in _page.Nodes)
        {
            var target = Attraction(ToScreen(node.X, node.Y), _pointer);
            var now = _offsets.GetValueOrDefault(node.Id);
            var next = now + ((target - now) * 0.18);
            if (Math.Abs(next.X - target.X) + Math.Abs(next.Y - target.Y) < 0.02)
            {
                next = target;
            }
            else
            {
                moving = true;
            }

            if (next == default)
            {
                _offsets.Remove(node.Id);
            }
            else
            {
                _offsets[node.Id] = next;
            }
        }

        if (!moving)
        {
            _motion.Stop();
        }

        InvalidateVisual();
    }

    private void OnZoomRequested(object? sender, double factor) => ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);

    private void OnViewChanged(object? sender, bool reset) => InvalidateVisual();

    /// <summary>Fits every node, label and caption into view with a margin; zoom limited to the allowed range.</summary>
    private Viewport Fit()
    {
        if (_page is null || _page.Nodes.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return new Viewport(0, 0, 1);
        }

        var minX = _page.Nodes.Min(n => NetworkLayout.BoxLeft(n.X) - 25);
        var maxX = _page.Nodes.Max(n => NetworkLayout.BoxRight(n.X, n.LabelWidth));
        var minY = _page.Nodes.Min(n => n.Y - 42);
        var maxY = _page.Nodes.Max(n => n.Y + RootRadius + 12);
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        var zoom = Viewport.Clamp(Math.Min((Bounds.Width - (2 * Viewport.FitMargin)) / width, (Bounds.Height - (2 * Viewport.FitMargin)) / height));
        var fitted = new Viewport((minX + maxX) / 2, (minY + maxY) / 2, Math.Min(zoom, 1.5));
        _page.Session.Viewport = fitted;
        return fitted;
    }

    private static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, weight), size, brush);

    /// <summary>The canvas colours of the current theme, all from the shared theme resources (D-022).</summary>
    internal sealed class Palette
    {
        private readonly Dictionary<(EdgeState, bool), IPen> _edges = [];
        private readonly Dictionary<IBrush, IPen> _rings = [];

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

        public static Palette From(Control control)
        {
            var theme = control.ActualThemeVariant;
            IBrush Brush(string key) =>
                control.TryFindResource(key, theme, out var value) && value is IBrush brush ? brush : Brushes.Gray;

            return new Palette
            {
                Background = Brush("NetworkBackground"),
                GridDot = Brush("NetworkGridDot"),
                Node = Brush("NetworkNode"),
                NodeRead = Brush("NetworkNodeRead"),
                NodeTrail = Brush("NetworkNodeTrail"),
                NodeHover = Brush("NetworkNodeHover"),
                UnreadDot = Brush("NetworkUnreadDot"),
                HaloSelected = Brush("NetworkHaloSelected"),
                HaloHover = Brush("NetworkHaloHover"),
                RingSelected = new Pen(Brush("NetworkRingSelected"), 2),
                RingMatch = new Pen(Brush("NetworkRingMatch"), 2),
                Label = Brush("NetworkLabel"),
                LabelRead = Brush("NetworkLabelRead"),
                LabelActive = Brush("NetworkLabelActive"),
                Caption = Brush("NetworkCaption"),
                Edge = Brush("NetworkEdge"),
                EdgeIndirect = Brush("NetworkEdgeIndirect"),
                EdgeNear = Brush("NetworkEdgeNear"),
                EdgeTrail = Brush("NetworkEdgeTrail"),
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
}
