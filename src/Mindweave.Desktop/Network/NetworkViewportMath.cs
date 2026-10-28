using System.Collections.Immutable;
using Mindweave.Core.Network;

namespace Mindweave.Desktop.Network;

/// <summary>World-space camera center and screen pixels per world unit. Resize changes no state.</summary>
public readonly record struct NetworkViewport(double CenterX, double CenterY, double Scale);

public static class NetworkViewportMath
{
    public const double MinScale = 0.15;
    public const double MaxScale = 2.5;
    public const double ZoomFactor = 1.15;

    public static NodePosition ToScreen(NodePosition world, NetworkViewport viewport, double width, double height)
    {
        world.Validate(nameof(world)); Validate(viewport); ValidateSize(width, height);
        return Point((world.X - viewport.CenterX) * viewport.Scale + width / 2,
            (world.Y - viewport.CenterY) * viewport.Scale + height / 2);
    }

    public static NodePosition ToWorld(NodePosition screen, NetworkViewport viewport, double width, double height)
    {
        screen.Validate(nameof(screen)); Validate(viewport); ValidateSize(width, height);
        return Point(viewport.CenterX + (screen.X - width / 2) / viewport.Scale,
            viewport.CenterY + (screen.Y - height / 2) / viewport.Scale);
    }

    /// <summary>
    /// Fits all measured rectangles. Zero width or height defers fit and returns current (or the
    /// default camera). Minimum zoom or an area smaller than twice padding may prevent full fit.
    /// </summary>
    public static NetworkViewport Fit(ImmutableDictionary<Guid, NodePosition> positions,
        ImmutableDictionary<Guid, NodeExtent> extents, double width, double height,
        NetworkViewport? current = null, double padding = 48)
    {
        ArgumentNullException.ThrowIfNull(positions); ArgumentNullException.ThrowIfNull(extents);
        ValidateSize(width, height); Finite(padding, nameof(padding));
        if (padding < 0 || padding > 1e9) throw new ArgumentOutOfRangeException(nameof(padding));
        var retained = current ?? new(0, 0, 1); Validate(retained);
        foreach (var point in positions.Values) point.Validate(nameof(positions));
        foreach (var extent in extents.Values) extent.Validate(nameof(extents));
        if (width == 0 || height == 0) return retained;
        if (positions.Count == 0) return new(0, 0, 1);

        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var (id, point) in positions)
        {
            var extent = extents.GetValueOrDefault(id, NodeExtent.Default);
            left = Math.Min(left, point.X - extent.Width / 2); right = Math.Max(right, point.X + extent.Width / 2);
            top = Math.Min(top, point.Y - extent.Height / 2); bottom = Math.Max(bottom, point.Y + extent.Height / 2);
        }
        var scale = Math.Clamp(Math.Min(Math.Max(0, width - 2 * padding) / (right - left),
            Math.Max(0, height - 2 * padding) / (bottom - top)), MinScale, MaxScale);
        return Camera(left + (right - left) / 2, top + (bottom - top) / 2, scale);
    }

    /// <summary>Positive deltas move the content right/down by that many screen pixels.</summary>
    public static NetworkViewport Pan(NetworkViewport viewport, double deltaX, double deltaY)
    {
        Validate(viewport); Finite(deltaX, nameof(deltaX)); Finite(deltaY, nameof(deltaY));
        return Camera(viewport.CenterX - deltaX / viewport.Scale, viewport.CenterY - deltaY / viewport.Scale, viewport.Scale);
    }

    /// <summary>Positive wheel delta zooms in at the screen anchor; center buttons use (width/2,height/2).</summary>
    public static NetworkViewport ZoomAt(NetworkViewport viewport, NodePosition screenAnchor,
        double width, double height, double wheelDelta)
    {
        Validate(viewport); ValidateSize(width, height); screenAnchor.Validate(nameof(screenAnchor)); Finite(wheelDelta, nameof(wheelDelta));
        var world = ToWorld(screenAnchor, viewport, width, height);
        var exponent = Math.Log(viewport.Scale) + wheelDelta * Math.Log(ZoomFactor);
        var scale = exponent >= Math.Log(MaxScale) ? MaxScale : exponent <= Math.Log(MinScale) ? MinScale : Math.Exp(exponent);
        if (scale == viewport.Scale) return viewport;
        return Camera(world.X - (screenAnchor.X - width / 2) / scale,
            world.Y - (screenAnchor.Y - height / 2) / scale, scale);
    }

    private static void Validate(NetworkViewport viewport)
    {
        new NodePosition(viewport.CenterX, viewport.CenterY).Validate(nameof(viewport));
        if (!double.IsFinite(viewport.Scale) || viewport.Scale < MinScale || viewport.Scale > MaxScale)
            throw new ArgumentOutOfRangeException(nameof(viewport));
    }

    private static void ValidateSize(double width, double height)
    {
        Finite(width, nameof(width)); Finite(height, nameof(height));
        if (width < 0 || width > 1e9) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0 || height > 1e9) throw new ArgumentOutOfRangeException(nameof(height));
    }

    private static void Finite(double value, string paramName)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(paramName);
    }

    private static NodePosition Point(double x, double y)
    {
        var point = new NodePosition(x, y); point.Validate("result"); return point;
    }

    private static NetworkViewport Camera(double x, double y, double scale)
    {
        var camera = new NetworkViewport(x, y, scale); Validate(camera); return camera;
    }
}
