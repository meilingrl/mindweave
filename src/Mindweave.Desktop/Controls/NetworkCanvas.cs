using System.Collections.Immutable;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mindweave.Desktop.Network;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Controls;

/// <summary>Owns only visual/input subscriptions. Main owns the session and navigation lifecycle.</summary>
public sealed class NetworkCanvas : Panel
{
    private NetworkViewModel? page;
    private NetworkEdges? edges;
    private bool attached, needsRebuild;
    private IPointer? panPointer;
    private Point lastPointer;
    public bool DirectWheelZoom { get; set; }
    public NetworkCanvas()
    {
        ClipToBounds = true; Background = Brushes.Transparent; Focusable = true;
        DataContextChanged += (_, _) => ObservePage();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); attached = true; ObservePage(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { attached = false; StopPan(); Unsubscribe(); base.OnDetachedFromVisualTree(e); }
    private void Unsubscribe()
    {
        if (page is not null) { page.Nodes.CollectionChanged -= NodesChanged; page.PropertyChanged -= PageChanged; page.Session.PropertyChanged -= SessionChanged; }
        page = null;
    }
    private void ObservePage()
    {
        Unsubscribe(); if (!attached) return;
        page = DataContext as NetworkViewModel;
        if (page is not null) { page.Nodes.CollectionChanged += NodesChanged; page.PropertyChanged += PageChanged; page.Session.PropertyChanged += SessionChanged; }
        Rebuild();
    }
    private void NodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A metadata refresh replaces the collection in one UI turn. Build once at layout,
        // rather than allocating the partial tree again for every individual Add.
        needsRebuild = true; InvalidateMeasure();
    }
    private void PageChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(NetworkViewModel.Edges)) { edges?.Refresh(); InvalidateArrange(); } }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NetworkSession.Viewport) or nameof(NetworkSession.Positions)) { InvalidateArrange(); edges?.InvalidateVisual(); }
        if (e.PropertyName == nameof(NetworkSession.IsFitPending) && page?.Session.IsFitPending == true) InvalidateArrange();
        if (e.PropertyName == nameof(NetworkSession.IsQuiesced)) { IsEnabled = page?.Session.IsQuiesced != true; StopPan(); }
    }
    private void Rebuild()
    {
        needsRebuild = false; Children.Clear(); edges = null;
        if (page is null) return;
        Children.Add(edges = new NetworkEdges(page) { Name = "NetworkEdges" });
        foreach (var node in page.Nodes) Children.Add(new NetworkNodeControl(page, node));
        IsEnabled = !page.Session.IsQuiesced; InvalidateMeasure();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        if (needsRebuild) Rebuild();
        foreach (var child in Children) child.Measure(child is NetworkNodeControl ? new Size(200, 112) : availableSize);
        return new(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (page is null) return finalSize;
        var measured = Children.OfType<NetworkNodeControl>().ToImmutableDictionary(c => c.Node.Id, c => new NodeExtent(c.DesiredSize.Width, c.DesiredSize.Height));
        if (measured.Count != page.Session.Extents.Count || measured.Any(p => !page.Session.Extents.TryGetValue(p.Key, out var old) || old != p.Value)) page.Session.SetNodeExtents(measured);
        page.Session.SetViewportSize(finalSize.Width, finalSize.Height);
        edges?.Arrange(new Rect(finalSize));
        foreach (var control in Children.OfType<NetworkNodeControl>())
        {
            var point = NetworkViewportMath.ToScreen(control.Node.Position, page.Session.Viewport, finalSize.Width, finalSize.Height);
            control.RenderTransform = new ScaleTransform(page.Session.Viewport.Scale, page.Session.Viewport.Scale);
            control.Arrange(new Rect(point.X - 100, point.Y - 56, 200, 112));
        }
        edges?.UpdateProjection(); return finalSize;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); if (page is null) return;
        var point = e.GetPosition(this);
        if (DirectWheelZoom || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) page.Session.ZoomAt(new(point.X, point.Y), e.Delta.Y);
        else page.Session.Pan(e.Delta.X * 36, e.Delta.Y * 36);
        e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (page is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            e.Source is Visual source && (source is NetworkNodeControl || source.GetVisualAncestors().OfType<NetworkNodeControl>().Any())) return;
        Focus(); lastPointer = e.GetPosition(this); panPointer = e.Pointer; panPointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (panPointer != e.Pointer || page is null) return;
        var next = e.GetPosition(this); var delta = next - lastPointer; lastPointer = next; page.Session.Pan(delta.X, delta.Y); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); if (panPointer == e.Pointer) { StopPan(); e.Handled = true; } }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { panPointer = null; base.OnPointerCaptureLost(e); }
    private void StopPan() { var pointer = panPointer; panPointer = null; pointer?.Capture(null); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (page is null || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var controls = Children.OfType<NetworkNodeControl>().ToArray(); if (controls.Length == 0) return;
        var current = page.SelectedNode;
        var candidates = controls.Where(c => current is null || c.Node.Id != current.Id);
        if (current is not null)
            candidates = candidates.Where(c => e.Key switch { Key.Left => c.Node.X < current.X, Key.Right => c.Node.X > current.X, Key.Up => c.Node.Y < current.Y, _ => c.Node.Y > current.Y });
        var next = candidates.OrderBy(c => current is null ? 0 : Math.Pow(c.Node.X - current.X, 2) + Math.Pow(c.Node.Y - current.Y, 2)).FirstOrDefault();
        if (next is not null) { page.Session.FocusNode(next.Node.Id); next.Focus(); } e.Handled = true;
    }
}
