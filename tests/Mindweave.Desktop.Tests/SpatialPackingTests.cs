using System.Diagnostics;
using System.Globalization;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The spatial view's packing alone (docs/design/session-network.md §6.1), checked with the §6.4 metrics on a fixed
/// set of generated workspaces.
/// </summary>
public class SpatialPackingTests
{
    /// <summary>Twenty fixed workspaces: 1 to 40 roots, trees 0 to 6 levels deep, chains and fans, varied labels.</summary>
    private static readonly IReadOnlyList<List<PackNode>> Workspaces =
        [.. Enumerable.Range(0, 20).Select(s => Workspace(seed: s, roots: 1 + (s * 39 / 19), maxDepth: s % 7))];

    [Fact]
    public void No_footprints_overlap()
    {
        foreach (var nodes in Workspaces)
        {
            var report = SpatialPacking.Measure(nodes, SpatialPacking.Arrange(nodes));

            Assert.Equal(0, report.Overlaps);
        }
    }

    [Fact]
    public void The_enclosing_circle_is_near_ideal()
    {
        var worst = Workspaces
            .Select(nodes => SpatialPacking.Measure(nodes, SpatialPacking.Arrange(nodes)))
            .Select(r => r.EnclosingRadius / r.IdealRadius)
            .Max();

        Output($"worst enclosing / ideal radius: {worst:F3}");
        Assert.True(worst <= 1.6, $"The enclosing circle is {worst:F3} × the ideal radius (at most 1.6).");
    }

    [Fact]
    public void The_centre_of_gravity_sits_near_the_middle()
    {
        // A lone dot cannot move against its own label: its offset is fixed by the footprint (about 0.8), so the
        // one-node workspace is left out.
        var worst = Workspaces
            .Where(nodes => nodes.Count > 1)
            .Select(nodes => SpatialPacking.Measure(nodes, SpatialPacking.Arrange(nodes)))
            .Max(r => r.CentroidOffset);

        Output($"worst centroid offset: {worst:F3} of the radius");
        Assert.True(worst <= 0.15, $"The centre of gravity is {worst:F3} of the radius from the circle's centre (at most 0.15).");
    }

    [Fact]
    public void It_is_not_a_timeline()
    {
        // Eight roots in creation order, with masses that do not follow time.
        double[] masses = [3, 40, 7, 25, 1, 60, 12, 5];
        var nodes = masses.Select((m, i) => Node(i + 1, null, i, m, label: 80, next: i + 1 < masses.Length ? i + 2 : null)).ToList();

        var dots = SpatialPacking.Arrange(nodes);
        var report = SpatialPacking.Measure(nodes, dots);

        var xs = nodes.Select(n => dots[n.Id].X).ToList();
        Assert.False(IsMonotone(xs), $"Root x follows time: {string.Join(", ", xs.Select(x => x.ToString("F0", CultureInfo.InvariantCulture)))}.");
        Assert.True(report.RootTimeRankCorrelation < 0.6, $"Rank correlation of time and x is {report.RootTimeRankCorrelation:F2} (below 0.6).");

        // On every generated workspace with at least five roots, root x never follows time. With a handful of
        // roots a high rank correlation can happen by chance, so the threshold is held by the average.
        var correlations = new List<double>();
        foreach (var workspace in Workspaces.Where(w => w.Count(n => n.Parent is null) >= 5))
        {
            var placed = SpatialPacking.Arrange(workspace);
            var rootXs = workspace.Where(n => n.Parent is null).OrderBy(n => n.Order).Select(n => placed[n.Id].X).ToList();
            Assert.False(IsMonotone(rootXs), "Root x follows time.");
            correlations.Add(SpatialPacking.Measure(workspace, placed).RootTimeRankCorrelation);
        }

        Output($"rank correlation of root time and x: average {correlations.Average():F2}, worst {correlations.Max():F2}");
        Assert.True(correlations.Average() < 0.3, $"Rank correlation of time and x averages {correlations.Average():F2} (below 0.3).");
    }

    [Fact]
    public void A_wide_fan_packs_round()
    {
        var nodes = new List<PackNode> { Node(1, null, 0, 41, label: 96) };
        nodes.AddRange(Enumerable.Range(0, 40).Select(i => Node(i + 2, 1, i + 1, 1, label: 48 + (16 * (i % 5)))));

        var dots = SpatialPacking.Arrange(nodes);
        var report = SpatialPacking.Measure(nodes, dots);

        Assert.Equal(0, report.Overlaps);
        var boxes = nodes.Select(n => Box(n, dots[n.Id])).ToList();
        var width = boxes.Max(b => b.Right) - boxes.Min(b => b.Left);
        var height = boxes.Max(b => b.Bottom) - boxes.Min(b => b.Top);
        var aspect = Math.Max(width, height) / Math.Min(width, height);
        Output($"wide fan: aspect {aspect:F3}, enclosing / ideal {report.EnclosingRadius / report.IdealRadius:F3}");
        Assert.True(aspect <= 1.3, $"A fan of 40 children is {aspect:F2} times as long as it is wide (at most 1.3).");
    }

