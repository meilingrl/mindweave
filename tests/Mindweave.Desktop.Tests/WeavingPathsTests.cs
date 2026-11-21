using Mindweave.Desktop.Views;

namespace Mindweave.Desktop.Tests;

/// <summary>The weaving indicator's paths (docs/design/generating-indicator.md): where the three dots are at a moment.</summary>
public class WeavingPathsTests
{
    private static readonly WeavingMotion[] AllMotions = Enum.GetValues<WeavingMotion>();

    [Fact]
    public void Every_dot_of_joining_grows_once_per_cycle_and_none_at_the_cycle_seam()
    {
        var cycle = WeavingPaths.JoiningCycle.TotalSeconds;
        for (var dot = 0; dot < 3; dot++)
        {
            var radii = Enumerable.Range(0, 480).Select(step => WeavingPaths.At(WeavingMotion.Joining, step * cycle / 240).Dots[dot].Radius).ToList();
            var peaks = Enumerable.Range(1, radii.Count - 2).Count(i => radii[i] > radii[i - 1] && radii[i] >= radii[i + 1] && radii[i] > 2.6);
            Assert.Equal(2, peaks);
            Assert.True(radii.Max() >= 2.8, $"dot {dot} only reaches {radii.Max()}");
        }

        // The seam where a cycle starts again: every dot at rest, so none jumps.
        Assert.All(WeavingPaths.At(WeavingMotion.Joining, cycle).Dots, d => Assert.Equal(WeavingPaths.RestRadius, d.Radius, 3));
    }

    [Fact]
    public void In_the_braid_the_front_dot_is_the_largest_and_the_most_solid()
    {
        for (var step = 0; step < 100; step++)
        {
            var dots = WeavingPaths.At(WeavingMotion.Braid, step * 0.014).Dots;
            var largest = dots.MaxBy(d => d.Radius);
            Assert.Equal(dots.Max(d => d.Opacity), largest.Opacity, 6);
        }
    }

    [Fact]
    public void Tightening_draws_the_braid_in_to_about_half_its_size()
    {
        static double Width(WeavingMotion motion) => Enumerable.Range(0, 200)
            .SelectMany(step => WeavingPaths.At(motion, step * 0.01).Dots).Select(d => d.Centre.X).Aggregate(
                (Min: double.MaxValue, Max: double.MinValue), (range, x) => (Math.Min(range.Min, x), Math.Max(range.Max, x)), range => range.Max - range.Min);

        var ratio = Width(WeavingMotion.Tightening) / Width(WeavingMotion.Braid);
        Assert.InRange(ratio, 0.4, 0.6);
        Assert.True(WeavingPaths.Period(WeavingMotion.Tightening) < WeavingPaths.Period(WeavingMotion.Braid));
    }

