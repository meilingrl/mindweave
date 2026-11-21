using System.Diagnostics;
using System.Globalization;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;

namespace Mindweave.Desktop.Tests;

/// <summary>The world-line timeline layout alone: roots on the main line, branches in lanes, deterministic.</summary>
public class TimelineLayoutTests
{
    private static readonly DateOnly Day1 = new(2026, 11, 19);
    private static readonly DateOnly Day2 = new(2026, 11, 20);

    [Fact]
    public void Roots_sit_on_the_main_line_in_order_one_step_apart()
    {
        var nodes = new List<TimelineNode> { Root(1, Day1, 0), Root(2, Day1, 1), Root(3, Day1, 2) };

        var positions = TimelineLayout.Arrange(nodes).Positions;

        Assert.Equal(new NodePosition(0, 0), positions[Id(1)]);
        Assert.Equal(new NodePosition(110, 0), positions[Id(2)]);
        Assert.Equal(new NodePosition(220, 0), positions[Id(3)]);
    }

    [Fact]
    public void A_new_day_opens_a_gap_and_starts_a_day_label()
    {
        var nodes = new List<TimelineNode> { Root(1, Day1, 0), Root(2, Day1, 1), Root(3, Day2, 2) };

        var arrangement = TimelineLayout.Arrange(nodes);

        Assert.Equal([0.0, 110.0, 330.0], [arrangement.Positions[Id(1)].X, arrangement.Positions[Id(2)].X, arrangement.Positions[Id(3)].X]);
        Assert.Equal([Id(1), Id(3)], arrangement.DayStarts);
    }

    [Fact]
    public void Midnight_splits_days()
    {
        // The caller turns created_at into the local day; one minute either side of midnight gives consecutive days.
        var nodes = new List<TimelineNode> { Root(1, Day1, 0), Root(2, Day1.AddDays(1), 1) };

        var arrangement = TimelineLayout.Arrange(nodes);

        Assert.Equal(TimelineLayout.Step + TimelineLayout.DayGap, arrangement.Positions[Id(2)].X - arrangement.Positions[Id(1)].X);
        Assert.Equal([Id(1), Id(2)], arrangement.DayStarts);
    }

    [Fact]
    public void A_roots_children_leave_the_main_line()
    {
        var nodes = new List<TimelineNode> { Root(1, Day1, 0), Child(2, 1, 1), Child(3, 1, 2), Child(4, 2, 3), Child(5, 4, 4), Child(6, 2, 5), Root(7, Day1, 6) };

        var positions = TimelineLayout.Arrange(nodes).Positions;

        Assert.All(nodes.Where(n => n.Parent is not null), n => Assert.NotEqual(0, positions[n.Id].Y));
        Assert.Equal(new NodePosition(110, -72), positions[Id(2)]);
        AssertRules(nodes, positions);
    }

    [Fact]
    public void A_branchs_first_child_continues_its_lane_and_the_next_opens_another()
    {
        var nodes = new List<TimelineNode> { Root(1, Day1, 0), Child(2, 1, 1), Child(3, 2, 2), Child(4, 2, 3) };

        var positions = TimelineLayout.Arrange(nodes).Positions;

        Assert.Equal(new NodePosition(positions[Id(2)].X + 110, positions[Id(2)].Y), positions[Id(3)]);
        Assert.NotEqual(positions[Id(2)].Y, positions[Id(4)].Y);

        // The second child rises again from the branch above the main line, never across it (ruling R9).
        Assert.Equal(new NodePosition(220, -144), positions[Id(4)]);
        AssertRules(nodes, positions);
    }

    [Fact]
    public void A_lane_runs_past_later_roots_without_moving_them()
    {
        var nodes = new List<TimelineNode> { Root(1, Day1, 0), Child(2, 1, 1), Child(3, 2, 2), Child(4, 3, 3), Child(5, 4, 4), Child(6, 5, 5), Root(7, Day1, 6) };

        var positions = TimelineLayout.Arrange(nodes).Positions;

        Assert.Equal(new NodePosition(110, 0), positions[Id(7)]);
        Assert.Equal(550, positions[Id(6)].X);
        AssertRules(nodes, positions);
    }

