using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mindweave.Desktop.Network;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// Draws the network and turns pointer input into selection, opening and viewport changes (architecture §11.1). All
/// drawing uses one canvas → screen transform, so nodes and edges always agree. Positions are never changed here;
/// the viewport lives in the workspace's <see cref="NetworkSession"/>.
/// </summary>
public sealed class NetworkCanvas : Control
{
    public const double DotRadius = 9;

    private static readonly IBrush Background = Brushes.White;
    private static readonly IBrush NodeFill = new SolidColorBrush(Color.Parse("#4B6BFB"));
    private static readonly IBrush ReadFill = new SolidColorBrush(Color.Parse("#9AA6C8"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#1F2430"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.Parse("#B8C0D0")), 1.5);
    private static readonly IPen ViaPen = new Pen(new SolidColorBrush(Color.Parse("#B8C0D0")), 1.5, new DashStyle([4, 4], 0));
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.Parse("#1F2430")), 2.5);
    private static readonly Typeface Face = new(FontFamily.Default);

    private NetworkHomeViewModel? _page;
    private Point? _panFrom;
    private Viewport _panStart;
    private bool _dragged;

    public NetworkCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>The viewport in use, fitted on first use.</summary>
    internal Viewport Current => _page?.Session.Viewport ?? Fit();

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

        InvalidateVisual();
        base.OnDataContextChanged(e);
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

    /// <summary>The node under a control point: its dot or its label.</summary>
    internal NodeView? HitTest(Point screen)
    {
        if (_page is null)
        {
            return null;
        }

        var zoom = Current.Zoom;
        foreach (var node in _page.Nodes.Reverse())
        {
            var p = ToScreen(node.X, node.Y);
            var dx = screen.X - p.X;
            var dy = screen.Y - p.Y;
            var onDot = (dx * dx) + (dy * dy) <= Math.Pow((DotRadius + 4) * Math.Max(zoom, 0.6), 2);
            var onLabel = Math.Abs(dx) <= Math.Max(node.LabelWidth * zoom / 2, DotRadius) && dy >= 0 && dy <= (DotRadius + 22) * zoom;
            if (onDot || onLabel)
            {
                return node;
            }
        }

        return null;
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.FillRectangle(Background, new Rect(Bounds.Size));
        if (_page is null || _page.Nodes.Count == 0)
        {
            return;
        }

        var zoom = Current.Zoom;
        foreach (var edge in _page.Edges)
        {
            var from = ToScreen(edge.FromX, edge.FromY);
            var to = ToScreen(edge.ToX, edge.ToY);
            context.DrawLine(edge.IsDirect ? EdgePen : ViaPen, from, to);
            if (edge.Label is { } label)
            {
                var text = Text(label, 11 * Math.Clamp(zoom, 0.8, 1.2), Muted);
                var origin = new Point(((from.X + to.X) / 2) - (text.Width / 2), ((from.Y + to.Y) / 2) - (text.Height / 2));
                context.FillRectangle(Background, new Rect(origin.X - 2, origin.Y, text.Width + 4, text.Height));
                context.DrawText(text, origin);
            }
        }

        var selected = _page.Session.Selected;
        var radius = DotRadius * Math.Clamp(zoom, 0.6, 1.6);
        foreach (var node in _page.Nodes)
        {
            var p = ToScreen(node.X, node.Y);
            context.DrawEllipse(node.Unread ? NodeFill : ReadFill, node.Id == selected ? SelectedPen : null, p, radius, radius);
            if (node.Unread)
            {
                // Unread is also a shape, not only a colour: a small filled badge at the dot's upper right.
                context.DrawEllipse(Label, null, new Point(p.X + radius, p.Y - radius), radius * 0.38, radius * 0.38);
            }

            var title = node.HasCandidate ? node.ShortTitle + " ↻" : node.ShortTitle;
            var text = Text(title, 13 * Math.Clamp(zoom, 0.7, 1.4), Label, node.Unread ? FontWeight.SemiBold : FontWeight.Normal);
            var origin = new Point(p.X - (text.Width / 2), p.Y + radius + 4);

            // Edges pass behind labels, so a label stays readable where an edge leaves its node downwards.
            context.FillRectangle(Background, new Rect(origin.X - 2, origin.Y, text.Width + 4, text.Height));
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
        if (_panFrom is not { } from || _page is null)
        {
            return;
        }

        var point = e.GetPosition(this);
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

    private void OnZoomRequested(object? sender, double factor) => ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);

    private void OnViewChanged(object? sender, bool reset) => InvalidateVisual();

    /// <summary>Fits every node and label into view with a margin; zoom limited to the allowed range.</summary>
    private Viewport Fit()
    {
        if (_page is null || _page.Nodes.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return new Viewport(0, 0, 1);
        }

        var minX = _page.Nodes.Min(n => n.X - Math.Max(n.LabelWidth / 2, DotRadius));
        var maxX = _page.Nodes.Max(n => n.X + Math.Max(n.LabelWidth / 2, DotRadius));
        var minY = _page.Nodes.Min(n => n.Y - DotRadius);
        var maxY = _page.Nodes.Max(n => n.Y + DotRadius + 24);
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        var zoom = Viewport.Clamp(Math.Min((Bounds.Width - (2 * Viewport.FitMargin)) / width, (Bounds.Height - (2 * Viewport.FitMargin)) / height));
        var fitted = new Viewport((minX + maxX) / 2, (minY + maxY) / 2, Math.Min(zoom, 1.5));
        _page.Session.Viewport = fitted;
        return fitted;
    }

    private static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, weight), size, brush);
}
