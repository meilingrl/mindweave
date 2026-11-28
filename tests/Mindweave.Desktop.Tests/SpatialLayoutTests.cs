using System.Diagnostics;
using System.Globalization;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The spatial view's molecule layout alone (docs/design/session-network.md §6.1): the guards of §6.4 on a fixed set of
/// generated workspaces, and the shapes the owner judged from pictures (straight chains, even angles, no curling,
/// things close in time close in space).
/// </summary>
public partial class SpatialLayoutTests
{
    /// <summary>Twenty fixed workspaces: 1 to 40 roots, trees 0 to 6 levels deep, chains and fans, varied labels.</summary>
    private static readonly IReadOnlyList<List<SpatialNode>> Generated =
        [.. Enumerable.Range(0, 20).Select(s => Workspace(seed: s, roots: 1 + (s * 39 / 19), maxDepth: s % 7))];

    /// <summary>Lone-tree shapes: balanced binary and ternary trees, a comb, a broom and a deep generated tree.</summary>
    private static readonly IReadOnlyList<List<SpatialNode>> Shapes =
        [Full(fan: 2, depth: 6), Full(fan: 3, depth: 4), Comb(spine: 15, legs: 1), Broom(handles: 12, bristles: 2), Workspace(seed: 5, roots: 1, maxDepth: 10)];

    private static readonly IReadOnlyList<List<SpatialNode>> Workspaces = [.. Generated, .. Shapes];

    [Fact]
    public void Nothing_covers_anything()
    {
        foreach (var nodes in Workspaces)
        {
            var report = SpatialLayout.Measure(nodes, SpatialLayout.Arrange(nodes));
            Output($"{nodes.Count} dots, {nodes.Count(n => n.Parent is null)} roots: {report}");

            Assert.Equal(0, report.DotOverlaps);
            Assert.Equal(0, report.LabelsOnDots);
            Assert.Equal(0, report.LabelOverlaps);
        }
    }

    [Fact]
    public void Branch_lines_do_not_cross_or_run_through_dots()
    {
        var reports = Workspaces.Select(nodes => SpatialLayout.Measure(nodes, SpatialLayout.Arrange(nodes))).ToList();

        Output($"crossings {reports.Sum(r => r.BranchCrossings)}, through dots {reports.Sum(r => r.LinesThroughDots)}, through labels {reports.Sum(r => r.LinesThroughLabels)}");

        Assert.All(reports, r => Assert.Equal(0, r.BranchCrossings));
        Assert.All(reports, r => Assert.Equal(0, r.LinesThroughDots));
    }

    [Fact]
    public void A_chain_runs_straight()
    {
        var nodes = Enumerable.Range(1, 6).Select(i => Node(i, i == 1 ? null : i - 1, i, 8 - i, Title(i, 4))).ToList();

        var dots = SpatialLayout.Arrange(nodes).Positions;

        for (var i = 2; i <= 5; i++)
        {
            Assert.InRange(Angle(dots[Id(i - 1)], dots[Id(i)], dots[Id(i + 1)]), 175, 180);
        }
    }

    [Fact]
    public void Three_equal_lines_spread_to_120_degrees()
    {
        // A dot with its parent and two one-dot branches: three lines of equal weight.
        var nodes = new List<SpatialNode> { Node(1, null, 1, 4, "根"), Node(2, 1, 2, 3, "中间"), Node(3, 2, 3, 1, "左"), Node(4, 2, 4, 1, "右") };

        var dots = SpatialLayout.Arrange(nodes).Positions;

        // The parent's side counts the dots behind it (one), like each branch, so the three angles are equal.
        Assert.InRange(Angle(dots[Id(1)], dots[Id(2)], dots[Id(3)]), 112, 128);
        Assert.InRange(Angle(dots[Id(1)], dots[Id(2)], dots[Id(4)]), 112, 128);
        Assert.InRange(Angle(dots[Id(3)], dots[Id(2)], dots[Id(4)]), 112, 128);
    }

    [Fact]
    public void A_bigger_branch_takes_more_room()
    {
        // A dot with a long branch (6 dots) and a single leaf: the long one runs nearer straight on from the parent.
        var nodes = new List<SpatialNode> { Node(1, null, 1, 10, "根"), Node(2, 1, 2, 9, "中间"), Node(3, 2, 3, 1, "叶") };
        nodes.AddRange(Enumerable.Range(4, 6).Select(i => Node(i, i == 4 ? 2 : i - 1, i, 10 - i, Title(i, 3))));

        var dots = SpatialLayout.Arrange(nodes).Positions;

        var big = Angle(dots[Id(1)], dots[Id(2)], dots[Id(4)]);
        var small = Angle(dots[Id(1)], dots[Id(2)], dots[Id(3)]);
        Assert.True(big > small + 15, $"The long branch sits at {big:F0}° from the parent's line, the leaf at {small:F0}°.");
    }

