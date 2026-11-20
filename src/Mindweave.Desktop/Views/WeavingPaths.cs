using Avalonia;

namespace Mindweave.Desktop.Views;

/// <summary>What the weaving indicator is doing (docs/design/generating-indicator.md).</summary>
public enum WeavingMotion
{
    /// <summary>Waiting for the answer, or a summary being written: three strands plaited along a sideways figure eight.</summary>
    Braid,

    /// <summary>A reasoning model thinking: links drawn one by one across a slowly turning triangle.</summary>
    Joining,

    /// <summary>Compressing the context: the braid drawn in to half its size, looping faster.</summary>
    Tightening,

    /// <summary>Text streaming in: a ripple running along a faint thread through the three dots.</summary>
    Thread,

    /// <summary>A tool fetching material (searching, reading a page, calling a tool): one dot reaches out along a thread and back.</summary>
    Reaching,

    /// <summary>Waiting for the user's approval: three dots in a row, still, breathing together.</summary>
    Holding,
}

/// <summary>One dot: its centre in the indicator's box, its radius and how solid it is.</summary>
public readonly record struct WeavingDot(Point Centre, double Radius, double Opacity);

/// <summary>A straight link between two dots, drawn as far as <see cref="To"/>.</summary>
public readonly record struct WeavingLink(Point From, Point To, double Opacity);

/// <summary>Everything drawn at one moment. The thread, when its opacity is above zero, runs through the dots in order.</summary>
public sealed record WeavingFrame(IReadOnlyList<WeavingDot> Dots, IReadOnlyList<WeavingLink> Links, double ThreadOpacity);

/// <summary>
/// The paths of the weaving indicator's three dots, as a function of time, in a <see cref="Size"/> box. Kept apart from
/// the control so the motion can be checked without drawing.
/// </summary>
public static class WeavingPaths
{
    /// <summary>The box every motion stays inside.</summary>
    public static readonly Size Size = new(44, 20);

    /// <summary>How long dots take to glide from one motion's path into the next.</summary>
    public static readonly TimeSpan GlideDuration = TimeSpan.FromSeconds(0.3);

    /// <summary>One round of links drawn and faded in <see cref="WeavingMotion.Joining"/>.</summary>
    public static readonly TimeSpan JoiningCycle = TimeSpan.FromSeconds(2.4);

    /// <summary>A joining dot between its turns.</summary>
    public const double RestRadius = 2.1;

    private static readonly TimeSpan BraidPeriod = TimeSpan.FromSeconds(1.4);
    private static readonly TimeSpan TighteningPeriod = TimeSpan.FromSeconds(0.9);
    private static readonly TimeSpan ThreadPeriod = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan JoiningTurn = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ReachingPeriod = TimeSpan.FromSeconds(1.4);
    private static readonly TimeSpan HoldingPeriod = TimeSpan.FromSeconds(2.4);
    private static readonly Point Centre = new(22, 10);
    private static readonly double[] ThreadXs = [12, 22, 32];

    /// <summary>After this long the motion looks the same again (for joining: the sizes and links; it also turns slowly).</summary>
    public static TimeSpan Period(WeavingMotion motion) => motion switch
    {
        WeavingMotion.Joining => JoiningCycle,
        WeavingMotion.Tightening => TighteningPeriod,
        WeavingMotion.Thread => ThreadPeriod,
        WeavingMotion.Reaching => ReachingPeriod,
        WeavingMotion.Holding => HoldingPeriod,
        _ => BraidPeriod,
    };

    /// <summary>The frame <paramref name="seconds"/> into a motion.</summary>
    public static WeavingFrame At(WeavingMotion motion, double seconds) => motion switch
    {
        WeavingMotion.Joining => Joining(seconds),
        WeavingMotion.Tightening => Braid(seconds, TighteningPeriod, 0.5),
        WeavingMotion.Thread => Thread(seconds),
        WeavingMotion.Reaching => Reaching(seconds),
        WeavingMotion.Holding => Holding(seconds),
        _ => Braid(seconds, BraidPeriod, 1),
    };

    /// <summary>
    /// Part way (<paramref name="progress"/> from 0 to 1, eased by the caller) from one frame to another: dots move
    /// from where they were, the old links and thread fade out and the new ones fade in.
    /// </summary>
    public static WeavingFrame Blend(WeavingFrame from, WeavingFrame to, double progress)
    {
        var u = Math.Clamp(progress, 0, 1);
        var dots = from.Dots.Zip(to.Dots, (a, b) => new WeavingDot(
            new Point(Lerp(a.Centre.X, b.Centre.X, u), Lerp(a.Centre.Y, b.Centre.Y, u)), Lerp(a.Radius, b.Radius, u), Lerp(a.Opacity, b.Opacity, u))).ToList();
        var links = from.Links.Select(l => l with { Opacity = l.Opacity * (1 - u) })
            .Concat(to.Links.Select(l => l with { Opacity = l.Opacity * u }))
            .Where(l => l.Opacity > 0).ToList();
        return new WeavingFrame(dots, links, Lerp(from.ThreadOpacity, to.ThreadOpacity, u));
    }

    /// <summary>Slow at both ends, for glides and links being drawn.</summary>
    public static double Ease(double u)
    {
        var x = Math.Clamp(u, 0, 1);
        return x * x * (3 - (2 * x));
    }

