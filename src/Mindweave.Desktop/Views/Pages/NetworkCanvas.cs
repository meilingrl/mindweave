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

    /// <summary>The largest label size; below it labels scale with the zoom so they stay inside their reserved box.</summary>
    public const double LabelMaxSize = 13;

    /// <summary>A selected, hovered or matching label shown below the hiding threshold is drawn at least this large.</summary>
    public const double FocusedLabelMinSize = 10;

    /// <summary>Screen px between an exploration's caption and the centre of its topmost node.</summary>
    public const double CaptionGap = 26;

    private const double CaptionSize = 11;
    private const double EdgeLabelSize = 10;
    private const double EdgeLabelEmphasizedSize = 11;

    /// <summary>Decorative pull towards the pointer: fixed at the sample's default 30% (1.5 px at most), drawing only.</summary>
    internal const double AttractionStrength = 0.3;
    private const double AttractionReach = 90;
    private const double GridSpacing = 28;

    private static readonly Typeface Face = new(FontFamily.Default);
    internal static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    /// <summary>While a node is dragged: the platform's "move" cursor (Avalonia has no closed-hand cursor on every platform).</summary>
    internal static readonly Cursor GrabCursor = new(StandardCursorType.SizeAll);

    /// <summary>A press on a node becomes a drag only after the pointer moves this far (screen px); less is a click.</summary>
    internal const double DragThreshold = 4;

    /// <summary>The lift shadow's layers, outermost first: how much wider than the dot, how far down, how opaque.</summary>
    private static readonly (double Grow, double Drop, double Opacity)[] ShadowLayers = [(5, 4.5, 0.35), (3, 3.5, 0.55), (1, 2.5, 0.8)];

    /// <summary>
    /// The lift glow's layers (dark only), outermost first: how far past the dot it reaches and how opaque. The glow
    /// sits just outside the selection halo (dot + 11), so the halo stays the same and the glow fades out around it.
    /// </summary>
    private static readonly (double Grow, double Opacity)[] GlowLayers = [(19, 0.08), (16, 0.14), (13.5, 0.22)];

    /// <summary>The thin rim at the halo's edge that outlines a lifted node (dark only): radius past the dot, opacity.</summary>
    private const double GlowRimGrow = 11;
    private const double GlowRimOpacity = 0.6;

    private readonly Dictionary<SummaryNodeId, Vector> _offsets = [];
    private readonly DispatcherTimer _motion;
    private NetworkHomeViewModel? _page;
    private NetworkPalette? _palette;
    private Point? _panFrom;
    private Viewport _panStart;
    private bool _dragged;
    private Point? _pointer;
    private SummaryNodeId? _pressed;
    private Point _pressedAt;
    private Vector _grab;
    private (double X, double Y) _dragAt;

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

    /// <summary>Where the pointer is for hover and the pull; null when it is off the canvas.</summary>
    internal Point? Pointer => _pointer;

    /// <summary>The node being dragged, drawn at the pointer until released; null otherwise.</summary>
    internal SummaryNodeId? Dragging { get; private set; }

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
        _pressed = null;
        Dragging = null;
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

    /// <summary>Where a node is drawn, in canvas coordinates: at the pointer while dragged, else at its layout position.</summary>
    private (double X, double Y) PositionOf(SummaryNodeId id, double x, double y) => id == Dragging ? _dragAt : (x, y);

    private Point ScreenOf(NodeView node)
    {
        var (x, y) = PositionOf(node.Id, node.X, node.Y);
        return ToScreen(x, y);
    }

    /// <summary>
    /// Label size: <see cref="NetworkLayout.LabelEm"/> × zoom, at most 13 px. A label is then never wider on screen than
    /// the box the layout reserved for it (its estimated width × zoom), so the no-overlap guarantee holds as drawn.
    /// </summary>
    internal static double LabelSize(double zoom) => Math.Min(LabelMaxSize, NetworkLayout.LabelEm * zoom);

    /// <summary>
    /// The node under a control point: its hit box at its layout position. Hidden labels are not clickable.
    /// </summary>
    internal NodeView? HitTest(Point screen)
    {
        if (_page is null)
        {
            return null;
        }

        var zoom = Current.Zoom;
        foreach (var node in _page.Nodes.Reverse())
        {
            if (HitBox(ScreenOf(node), node, zoom, IsLabelVisible(node, zoom)).Contains(screen))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>Labels show above the hiding threshold; below it only the selected, hovered and matching ones.</summary>
    private bool IsLabelVisible(NodeView node, double zoom) =>
        zoom > LabelHideScale || node.Id == Hovered || (_page is { } page && (node.Id == page.Session.Selected || page.Matches.Contains(node.Id)));

    /// <summary>
    /// A node's hit box around its screen centre: from just left of the dot to the end of its label (only the dot when
    /// the label is hidden), clamped to the node's layout box widened by half the layout's spacing. Boxes of nodes the
    /// layout keeps apart therefore never overlap, at any zoom.
    /// </summary>
    internal static Rect HitBox(Point center, NodeView node, double zoom, bool labelVisible)
    {
        ArgumentNullException.ThrowIfNull(node);
        var half = Math.Min(20, NetworkLayout.NodeHeight * zoom / 2);
        var left = Math.Min(16, (NetworkLayout.NodeRadius + (NetworkLayout.LabelPadding / 2)) * zoom);
        var right = labelVisible
            ? Math.Min(
                RadiusOf(node) + NetworkLayout.LabelGap + (node.LabelWidth * LabelSize(zoom) / NetworkLayout.LabelEm) + 12,
                (NetworkLayout.NodeRadius + NetworkLayout.LabelGap + node.LabelWidth + (NetworkLayout.LabelPadding / 2)) * zoom)
            : Math.Min(RadiusOf(node) + 4, (NetworkLayout.NodeRadius + (NetworkLayout.LabelPadding / 2)) * zoom);
        return new Rect(center.X - left, center.Y - half, left + right, 2 * half);
    }

    private static double RadiusOf(NodeView node) => node.IsRoot ? RootRadius : ChildRadius;

    /// <summary>What each node looks like now, in drawing order. The render pass and the tests read the same list.</summary>
    internal IReadOnlyList<NodePaint> PaintNodes()
    {
        if (_page is null)
        {
            return [];
        }

        var palette = _palette ??= NetworkPalette.From(this);
        var zoom = Current.Zoom;
        var size = LabelSize(zoom);
        var drawn = _page.Nodes.ToDictionary(n => n.Id, n => ScreenOf(n) + _offsets.GetValueOrDefault(n.Id));
        var incident = _page.Edges.SelectMany(e => new[] { (Node: e.From, Other: e.To), (Node: e.To, Other: e.From) })
            .Where(x => drawn.ContainsKey(x.Other)).ToLookup(x => x.Node, x => drawn[x.Other]);
        var selected = _page.Session.Selected;
        var trail = _page.Trail;
        var matches = _page.Matches;
        return [.. _page.Nodes.Select(node =>
        {
            var hovered = node.Id == Hovered;
            var lifted = node.Id == Dragging;
            var isSelected = node.Id == selected;
            var matched = matches.Contains(node.Id);
            var radius = RadiusOf(node) + (hovered || lifted ? HoverGrowth : 0);
            var center = drawn[node.Id];
            var ring = isSelected ? NodeRing.Selected : matched ? NodeRing.Match : NodeRing.None;
            var label = NetworkLayout.Label(node.ShortTitle, node.HasCandidate);
            var visible = IsLabelVisible(node, zoom);
            var labelSize = zoom > LabelHideScale ? size : Math.Max(size, FocusedLabelMinSize);
            var brush = hovered || isSelected ? palette.LabelActive : !node.Unread ? palette.LabelRead : palette.Label;
            var text = visible ? Text(label, labelSize, brush, node.Unread ? FontWeight.SemiBold : FontWeight.Normal) : null;
            var origin = text is null ? default : LabelOrigin(center, radius, new Size(text.Width, text.Height), incident[node.Id]);
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
                LabelVisible: visible,
                LabelBold: node.Unread,
                labelSize,
                label)
            {
                Text = text,
                LabelBounds = text is null ? default : new Rect(origin, new Size(text.Width, text.Height)),
                Lifted = lifted,
            };
        })];
    }

    /// <summary>
    /// Where a label goes: to the right of the dot, level with it; lifted above (or dropped below) the dot when one of
    /// the node's own edges would run through it, as for a child drawn left of its source, so no edge strikes it out.
    /// </summary>
    internal static Point LabelOrigin(Point center, double radius, Size text, IEnumerable<Point> neighbours)
    {
        ArgumentNullException.ThrowIfNull(neighbours);
        var x = center.X + radius + NetworkLayout.LabelGap;
        var level = new Point(x, center.Y - 9);
        var others = neighbours.ToList();
        bool Clear(Point origin) => !others.Any(other => Crosses(center, other, new Rect(origin, text).Inflate(new Thickness(2, 0))));
        if (Clear(level))
        {
            return level;
        }

        var above = new Point(x, center.Y - text.Height - 3);
        var below = new Point(x, center.Y + 3);
        return Clear(above) ? above : Clear(below) ? below : level;
    }

    /// <summary>Whether the segment from <paramref name="a"/> to <paramref name="b"/> passes through the rectangle (Liang–Barsky).</summary>
    internal static bool Crosses(Point a, Point b, Rect rect)
    {
        var (t0, t1) = (0.0, 1.0);
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        foreach (var (p, q) in new[] { (-dx, a.X - rect.Left), (dx, rect.Right - a.X), (-dy, a.Y - rect.Top), (dy, rect.Bottom - a.Y) })
        {
            if (p == 0)
            {
                if (q < 0)
                {
                    return false;
                }

                continue;
            }

            var t = q / p;
            if (p < 0)
            {
                t0 = Math.Max(t0, t);
            }
            else
            {
                t1 = Math.Min(t1, t);
            }

            if (t0 > t1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Each edge from the parent's drawn position to the child's, with its state.</summary>
    internal IReadOnlyList<EdgePaint> PaintEdges()
    {
        if (_page is null)
        {
            return [];
        }

        var trail = _page.Trail;
        var zoom = Current.Zoom;
        return [.. _page.Edges.Select(edge =>
        {
            var (fromX, fromY) = PositionOf(edge.From, edge.FromX, edge.FromY);
            var (toX, toY) = PositionOf(edge.To, edge.ToX, edge.ToY);
            var from = ToScreen(fromX, fromY) + _offsets.GetValueOrDefault(edge.From);
            var to = ToScreen(toX, toY) + _offsets.GetValueOrDefault(edge.To);
            var state = trail.Contains(edge.To) ? EdgeState.Trail
                : Hovered is { } h && (h == edge.To || h == edge.From) ? EdgeState.Near
                : !edge.IsDirect ? EdgeState.Indirect
                : EdgeState.Normal;
            var highlighted = state is EdgeState.Trail or EdgeState.Near;

            // "经 N 个未总结会话" stays readable at rest (quiet), hidden with the other labels when zoomed far out.
            var label = edge.Label is { } via && (highlighted || zoom > LabelHideScale) ? via : null;
            return new EdgePaint(edge, from, to, state, !edge.IsDirect, label, highlighted);
        })];
    }

    /// <summary>Each exploration's caption, just above the topmost of its nodes and starting at the leftmost one.</summary>
    internal IReadOnlyList<CaptionPaint> PaintCaptions()
    {
        if (_page is null)
        {
            return [];
        }

        var palette = _palette ??= NetworkPalette.From(this);
        var nodes = _page.Nodes.ToDictionary(n => n.Id);
        var result = new List<CaptionPaint>();
        foreach (var caption in _page.Captions)
        {
            var members = caption.Members.Where(nodes.ContainsKey).Select(id => (Node: nodes[id], At: ScreenOf(nodes[id]))).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var text = Text(caption.Text, CaptionSize, palette.Caption);
            var left = members.Min(m => m.At.X - RadiusOf(m.Node));
            var top = members.Min(m => m.At.Y);
            result.Add(new CaptionPaint(caption, text, new Point(left, top - CaptionGap - text.Height)));
        }

        return result;
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var palette = _palette ??= NetworkPalette.From(this);
        context.FillRectangle(palette.Background, new Rect(Bounds.Size));
        DrawGrid(context, palette);
        if (_page is null || _page.Nodes.Count == 0)
        {
            return;
        }

        // Layers: resting edges, label backdrops (resting edges pass behind labels), highlighted edges (above the
        // backdrops, so a trail is never hidden by a label), nodes, label text, captions.
        var edges = PaintEdges();
        var nodes = PaintNodes().OrderBy(n => n.Lifted).ToList();
        foreach (var edge in edges.Where(e => !e.Highlighted))
        {
            DrawEdge(context, palette, edge);
        }

        using (context.PushOpacity(0.85))
        {
            foreach (var node in nodes.Where(n => n.Text is not null))
            {
                context.FillRectangle(palette.Background, node.Backdrop, 3);
            }
        }

        foreach (var edge in edges.Where(e => e.Highlighted))
        {
            DrawEdge(context, palette, edge);
        }

        foreach (var node in nodes)
        {
            DrawNode(context, palette, node);
        }

        foreach (var node in nodes)
        {
            if (node.Text is { } text)
            {
                context.DrawText(text, node.LabelBounds.TopLeft);
            }
        }

        foreach (var caption in PaintCaptions())
        {
            // Always drawn, even when labels are hidden: it names the exploration.
            context.DrawText(caption.Text, caption.Origin);
        }
    }

    private static void DrawEdge(DrawingContext context, NetworkPalette palette, EdgePaint edge)
    {
        context.DrawLine(palette.EdgePen(edge.State, edge.Dashed), edge.From, edge.To);
        if (edge.Label is not { } via)
        {
            return;
        }

        var brush = !edge.LabelEmphasized ? palette.Caption : edge.State == EdgeState.Trail ? palette.EdgeTrail : palette.EdgeNear;
        var text = Text(via, edge.LabelEmphasized ? EdgeLabelEmphasizedSize : EdgeLabelSize, brush);
        context.DrawText(text, EdgeLabelOrigin(edge, text));
    }

    /// <summary>Beside the edge's middle: above a mostly horizontal edge, to the right of a mostly vertical one.</summary>
    internal static Point EdgeLabelOrigin(EdgePaint edge, FormattedText text)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(text);
        var middle = new Point((edge.From.X + edge.To.X) / 2, (edge.From.Y + edge.To.Y) / 2);
        return Math.Abs(edge.To.X - edge.From.X) >= Math.Abs(edge.To.Y - edge.From.Y)
            ? new Point(middle.X - (text.Width / 2), middle.Y - text.Height - 2)
            : new Point(middle.X + 6, middle.Y - (text.Height / 2));
    }

    private void DrawGrid(DrawingContext context, NetworkPalette palette)
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

    private static void DrawNode(DrawingContext context, NetworkPalette palette, NodePaint node)
    {
        var p = node.Center;
        var r = node.Radius;
        var surface = palette.Background;
        var glow = node.Lifted && palette.HasDragGlow;
        if (glow)
        {
            // Dark only: a black shadow hardly shows on the near-black canvas, so the lifted node also glows faintly.
            foreach (var (grow, opacity) in GlowLayers)
            {
                using (context.PushOpacity(opacity))
                {
                    context.DrawEllipse(palette.DragGlow, null, p, r + grow, r + grow);
                }
            }
        }

        if (node.Halo)
        {
            surface = node.Ring == NodeRing.Selected ? palette.HaloSelected : palette.HaloHover;
            context.DrawEllipse(surface, null, p, r + 11, r + 11);
        }

        if (node.Lifted)
        {
            // Lifted off the canvas: a soft shadow falling a little below the dot in three translucent layers,
            // the widest faintest, the closest darkest, so it fades out instead of ending in a hard edge.
            foreach (var (grow, drop, opacity) in ShadowLayers)
            {
                using (context.PushOpacity(opacity))
                {
                    context.DrawEllipse(palette.DragShadow, null, p + new Vector(0, drop), r + grow, r + grow - 1);
                }
            }

            if (glow)
            {
                using (context.PushOpacity(GlowRimOpacity))
                {
                    context.DrawEllipse(null, palette.DragGlowRim, p, r + GlowRimGrow, r + GlowRimGrow);
                }
            }
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
            else if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // Maybe the start of a drag: it becomes one once the pointer has moved past the threshold.
                _pressed = node.Id;
                _pressedAt = point;
                var (x, y) = ToCanvas(point);
                _grab = new Vector(x - node.X, y - node.Y);
                e.Pointer.Capture(this);
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
        if (_pressed is { } pressed && _page is not null)
        {
            DragTo(pressed, point);
            return;
        }

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
        if (_panFrom is null && _pressed is null)
        {
            SetPointer(null);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_pressed is not null)
        {
            // The end of a drag places the node (an unsaved layout); a press without a drag was only the click.
            var dragged = Dragging;
            var at = _dragAt;
            _pressed = null;
            Dragging = null;
            e.Pointer.Capture(null);
            if (dragged is { } id)
            {
                _page?.MoveNode(id, at.X, at.Y);
            }

            SetPointer(e.GetPosition(this));
            InvalidateVisual();
            return;
        }

        if (_panFrom is not null && !_dragged)
        {
            // A click on empty canvas clears the selection.
            _page?.Select(null);
        }

        _panFrom = null;
        e.Pointer.Capture(null);

        // During a pan the pointer stays put while the network moves under it: hover follows what is under it now.
        SetPointer(e.GetPosition(this));
    }

    /// <summary>Capture taken away mid-drag (a window switch, a dialog): the node goes back, nothing becomes unsaved.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pressed is not null)
        {
            _pressed = null;
            Dragging = null;
            SetPointer(_pointer);
            InvalidateVisual();
        }
    }

    /// <summary>
    /// A held press on a node: below <see cref="DragThreshold"/> nothing moves (a click); past it the node follows
    /// the pointer, keeping the spot where it was grabbed. Only drawing changes until release.
    /// </summary>
    private void DragTo(SummaryNodeId node, Point point)
    {
        var delta = point - _pressedAt;
        if (Dragging is null)
        {
            if (Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y)) < DragThreshold)
            {
                return;
            }

            Dragging = node;
            Hovered = node;
            _offsets.Remove(node);
            Cursor = GrabCursor;
        }

        PlaceDragged(point);
    }

    /// <summary>
    /// The dragged node under <paramref name="point"/>, keeping the spot where it was grabbed. Worked out in canvas
    /// coordinates from the current viewport, so a wheel pan or zoom mid-drag leaves the node under the pointer.
    /// </summary>
    private void PlaceDragged(Point point)
    {
        var (x, y) = ToCanvas(point);
        _dragAt = (x - _grab.X, y - _grab.Y);
        _pointer = point;
        InvalidateVisual();
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

        if (Dragging is not null)
        {
            // The view moved under a held node: it stays under the pointer (it is placed in canvas coordinates).
            PlaceDragged(e.GetPosition(this));
        }

        SetPointer(e.GetPosition(this));
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
            InvalidateVisual();
        }

        Cursor = Dragging is not null ? GrabCursor : hovered is null ? Cursor.Default : HandCursor;

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
            // The dragged node sits exactly under the pointer; no pull on it.
            var target = node.Id == Dragging ? default : Attraction(ScreenOf(node), _pointer);
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

        var fitted = FitTo(_page.Nodes, Bounds.Size);
        _page.Session.Viewport = fitted;
        return fitted;
    }

    /// <summary>
    /// The viewport that shows every node, its label and its group's caption. Positions and label widths are canvas
    /// units; dots, the label gap and captions are screen px, so the zoom is solved with both (§11.3: zoom clamped,
    /// at most 1.5 when fitting).
    /// </summary>
    internal static Viewport FitTo(IReadOnlyList<NodeView> nodes, Size size)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        const double left = RootRadius + HoverGrowth;
        const double right = RootRadius + HoverGrowth + NetworkLayout.LabelGap;
        const double top = CaptionGap + 16;
        const double bottom = RootRadius + HoverGrowth + 4;
        var minX = nodes.Min(n => n.X);
        var maxX = nodes.Max(n => n.X + n.LabelWidth);
        var minY = nodes.Min(n => n.Y);
        var maxY = nodes.Max(n => n.Y);
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        var zoom = Math.Min(1.5, Viewport.Clamp(Math.Min(
            (size.Width - (2 * Viewport.FitMargin) - left - right) / width,
            (size.Height - (2 * Viewport.FitMargin) - top - bottom) / height)));
        return new Viewport(((minX + maxX) / 2) + ((right - left) / (2 * zoom)), ((minY + maxY) / 2) + ((bottom - top) / (2 * zoom)), zoom);
    }

    internal static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, weight), size, brush);
}