    [Fact]
    public void A_comb_zigzags_instead_of_curling()
    {
        var nodes = Comb(spine: 15, legs: 1);
        var dots = SpatialLayout.Arrange(nodes).Positions;
        var spine = nodes.Where(n => n.Order % 2 == 1).Select(n => dots[n.Id]).ToList();

        // Curling would bring the two ends back together; a gentle zigzag keeps them most of the path apart.
        var path = spine.Zip(spine.Skip(1), Distance).Sum();
        var ends = Distance(spine[0], spine[^1]);
        Assert.True(ends > 0.7 * path, $"The spine's ends are {ends:F0} apart along a path of {path:F0}.");
    }

    [Fact]
    public void Things_close_in_time_are_close_in_space()
    {
        // Forty roots: each step of the time thread is short compared with the whole picture.
        var nodes = Generated[^1];
        var arrangement = SpatialLayout.Arrange(nodes);
        var report = SpatialLayout.Measure(nodes, arrangement);
        var xs = arrangement.Positions.Values.Select(p => p.X).ToList();
        var ys = arrangement.Positions.Values.Select(p => p.Y).ToList();
        var extent = Math.Max(xs.Max() - xs.Min(), ys.Max() - ys.Min());

        Output($"time thread mean {report.ThreadMean:F0}, longest {report.ThreadLongest:F0}, picture {extent:F0}");
        Assert.True(report.ThreadMean < 0.2 * extent, $"The time thread's mean step is {report.ThreadMean:F0} in a picture {extent:F0} across.");
    }

    [Fact]
    public void The_heaviest_exploration_sits_in_the_middle()
    {
        var nodes = Generated[10];
        var arrangement = SpatialLayout.Arrange(nodes);
        var roots = nodes.Where(n => n.Parent is null).ToList();
        var heaviest = roots.MaxBy(n => n.Weight)!;
        var centre = new NodePosition(arrangement.Positions.Values.Average(p => p.X), arrangement.Positions.Values.Average(p => p.Y));

        var rank = roots.OrderBy(r => Distance(arrangement.Positions[r.Id], centre)).ToList().IndexOf(heaviest);
        Assert.True(rank < roots.Count / 3, $"The heaviest root is number {rank + 1} of {roots.Count} from the middle.");
    }

    // ── Explorations keep their places (design §16, option C) ─────────────────────

    [Fact]
    public void With_their_places_explorations_come_back_exactly_where_they_were()
    {
        var nodes = Generated[10];
        var first = SpatialLayout.Arrange(nodes);

        var again = SpatialLayout.Arrange(nodes, places: first.Places);

        Assert.Equal(first.Places.Count, nodes.Count(n => n.Parent is null));
        foreach (var node in nodes)
        {
            Assert.True(Distance(first.Positions[node.Id], again.Positions[node.Id]) < 0.01, $"{node.Id} moved by {Distance(first.Positions[node.Id], again.Positions[node.Id]):F2}.");
        }
    }

    [Fact]
    public void A_new_branch_or_a_new_exploration_moves_no_other_exploration()
    {
        var nodes = Generated[10];
        var before = SpatialLayout.Arrange(nodes);
        var roots = nodes.Where(n => n.Parent is null).ToList();
        var grown = roots[3];
        var next = nodes.Max(n => n.Order) + 1;
        var leaf = Node(9001, null, next, 1, "新的一个分支") with { Parent = grown.Id };
        var fresh = Node(9002, null, next + 1, 1, "一个新的探索");

        foreach (var (changed, added) in new[] { (grown.Id, leaf), (fresh.Id, fresh) })
        {
            var after = SpatialLayout.Arrange([.. nodes, added], places: before.Places);

            foreach (var root in roots.Where(r => r.Id != changed))
            {
                Assert.True(Distance(before.Positions[root.Id], after.Positions[root.Id]) < 0.01, $"Adding {added.Label} moved the exploration of {root.Id}.");
            }

            // One more branch fits where the exploration is: it is drawn again in its place, pointing the same way.
            if (changed == grown.Id)
            {
                Assert.True(Distance(before.Positions[changed], after.Positions[changed]) < 0.01, "The exploration with the new branch moved although it has room.");
            }

            var report = SpatialLayout.Measure([.. nodes, added], after);
            Assert.Equal(0, report.DotOverlaps);
            Assert.Equal(0, report.LabelsOnDots);
            Assert.Equal(0, report.LabelOverlaps);
        }
    }