    [Fact]
    public void Children_wind_clockwise_in_creation_order()
    {
        var nodes = new List<PackNode> { Node(1, null, 0, 7, label: 96) };
        nodes.AddRange(Enumerable.Range(0, 6).Select(i => Node(i + 2, 1, i + 1, 1, label: 64)));

        var dots = SpatialPacking.Arrange(nodes);

        // Angles around the root's footprint, measured clockwise on screen (y grows downwards) from the first child.
        var centre = FootprintCentre(nodes[0], dots[nodes[0].Id]);
        var angles = nodes.Skip(1)
            .Select(n => FootprintCentre(n, dots[n.Id]))
            .Select(p => Math.Atan2(p.Y - centre.Y, p.X - centre.X))
            .ToList();
        var turned = angles.Select(a => ((a - angles[0]) % (2 * Math.PI) + (2 * Math.PI)) % (2 * Math.PI)).ToList();
        for (var i = 1; i < turned.Count; i++)
        {
            Assert.True(turned[i] > turned[i - 1], $"Child {i + 1} is not clockwise of child {i} ({turned[i - 1]:F2} → {turned[i]:F2} rad).");
        }
    }

    [Fact]
    public void Same_input_same_output()
    {
        var nodes = Workspaces[^1];

        var first = SpatialPacking.Arrange(nodes);
        var second = SpatialPacking.Arrange([.. nodes.AsEnumerable().Reverse()]);

        Assert.Equal(nodes.Count, first.Count);
        foreach (var node in nodes)
        {
            Assert.Equal(first[node.Id], second[node.Id]);
        }
    }

    [Fact]
    public void Labels_never_cover_dots_or_labels()
    {
        // Footprints never overlap (No_footprints_overlap), so it is enough that each footprint holds its dot and its
        // label wherever the canvas may draw it: level with the dot, lifted above it or dropped below it.
        foreach (var node in Workspaces.SelectMany(w => w))
        {
            var dot = new NodePosition(100, 50);
            var (centre, radius) = SpatialPacking.FootprintOf(node, dot);
            var left = dot.X + node.DotRadius + SummaryNetworkLayout.LabelGap;
            var right = left + node.LabelWidth;
            foreach (var (top, bottom) in new[]
            {
                (dot.Y - 9, dot.Y - 9 + SpatialPacking.LabelHeight),
                (dot.Y - SpatialPacking.LabelHeight - 3, dot.Y - 3),
                (dot.Y + 3, dot.Y + 3 + SpatialPacking.LabelHeight),
            })
            {
                foreach (var corner in new[] { (left, top), (right, top), (left, bottom), (right, bottom) })
                {
                    Assert.True(Distance(centre, corner) <= radius, $"A corner of {node.Id}'s label lies outside its footprint.");
                }
            }

            Assert.True(Distance(centre, (dot.X, dot.Y)) + node.DotRadius + 2 <= radius, $"{node.Id}'s dot pokes out of its footprint.");
        }
    }