    [Fact]
    public void A_roots_third_child_fans_out_beside_its_siblings()
    {
        // The siblings sit at the same x; the third child's connector reaches its own lane at that x, so they do not block it.
        var nodes = new List<TimelineNode> { Root(1, Day1, 0), Child(2, 1, 1), Child(3, 1, 2), Child(4, 1, 3), Child(5, 2, 4), Child(6, 3, 5), Child(7, 4, 6) };

        var positions = TimelineLayout.Arrange(nodes).Positions;

        Assert.Equal(new NodePosition(110, -72), positions[Id(2)]);
        Assert.Equal(new NodePosition(110, 72), positions[Id(3)]);
        Assert.Equal(new NodePosition(110, -144), positions[Id(4)]);
        AssertRules(nodes, positions);
    }

    [Fact]
    public void A_root_after_a_root_with_two_long_chains_branches_across_a_chain()
    {
        // Root 1's chains fill the lanes either side of the main line; root 2's branch crosses one at a dot boundary.
        var nodes = new List<TimelineNode> { Root(1, Day1, 0), Root(2, Day1, 1) };
        nodes.AddRange(Chain(10, parent: 1, length: 5, firstOrder: 2));
        nodes.AddRange(Chain(20, parent: 1, length: 5, firstOrder: 7));
        nodes.Add(Child(30, 2, 12));

        var positions = TimelineLayout.Arrange(nodes).Positions;

        Assert.Equal(-72, positions[Id(10)].Y);
        Assert.Equal(72, positions[Id(20)].Y);
        Assert.Equal(new NodePosition(220, -144), positions[Id(30)]);
        AssertRules(nodes, positions);
    }

    [Fact]
    public void A_new_day_root_between_two_long_chains_branches_across_a_chain()
    {
        // Root 2 starts a new day. The day gap is a whole step, so its branch's x stays on the grid of root 1's chains
        // and the branch crosses lane −1 between two of its dots, one step from root 2 (ruling R7).
        var nodes = new List<TimelineNode> { Root(1, Day1, 0), Root(2, Day2, 1) };
        nodes.AddRange(Chain(10, parent: 1, length: 4, firstOrder: 2));
        nodes.AddRange(Chain(20, parent: 1, length: 4, firstOrder: 6));
        nodes.Add(Child(30, 2, 10));

        var positions = TimelineLayout.Arrange(nodes).Positions;

        Assert.Equal(new NodePosition(220, 0), positions[Id(2)]);
        Assert.Equal(new NodePosition(330, -144), positions[Id(30)]);
        AssertRules(nodes, positions);
    }

    [Fact]
    public void Dots_never_overlap_and_connectors_never_pass_through_a_dot()
    {
        // Twenty fixed seeds of chains, fans, late branches and new days.
        for (var seed = 1; seed <= 20; seed++)
        {
            var nodes = RandomForest(300, seed);

            var positions = TimelineLayout.Arrange(nodes).Positions;

            AssertRules(nodes, positions);
        }
    }

    [Fact]
    public void Same_input_same_output()
    {
        var nodes = RandomForest(120, seed: 7);
        var first = TimelineLayout.Arrange(nodes);
        var second = TimelineLayout.Arrange(nodes);
        var shuffled = TimelineLayout.Arrange([.. nodes.AsEnumerable().Reverse()]);

        Assert.Equal(first.Positions, second.Positions);
        Assert.Equal(first.DayStarts, second.DayStarts);
        Assert.Equal(first.Positions, shuffled.Positions);
        Assert.Equal(first.DayStarts, shuffled.DayStarts);

        // Two roots with the same rank (equal timestamps) are ordered by id, whatever the input order.
        var a = new TimelineNode(Id(1), null, Day1, 0);
        var b = new TimelineNode(Id(2), null, Day1, 0);
        Assert.Equal(TimelineLayout.Arrange([a, b]).Positions, TimelineLayout.Arrange([b, a]).Positions);
        Assert.Equal(new NodePosition(110, 0), TimelineLayout.Arrange([b, a]).Positions[Id(2)]);
    }

