using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mindweave.Desktop.Network;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Controls;

/// <summary>Background and true source relationships are drawn; hidden sessions remain explicit paths.</summary>
public sealed class NetworkEdges : Panel
{
    private readonly NetworkViewModel page;
    public NetworkEdges(NetworkViewModel page) { this.page = page; IsHitTestVisible = false; Refresh(); }
    public void Refresh()
    {
        Children.Clear(); Children.Add(new EdgeDrawing(page));
        foreach (var edge in page.Edges.Where(e => e.Path.Length > 2))
            Children.Add(new TextBlock { Text = $"经{edge.Path.Length - 2}个未总结会话", FontSize = 11, Background = Brush.Parse("#F4F5EF"), Padding = new(3, 1), Tag = edge });
        InvalidateMeasure(); InvalidateVisual();
    }
    public void UpdateProjection()
    {
        InvalidateArrange();
        // Panel invalidation does not invalidate its retained child drawing commands.
        Children[0].InvalidateVisual();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        Children[0].Measure(availableSize);
        foreach (var label in Children.OfType<TextBlock>()) label.Measure(Size.Infinity);
        return new(0, 0);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var label in Children.OfType<TextBlock>())
        {
            var edge = (Mindweave.Application.Network.NetworkEdge)label.Tag!;
            if (!page.Session.Positions.TryGetValue(edge.ParentNodeId, out var parent) || !page.Session.Positions.TryGetValue(edge.ChildNodeId, out var child)) continue;
            var midpoint = NetworkViewportMath.ToScreen(new((parent.X + child.X) / 2, (parent.Y + child.Y) / 2), page.Session.Viewport, finalSize.Width, finalSize.Height);
            label.Arrange(new Rect(midpoint.X - label.DesiredSize.Width / 2, midpoint.Y - label.DesiredSize.Height / 2, label.DesiredSize.Width, label.DesiredSize.Height));
        }
        Children[0].Arrange(new Rect(finalSize)); return finalSize;
    }
    private sealed class EdgeDrawing(NetworkViewModel page) : Control
    {
        public override void Render(DrawingContext context)
        {
            base.Render(context); context.DrawRectangle(Brush.Parse("#F4F5EF"), null, new Rect(Bounds.Size));
            var gridPen = new Pen(Brush.Parse("#E6E9DF"), 1);
            for (double x = 20; x < Bounds.Width; x += 32)
                for (double y = 20; y < Bounds.Height; y += 32) context.DrawLine(gridPen, new(x, y), new(x + 1, y));
            foreach (var edge in page.Edges)
            {
                if (!page.Session.Positions.TryGetValue(edge.ParentNodeId, out var parent) || !page.Session.Positions.TryGetValue(edge.ChildNodeId, out var child)) continue;
                var a = NetworkViewportMath.ToScreen(parent, page.Session.Viewport, Bounds.Width, Bounds.Height);
                var b = NetworkViewportMath.ToScreen(child, page.Session.Viewport, Bounds.Width, Bounds.Height);
                var pen = new Pen(Brush.Parse("#8B967F"), 1.5, edge.Path.Length > 2 ? DashStyle.Dash : null);
                context.DrawLine(pen, new(a.X, a.Y), new(b.X, b.Y));
            }
        }
    }
}