    [Fact]
    public void An_exploration_that_grows_a_lot_moves_aside_and_nothing_else_moves_or_overlaps()
    {
        var nodes = Generated[10];
        var before = SpatialLayout.Arrange(nodes);
        var roots = nodes.Where(n => n.Parent is null).ToList();
        var small = roots.Where(r => !nodes.Any(n => n.Parent == r.Id)).OrderBy(r => r.Order).First();
        var next = nodes.Max(n => n.Order) + 1;
        List<SpatialNode> grown = [.. nodes, .. Enumerable.Range(0, 12).Select(k => Node(9100 + k, null, next + k, 1, Title(k, 5)) with { Parent = small.Id })];

        var after = SpatialLayout.Arrange(grown, places: before.Places);

        foreach (var root in roots.Where(r => r.Id != small.Id))
        {
            Assert.True(Distance(before.Positions[root.Id], after.Positions[root.Id]) < 0.01, $"The growth moved the exploration of {root.Id}.");
        }

        var report = SpatialLayout.Measure(grown, after);
        Assert.Equal(0, report.DotOverlaps);
        Assert.Equal(0, report.LabelsOnDots);
        Assert.Equal(0, report.LabelOverlaps);
        Assert.Equal(0, report.BranchCrossings);
        Assert.Equal(12 + 1, after.Places[small.Id.Value].Size);
    }

    [Fact]
    public void Growing_one_conversation_at_a_time_never_overlaps_and_leaves_unchanged_explorations_in_place()
    {
        // Review 2026-11-23: a turned exploration once put a title on its own new dot. A workspace grows by one conversation
        // per load; at every step nothing overlaps, and no exploration that did not change moves.
        var full = Workspace(seed: 2, roots: 40, maxDepth: 5, total: 160).OrderBy(n => n.Order).ToList();
        SpatialArrangement? previous = null;
        for (var count = 120; count <= full.Count; count++)
        {
            var nodes = full.Take(count).ToList();
            var arrangement = SpatialLayout.Arrange(nodes, places: previous?.Places);
            var report = SpatialLayout.Measure(nodes, arrangement);
            Assert.True(report.DotOverlaps + report.LabelsOnDots + report.LabelOverlaps == 0, $"At {count} conversations: {report}.");
            if (previous is not null)
            {
                foreach (var (root, place) in previous.Places.Where(p => arrangement.Places.TryGetValue(p.Key, out var now) && now.Size == p.Value.Size))
                {
                    var now = arrangement.Places[root];
                    Assert.True(Math.Abs(now.X - place.X) < 0.01 && Math.Abs(now.Y - place.Y) < 0.01, $"At {count} conversations an unchanged exploration moved.");
                }
            }

            previous = arrangement;
        }
    }

    [Fact]
    public void Same_input_same_output()
    {
        var nodes = Generated[^1];

        var first = SpatialLayout.Arrange(nodes);
        var second = SpatialLayout.Arrange([.. nodes.AsEnumerable().Reverse()]);

        Assert.Equal(nodes.Count, first.Positions.Count);
        foreach (var node in nodes)
        {
            Assert.Equal(first.Positions[node.Id], second.Positions[node.Id]);
            Assert.Equal(first.Labels[node.Id].Side, second.Labels[node.Id].Side);
        }
    }

    [Fact]
    public void Every_reachable_node_gets_a_dot()
    {
        var nodes = new List<SpatialNode>
        {
            Node(1, null, 0, 3, "一"),
            Node(2, 99, 1, 1, "二"), // parent unknown: placed as a root
            Node(3, 4, 2, 1, "三"), // a parent cycle the data cannot hold: left out
            Node(4, 3, 3, 1, "四"),
        };

        var arrangement = SpatialLayout.Arrange(nodes);

        Assert.Equal([Id(1), Id(2)], arrangement.Positions.Keys.OrderBy(k => k.Value).ToArray());
        Assert.Equal([Id(1), Id(2)], arrangement.Labels.Keys.OrderBy(k => k.Value).ToArray());
        Assert.Empty(SpatialLayout.Arrange([]).Positions);
    }