    [Fact]
    public void Every_reachable_node_gets_a_dot()
    {
        var nodes = new List<PackNode>
        {
            Node(1, null, 0, 3, label: 64),
            Node(2, 99, 1, 1, label: 64), // parent unknown: placed as a root
            Node(3, 4, 2, 1, label: 64), // a parent cycle the data cannot hold: left out
            Node(4, 3, 3, 1, label: 64),
        };

        var dots = SpatialPacking.Arrange(nodes);

        Assert.Equal([Id(1), Id(2)], dots.Keys.OrderBy(k => k.Value).ToArray());
        Assert.Empty(SpatialPacking.Arrange([]));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Thousand_dots_within_300_ms()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MINDWEAVE_PERF") == "1", "Run through scripts/perf.sh.");
        var nodes = Workspace(seed: 1000, roots: 220, maxDepth: 10, total: 1_000);
        SpatialPacking.Arrange(nodes); // warm-up

        var watch = Stopwatch.StartNew();
        var dots = SpatialPacking.Arrange(nodes);
        var elapsed = watch.Elapsed;

        Assert.Equal(nodes.Count, dots.Count);
        var report = SpatialPacking.Measure(nodes, dots);
        Output($"spatial packing of {nodes.Count:N0} dots: {elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms; {report}");
        Assert.Equal(0, report.Overlaps);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(300), $"Packing 1,000 dots took {elapsed.TotalMilliseconds:F1} ms (budget 300 ms).");
    }

    /// <summary>
    /// A generated workspace: <paramref name="roots"/> trees, at most <paramref name="maxDepth"/> levels below the
    /// root, with chains and fans; the first root reaches the full depth through a chain. Creation order interleaves
    /// roots and branches; a node's mass is its own questions plus all its branches' (the weight w), and its dot
    /// grows with it as on the canvas. With <paramref name="total"/>, trees keep growing until that many nodes exist.
    /// </summary>
    private static List<PackNode> Workspace(int seed, int roots, int maxDepth, int total = 0)
    {
        var random = new Random(seed);
        var parent = new List<int>();
        var depth = new List<int>();
        var time = new List<double>();
        var open = new List<int>();
        for (var r = 0; r < roots; r++)
        {
            Add(-1, random.NextDouble() * 1000 * roots);
        }

        // The first root reaches the full depth.
        var tip = 0;
        for (var d = 0; d < maxDepth; d++)
        {
            tip = Add(tip, 0);
        }

        // Fans and chains: each open node gets 0–5 children, most often none or one.
        for (var i = 0; i < parent.Count && (total == 0 || parent.Count < total); i++)
        {
            if (depth[i] >= maxDepth)
            {
                continue;
            }

            var roll = random.NextDouble();
            var count = roll < 0.45 ? 0 : roll < 0.75 ? 1 : roll < 0.88 ? 2 : roll < 0.95 ? 3 : 4 + random.Next(2);
            for (var c = 0; c < count && (total == 0 || parent.Count < total); c++)
            {
                Add(i, 0);
            }

            if (total > 0 && i == parent.Count - 1 && parent.Count < total)
            {
                Add(-1, random.NextDouble() * 1000 * roots); // keep growing until the budgeted size
            }
        }

        var n = parent.Count;
        var own = Enumerable.Range(0, n).Select(_ => 1.0 + random.Next(12)).ToArray();
        var mass = (double[])own.Clone();
        for (var i = n - 1; i >= 0; i--)
        {
            if (parent[i] >= 0)
            {
                mass[parent[i]] += mass[i];
            }
        }

        var order = Enumerable.Range(0, n).OrderBy(i => time[i]).ThenBy(i => i).ToArray();
        var rank = new int[n];
        for (var k = 0; k < n; k++)
        {
            rank[order[k]] = k;
        }

        var rootsInTime = order.Where(i => parent[i] < 0).ToList();
        var nodes = new List<PackNode>(n);
        for (var i = 0; i < n; i++)
        {
            var chars = 2 + random.Next(11);
            var label = Enumerable.Range(0, chars).Sum(_ => random.NextDouble() < 0.3 ? SummaryNetworkLayout.AsciiWidth : SummaryNetworkLayout.LabelEm);
            var ring = random.NextDouble() < 0.3 ? 4.5 : 0; // the summary ring around the dot
            var radius = Math.Clamp(5 + (1.3765 * Math.Sqrt(mass[i] - 1)), 5, 14) + ring;
            var at = rootsInTime.IndexOf(i);
            var next = at >= 0 && at + 1 < rootsInTime.Count ? Id(rootsInTime[at + 1] + 1) : (SummaryNodeId?)null;
            nodes.Add(new PackNode(Id(i + 1), parent[i] < 0 ? null : Id(parent[i] + 1), rank[i], mass[i], radius, label, next));
        }

        return nodes;

        int Add(int of, double at)
        {
            parent.Add(of);
            depth.Add(of < 0 ? 0 : depth[of] + 1);
            time.Add(of < 0 ? at : time[of] + 1 + (random.NextDouble() * 400));
            return parent.Count - 1;
        }
    }

    private static PackNode Node(int id, int? parent, int order, double mass, double label, int? next = null) =>
        new(Id(id), parent is { } p ? Id(p) : null, order, mass, Math.Clamp(5 + (1.3765 * Math.Sqrt(mass - 1)), 5, 14), label, next is { } n ? Id(n) : null);

    private static (double Left, double Top, double Right, double Bottom) Box(PackNode node, NodePosition dot)
    {
        var (centre, radius) = SpatialPacking.FootprintOf(node, dot);
        return (centre.X - radius, centre.Y - radius, centre.X + radius, centre.Y + radius);
    }

    private static NodePosition FootprintCentre(PackNode node, NodePosition dot) => SpatialPacking.FootprintOf(node, dot).Centre;

    private static double Distance(NodePosition a, (double X, double Y) b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static bool IsMonotone(List<double> xs) =>
        xs.Zip(xs.Skip(1)).All(p => p.Second >= p.First) || xs.Zip(xs.Skip(1)).All(p => p.Second <= p.First);

    private static void Output(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static SummaryNodeId Id(int n) => new(new Guid(n, 0, 0, new byte[8]));
}
