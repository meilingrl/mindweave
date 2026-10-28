using System.Collections.Immutable;
using Mindweave.Core.Network;
using Mindweave.Desktop.Network;
using Xunit;

namespace Mindweave.Tests;

public sealed class NetworkViewportTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly ImmutableDictionary<Guid, NodeExtent> Extents = ImmutableDictionary<Guid, NodeExtent>.Empty;

    // Catches forgetting the viewport center, half-screen offset, or scale in either transform.
    [Fact]
    public void World_screen_roundtrip_and_hand_calculated_projection_agree()
    {
        var viewport = new NetworkViewport(100, -50, 2);
        Assert.Equal(new NodePosition(700, 460), NetworkViewportMath.ToScreen(new(150, 30), viewport, 1200, 600));
        var random = new Random(521);
        for (var i = 0; i < 100; i++)
        {
            var world = new NodePosition(random.NextDouble() * 20000 - 10000, random.NextDouble() * 20000 - 10000);
            var camera = new NetworkViewport(51.25, -32.75, .15 + random.NextDouble() * 2.35);
            AssertClose(world, NetworkViewportMath.ToWorld(NetworkViewportMath.ToScreen(world, camera, 1234, 789), camera, 1234, 789));
        }
    }

    // Catches zooming around the camera center when the user points away from it.
    [Theory]
    [InlineData(0, 0, 4)][InlineData(900, 520, -8)][InlineData(321.75, 99.25, 10000)][InlineData(777, 33, -10000)]
    public void Zoom_keeps_pointer_world_point_fixed_including_scale_limits(double x, double y, double delta)
    {
        var viewport = new NetworkViewport(-22.5, 71.125, 1);
        var pointer = new NodePosition(x, y);
        var before = NetworkViewportMath.ToWorld(pointer, viewport, 1000, 600);
        var zoomed = NetworkViewportMath.ZoomAt(viewport, pointer, 1000, 600, delta);
        Assert.InRange(zoomed.Scale, .15, 2.5);
        AssertClose(before, NetworkViewportMath.ToWorld(pointer, zoomed, 1000, 600));
    }

    [Fact]
    public void Center_zoom_uses_115_percent_factor_and_pan_uses_screen_pixels()
    {
        var viewport = new NetworkViewport(100, -50, 2);
        Assert.Equal(new NetworkViewport(80, -40, 2), NetworkViewportMath.Pan(viewport, 40, -20));
        var zoomed = NetworkViewportMath.ZoomAt(new(100, -50, 1), new(500, 300), 1000, 600, 1);
        Assert.Equal(100, zoomed.CenterX); Assert.Equal(-50, zoomed.CenterY);
        Assert.InRange(Math.Abs(zoomed.Scale - 1.15), 0, 1e-12);
        Assert.Equal(2.5, NetworkViewportMath.ZoomAt(viewport, new(500, 300), 1000, 600, double.MaxValue).Scale);
        Assert.Equal(.15, NetworkViewportMath.ZoomAt(viewport, new(500, 300), 1000, 600, -double.MaxValue).Scale);
    }

    // Catches fitting centers while clipping a long measured title, or mutating world positions for ShowAll.
    [Fact]
    public void Fit_includes_all_node_rectangles_and_does_not_change_positions()
    {
        var positions = ImmutableDictionary<Guid, NodePosition>.Empty.Add(A, new(-400, -200)).Add(B, new(700, 300));
        var before = positions.OrderBy(p => p.Key).ToArray();
        var extents = Extents.Add(A, new(1000, 180)).Add(B, new(200, 300));
        var fitted = NetworkViewportMath.Fit(positions, extents, 1200, 800);
        Assert.Equal(-50, fitted.CenterX); Assert.Equal(80, fitted.CenterY);
        Assert.InRange(Math.Abs(fitted.Scale - 1104.0 / 1700), 0, 1e-12);
        foreach (var pair in positions)
        {
            var extent = extents[pair.Key];
            var topLeft = NetworkViewportMath.ToScreen(new(pair.Value.X - extent.Width / 2, pair.Value.Y - extent.Height / 2), fitted, 1200, 800);
            var bottomRight = NetworkViewportMath.ToScreen(new(pair.Value.X + extent.Width / 2, pair.Value.Y + extent.Height / 2), fitted, 1200, 800);
            Assert.InRange(topLeft.X, 48 - 1e-9, 1152); Assert.InRange(topLeft.Y, 48 - 1e-9, 752);
            Assert.InRange(bottomRight.X, 48, 1152 + 1e-9); Assert.InRange(bottomRight.Y, 48, 752 + 1e-9);
        }
        Assert.Equal(before, positions.OrderBy(p => p.Key));
    }

    [Fact]
    public void Fit_limits_scale_and_explicitly_cannot_fit_a_huge_graph_at_minimum_zoom()
    {
        var positions = ImmutableDictionary<Guid, NodePosition>.Empty.Add(A, new(-10000, 0)).Add(B, new(10000, 0));
        var viewport = NetworkViewportMath.Fit(positions, Extents, 1000, 600);
        Assert.Equal(.15, viewport.Scale);
        Assert.True(NetworkViewportMath.ToScreen(positions[A], viewport, 1000, 600).X < 48);
        Assert.Equal(2.5, NetworkViewportMath.Fit(ImmutableDictionary<Guid, NodePosition>.Empty.Add(A, new(10, 20)), Extents.Add(A, new(1, 1)), 1000, 600).Scale);
    }

    // Catches losing a retained session viewport while layout dimensions are not measured yet.
    [Fact]
    public void Zero_size_defers_fit_and_resize_only_changes_projection()
    {
        var current = new NetworkViewport(123, 456, .75);
        var positions = ImmutableDictionary<Guid, NodePosition>.Empty.Add(A, new(0, 0));
        Assert.Equal(current, NetworkViewportMath.Fit(positions, Extents, 0, 800, current));
        Assert.Equal(current, NetworkViewportMath.Fit(positions, Extents, 1200, 0, current));
        Assert.Equal(new(0, 0, 1), NetworkViewportMath.Fit(positions, Extents, 0, 0));
        Assert.Equal(new NodePosition(400, 300), NetworkViewportMath.ToScreen(new(123, 456), current, 800, 600));
        Assert.Equal(new NodePosition(600, 400), NetworkViewportMath.ToScreen(new(123, 456), current, 1200, 800));
        Assert.Equal(new(123, 456, .75), current);
        Assert.Equal(new(0, 0, 1), NetworkViewportMath.Fit(ImmutableDictionary<Guid, NodePosition>.Empty, Extents, 1200, 800, current));
    }

    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(double.NegativeInfinity)]
    public void Nonfinite_inputs_are_rejected_without_propagation(double invalid)
    {
        var viewport = new NetworkViewport(0, 0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.ToScreen(new(invalid, 0), viewport, 800, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.ToWorld(new(0, 0), new(0, invalid, 1), 800, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.ToWorld(new(0, 0), viewport, invalid, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.Pan(viewport, invalid, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.ZoomAt(viewport, new(400, 300), 800, 600, invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.Fit(ImmutableDictionary<Guid, NodePosition>.Empty, Extents, 800, 600, padding: invalid));
    }

    [Fact]
    public void Invalid_scale_dimensions_extents_and_positions_are_rejected()
    {
        foreach (var scale in new[] { 0, -.5, .149, 2.501, double.NaN })
            Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.Pan(new(0, 0, scale), 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.ToScreen(new(0, 0), new(0, 0, 1), -1, 800));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.Fit(ImmutableDictionary<Guid, NodePosition>.Empty, Extents, 800, 600, padding: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.Fit(ImmutableDictionary<Guid, NodePosition>.Empty.Add(A, new(double.NaN, 0)), Extents, 800, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkViewportMath.Fit(ImmutableDictionary<Guid, NodePosition>.Empty.Add(A, new(0, 0)), Extents.Add(A, new(0, 112)), 800, 600));
    }

    private static void AssertClose(NodePosition expected, NodePosition actual)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1e-9);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1e-9);
    }
}