    [Fact]
    public void A_deep_chain_stays_in_one_lane()
    {
        var nodes = new List<TimelineNode> { Root(1, Day1, 0) };
        nodes.AddRange(Chain(1000, parent: 1, length: 100_000, firstOrder: 1)); // far deeper than a call stack allows

        var positions = TimelineLayout.Arrange(nodes).Positions;

        Assert.Single(nodes.Skip(1).Select(n => positions[n.Id].Y).Distinct());
        Assert.Equal(100_000 * 110, positions[Id(1000 + 99_999)].X);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Thousand_dots_within_100_ms()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MINDWEAVE_PERF") == "1", "Run through scripts/perf.sh.");
        var nodes = RandomForest(1_000, seed: 1000);
        TimelineLayout.Arrange(nodes); // warm-up

        var watch = Stopwatch.StartNew();
        var positions = TimelineLayout.Arrange(nodes).Positions;
        var elapsed = watch.Elapsed;

        Assert.Equal(nodes.Count, positions.Count);
        TestContext.Current.TestOutputHelper?.WriteLine($"timeline of 1,000 dots: {elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms");
        Assert.True(elapsed < TimeSpan.FromMilliseconds(100), $"Arranging 1,000 dots took {elapsed.TotalMilliseconds:F1} ms (budget 100 ms).");
    }

    /// <summary>
    /// Every node placed; no two dots on one Y closer than a step; no sub-branch connector spans the main line; no
    /// connector passes a dot: for each branch, no dot
    /// at an X strictly between the parent's and the child's with a Y strictly between theirs, nor in the parent's or
    /// the child's lane (so every connector spans exactly one step).
    /// </summary>
    private static void AssertRules(List<TimelineNode> nodes, IReadOnlyDictionary<SummaryNodeId, NodePosition> positions)
    {
        Assert.Equal(nodes.Count, positions.Count);
        foreach (var row in positions.Values.GroupBy(p => p.Y))
        {
            var xs = row.Select(p => p.X).Order().ToArray();
            for (var i = 1; i < xs.Length; i++)
            {
                Assert.True(xs[i] - xs[i - 1] >= TimelineLayout.Step, $"Dots at ({xs[i - 1]}, {row.Key}) and ({xs[i]}, {row.Key}) overlap.");
            }
        }

        var dots = positions.ToArray();
        foreach (var node in nodes.Where(n => n.Parent is not null))
        {
            var parent = positions[node.Parent!.Value];
            var child = positions[node.Id];
            Assert.True(child.X > parent.X, $"{node.Id} is not right of its parent.");
            // A sub-branch stays on its parent's side of the main line (ruling R9): only a root's connectors leave y = 0.
            if (parent.Y != 0)
            {
                Assert.True(Math.Sign(parent.Y) == Math.Sign(child.Y), $"The connector {parent} → {child} crosses the main line.");
            }

            var low = Math.Min(parent.Y, child.Y);
            var high = Math.Max(parent.Y, child.Y);
            foreach (var (id, dot) in dots)
            {
                Assert.False(
                    dot.X > parent.X && dot.X < child.X && dot.Y >= low && dot.Y <= high,
                    $"The connector {parent} → {child} passes the dot of {id} at {dot}.");
            }
        }
    }

    /// <summary>Roots and branches in creation order: chains, fans and new days, from a fixed seed.</summary>
    private static List<TimelineNode> RandomForest(int count, int seed)
    {
        var random = new Random(seed);
        var nodes = new List<TimelineNode>();
        var day = Day1;
        for (var i = 0; i < count; i++)
        {
            if (nodes.Count == 0 || random.NextDouble() < 0.25)
            {
                if (random.NextDouble() < 0.3)
                {
                    day = day.AddDays(1);
                }

                nodes.Add(new TimelineNode(Id(i + 1), null, day, i));
                continue;
            }

            // Mostly a recent node (long chains), sometimes any earlier one (fans, late branches).
            var parent = random.NextDouble() < 0.7 ? nodes[Math.Max(0, nodes.Count - 1 - random.Next(6))] : nodes[random.Next(nodes.Count)];
            nodes.Add(new TimelineNode(Id(i + 1), parent.Id, day, i));
        }

        return nodes;
    }

    private static IEnumerable<TimelineNode> Chain(int firstId, int parent, int length, int firstOrder)
    {
        for (var i = 0; i < length; i++)
        {
            yield return Child(firstId + i, i == 0 ? parent : firstId + i - 1, firstOrder + i);
        }
    }

    private static TimelineNode Root(int id, DateOnly day, int order) => new(Id(id), null, day, order);

    private static TimelineNode Child(int id, int parent, int order) => new(Id(id), Id(parent), Day1, order);

    private static SummaryNodeId Id(int n) => new(new Guid(n, 0, 0, new byte[8]));
}