    /// <summary>
    /// Three dots a third of a loop apart on a sideways figure eight (x follows sin p, y sin 2p). The depth cos p makes
    /// the dot in front larger and more solid, so the strands read as crossing over and under.
    /// </summary>
    private static WeavingFrame Braid(double seconds, TimeSpan period, double scale)
    {
        var turn = Fraction(seconds, period) * 2 * Math.PI;
        var dots = new WeavingDot[3];
        for (var i = 0; i < 3; i++)
        {
            var p = turn + (i * 2 * Math.PI / 3);
            var depth = Math.Cos(p);
            dots[i] = new WeavingDot(
                new Point(Centre.X + (13 * scale * Math.Sin(p)), Centre.Y + (4.5 * scale * Math.Sin(2 * p))),
                2.2 + (0.8 * depth),
                0.55 + (0.45 * (depth + 1) / 2));
        }

        return new WeavingFrame(dots, [], 0);
    }

    /// <summary>
    /// An equilateral triangle turning once every six seconds. Each cycle, link i (from dot i to the next) is drawn from
    /// 0.1 + 0.2i of the way through, over a fifth of the cycle, and dot i grows as its link starts (peaking at
    /// 0.15 + 0.2i); the links fade out over the last fifth. Every dot is at rest where a cycle meets the next.
    /// </summary>
    private static WeavingFrame Joining(double seconds)
    {
        const double radius = 7;
        var rotation = Fraction(seconds, JoiningTurn) * 2 * Math.PI;
        var cycle = Fraction(seconds, JoiningCycle);
        var corners = new Point[3];
        for (var i = 0; i < 3; i++)
        {
            var angle = rotation - (Math.PI / 2) + (i * 2 * Math.PI / 3);
            corners[i] = new Point(Centre.X + (radius * Math.Cos(angle)), Centre.Y + (radius * Math.Sin(angle)));
        }

        var fade = cycle > 0.8 ? 1 - Ease((cycle - 0.8) / 0.2) : 1;
        var links = new List<WeavingLink>();
        var dots = new WeavingDot[3];
        for (var i = 0; i < 3; i++)
        {
            var from = corners[i];
            var to = corners[(i + 1) % 3];
            var drawn = Ease((cycle - (0.1 + (0.2 * i))) / 0.2);
            if (drawn > 0 && fade > 0)
            {
                links.Add(new WeavingLink(from, new Point(Lerp(from.X, to.X, drawn), Lerp(from.Y, to.Y, drawn)), 0.55 * fade));
            }

            var distance = Math.Min(1, Math.Abs(cycle - (0.15 + (0.2 * i))) / 0.13);
            var glow = Math.Pow(Math.Cos(distance * Math.PI / 2), 2);
            dots[i] = new WeavingDot(from, RestRadius + (0.8 * glow), 0.55 + (0.45 * glow));
        }

        return new WeavingFrame(dots, links, 0);
    }

    /// <summary>Three dots on a faint thread; a ripple lifts each in turn, 0.9 radians after the one before.</summary>
    private static WeavingFrame Thread(double seconds)
    {
        var turn = Fraction(seconds, ThreadPeriod) * 2 * Math.PI;
        var dots = new WeavingDot[3];
        for (var i = 0; i < 3; i++)
        {
            var lift = Math.Max(0, Math.Sin(turn - (0.9 * i)));
            dots[i] = new WeavingDot(new Point(ThreadXs[i], Centre.Y - (4 * lift)), 2.4, 0.6 + (0.4 * lift));
        }

        return new WeavingFrame(dots, [], 0.35);
    }

    /// <summary>
    /// Two dots rest on the left; the third reaches from beside them to the far right along a faint link and comes back,
    /// eased at both ends, growing more solid the further out it is.
    /// </summary>
    private static WeavingFrame Reaching(double seconds)
    {
        var cycle = Fraction(seconds, ReachingPeriod);
        var reach = Ease(cycle < 0.5 ? cycle * 2 : (1 - cycle) * 2);
        var anchor = new Point(17, Centre.Y);
        var tip = new Point(Lerp(24, 36, reach), Centre.Y);
        WeavingDot[] dots =
        [
            new(new Point(9, Centre.Y), 2.2, 0.6),
            new(anchor, 2.2, 0.75),
            new(tip, 2.2 + (0.7 * reach), 0.55 + (0.45 * reach)),
        ];
        return new WeavingFrame(dots, [new WeavingLink(anchor, tip, 0.25 + (0.25 * reach))], 0);
    }

    /// <summary>Three dots in a row, not moving; they swell and fade together, slowly.</summary>
    private static WeavingFrame Holding(double seconds)
    {
        var breath = (1 - Math.Cos(Fraction(seconds, HoldingPeriod) * 2 * Math.PI)) / 2;
        var dots = new WeavingDot[3];
        for (var i = 0; i < 3; i++)
        {
            dots[i] = new WeavingDot(new Point(ThreadXs[i], Centre.Y), 2.1 + (0.6 * breath), 0.5 + (0.4 * breath));
        }

        return new WeavingFrame(dots, [], 0);
    }

    private static double Fraction(double seconds, TimeSpan period)
    {
        var f = seconds / period.TotalSeconds % 1;
        return f < 0 ? f + 1 : f;
    }

    private static double Lerp(double a, double b, double u) => a + ((b - a) * u);
}