    [Fact]
    public void Each_motion_repeats_after_its_period()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.4), WeavingPaths.Period(WeavingMotion.Braid));
        Assert.Equal(TimeSpan.FromSeconds(1.2), WeavingPaths.Period(WeavingMotion.Thread));
        foreach (var motion in AllMotions)
        {
            var period = WeavingPaths.Period(motion).TotalSeconds;
            foreach (var t in new[] { 0.13, 0.77, 1.9 })
            {
                var a = WeavingPaths.At(motion, t).Dots;
                var b = WeavingPaths.At(motion, t + period).Dots;
                for (var i = 0; i < 3; i++)
                {
                    Assert.Equal(a[i].Radius, b[i].Radius, 6);
                    Assert.Equal(a[i].Opacity, b[i].Opacity, 6);
                    if (motion != WeavingMotion.Joining)
                    {
                        // Joining also turns, once every six seconds: only its sizes repeat every cycle.
                        Assert.Equal(a[i].Centre.X, b[i].Centre.X, 6);
                        Assert.Equal(a[i].Centre.Y, b[i].Centre.Y, 6);
                    }
                }
            }
        }
    }

    [Fact]
    public void Every_motion_stays_inside_its_box()
    {
        foreach (var motion in AllMotions)
        {
            for (var step = 0; step < 600; step++)
            {
                var frame = WeavingPaths.At(motion, step * 0.011);
                Assert.Equal(3, frame.Dots.Count);
                foreach (var dot in frame.Dots)
                {
                    Assert.InRange(dot.Centre.X - dot.Radius, 0, WeavingPaths.Size.Width);
                    Assert.InRange(dot.Centre.X + dot.Radius, 0, WeavingPaths.Size.Width);
                    Assert.InRange(dot.Centre.Y - dot.Radius, 0, WeavingPaths.Size.Height);
                    Assert.InRange(dot.Centre.Y + dot.Radius, 0, WeavingPaths.Size.Height);
                    Assert.InRange(dot.Opacity, 0, 1);
                }
            }
        }
    }

    [Fact]
    public void Reaching_sends_one_dot_out_along_a_link_and_back_while_two_rest()
    {
        var period = WeavingPaths.Period(WeavingMotion.Reaching).TotalSeconds;
        var home = WeavingPaths.At(WeavingMotion.Reaching, 0);
        var out_ = WeavingPaths.At(WeavingMotion.Reaching, period / 2);

        Assert.Equal(home.Dots[0], out_.Dots[0]);
        Assert.Equal(home.Dots[1], out_.Dots[1]);
        Assert.True(out_.Dots[2].Centre.X - home.Dots[2].Centre.X > 10);
        Assert.True(out_.Dots[2].Radius > home.Dots[2].Radius && out_.Dots[2].Opacity > home.Dots[2].Opacity);
        var link = Assert.Single(out_.Links);
        Assert.Equal((home.Dots[1].Centre, out_.Dots[2].Centre), (link.From, link.To));
    }

    [Fact]
    public void Holding_keeps_the_dots_in_place_and_breathes_them_together()
    {
        var period = WeavingPaths.Period(WeavingMotion.Holding).TotalSeconds;
        var rest = WeavingPaths.At(WeavingMotion.Holding, 0);
        var full = WeavingPaths.At(WeavingMotion.Holding, period / 2);

        Assert.Equal(rest.Dots.Select(d => d.Centre), full.Dots.Select(d => d.Centre));
        Assert.All(full.Dots, d => Assert.Equal(full.Dots[0].Radius, d.Radius, 6));
        Assert.True(full.Dots[0].Radius > rest.Dots[0].Radius && full.Dots[0].Opacity > rest.Dots[0].Opacity);
        Assert.Empty(full.Links);
    }

    [Fact]
    public void Only_joining_and_reaching_have_links_and_only_the_thread_has_a_thread()
    {
        foreach (var motion in AllMotions)
        {
            var frame = WeavingPaths.At(motion, WeavingPaths.JoiningCycle.TotalSeconds * 0.6);
            Assert.Equal(motion is WeavingMotion.Joining or WeavingMotion.Reaching, frame.Links.Count > 0);
            Assert.Equal(motion == WeavingMotion.Thread, frame.ThreadOpacity > 0);
        }
    }

    [Fact]
    public void A_change_of_motion_glides_from_where_the_dots_were()
    {
        var from = WeavingPaths.At(WeavingMotion.Braid, 0.3);
        var to = WeavingPaths.At(WeavingMotion.Thread, 0.3);

        var start = WeavingPaths.Blend(from, to, 0);
        var middle = WeavingPaths.Blend(from, to, 0.5);
        var end = WeavingPaths.Blend(from, to, 1);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(from.Dots[i].Centre, start.Dots[i].Centre);
            Assert.Equal(to.Dots[i].Centre, end.Dots[i].Centre);
            Assert.InRange(middle.Dots[i].Centre.X, Math.Min(from.Dots[i].Centre.X, to.Dots[i].Centre.X), Math.Max(from.Dots[i].Centre.X, to.Dots[i].Centre.X));
        }

        // The new motion's thread fades in with the glide rather than appearing at once.
        Assert.Equal(0, start.ThreadOpacity);
        Assert.Equal(to.ThreadOpacity, end.ThreadOpacity);
        Assert.Equal(TimeSpan.FromSeconds(0.3), WeavingPaths.GlideDuration);
    }
}