    [Fact]
    public void Titles_wrap_to_a_narrow_column_and_stay_whole()
    {
        Assert.Equal(["采集技术"], SpatialLayout.Wrap("采集技术"));

        var long_ = "数据采集技术及CDC与消息队列的通俗讲解";
        var lines = SpatialLayout.Wrap(long_);
        Assert.True(lines.Count >= 2);
        Assert.Equal(long_, string.Concat(lines));
        Assert.All(lines, line => Assert.True(SummaryNetworkLayout.LabelWidth(line) <= SpatialLayout.MaxLineWidth));

        // English breaks only at spaces, and a short last word is never glued to a piece of the one before (review 2026-11-22).
        foreach (var title in new[] { "Why does stochastic gradient descent generalize", "Why does the steam engine matter so much ok" })
        {
            var english = SpatialLayout.Wrap(title);
            Assert.All(english, line => Assert.DoesNotContain("  ", line, StringComparison.Ordinal));
            Assert.Equal(title, string.Join(' ', english));
        }

        // A lonely last wide character takes a few from the line before; a character outside the basic plane stays whole.
        var cjk = SpatialLayout.Wrap("数据采集技术演进与层次关系的通俗讲解啊");
        Assert.True(cjk[^1].Length > 1);
        var emoji = SpatialLayout.Wrap("数据采集技术演进与层次关系🦀的讲解");
        Assert.Equal("数据采集技术演进与层次关系🦀的讲解", string.Concat(emoji));
        Assert.All(emoji, line => Assert.False(char.IsHighSurrogate(line[^1]), $"'{line}' ends inside a character."));
    }

    [Fact]
    public void A_drawn_label_smaller_than_reserved_stays_inside_the_reserved_block()
    {
        // The canvas draws text no larger than the layout reserved; anchored the same way, it never leaves the block.
        for (var place = 0; place < SpatialLayout.Sides.Count; place++)
        {
            var (l, t) = SpatialLayout.LabelOffset(place, 10, 120, 42);
            foreach (var (w, h) in new[] { (60.0, 21.0), (119.0, 41.0), (1.0, 1.0) })
            {
                var (dl, dt) = SpatialLayout.LabelOffset(place, 10, w, h);
                Assert.True(dl >= l - 1e-9 && dt >= t - 1e-9 && dl + w <= l + 120 + 1e-9 && dt + h <= t + 42 + 1e-9, $"Side {place}: a {w}×{h} block leaves the reserved one.");
            }
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Thousand_dots_within_300_ms()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MINDWEAVE_PERF") == "1", "Run through scripts/perf.sh.");
        var shapes = new[]
        {
            ("220 roots", Workspace(seed: 1000, roots: 220, maxDepth: 10, total: 1_000)),
            ("one binary tree", Full(fan: 2, depth: 9)),
            ("1,000 sessions without branches", Workspace(seed: 3, roots: 1_000, maxDepth: 0)),
            ("one session with 1,000 branches", Broom(handles: 999, bristles: 0)),
        };
        foreach (var (name, nodes) in shapes)
        {
            SpatialLayout.Arrange(nodes); // warm-up
            var times = new List<TimeSpan>();
            SpatialArrangement? arrangement = null;
            for (var k = 0; k < 3; k++)
            {
                var watch = Stopwatch.StartNew();
                arrangement = SpatialLayout.Arrange(nodes);
                times.Add(watch.Elapsed);
            }

            var elapsed = times.Order().ElementAt(1);
            var report = SpatialLayout.Measure(nodes, arrangement!);
            Output($"spatial layout of {nodes.Count:N0} dots ({name}), median of three: {elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms; {report}");
            Assert.Equal(0, report.DotOverlaps);
            var budget = PerfBudget.Ms(300);
            Assert.True(elapsed < budget, $"Laying out {nodes.Count:N0} dots ({name}) took {elapsed.TotalMilliseconds:F1} ms (budget {budget.TotalMilliseconds} ms).");
        }
    }

    /// <summary>The angle at <paramref name="b"/> between the lines to <paramref name="a"/> and <paramref name="c"/>, in degrees.</summary>
    private static double Angle(NodePosition a, NodePosition b, NodePosition c)
    {
        var first = Math.Atan2(a.Y - b.Y, a.X - b.X);
        var second = Math.Atan2(c.Y - b.Y, c.X - b.X);
        var degrees = Math.Abs(first - second) * 180 / Math.PI;
        return degrees > 180 ? 360 - degrees : degrees;
    }

    private static double Distance(NodePosition a, NodePosition b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static void Output(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
}
