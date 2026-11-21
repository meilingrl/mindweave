using System.Diagnostics;
using System.Globalization;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Lone trees of any shape and size (Ruling R-P6) and the choice between arrangements (Ruling R-P7): every lone tree
/// up to 2,000 dots stays near round and overlap-free, and crossings are avoided before fan order or stretch.
/// </summary>
public partial class SpatialPackingTests
{
    /// <summary>Lone trees of the shapes that strain one way of packing or another, each up to 2,000 dots.</summary>
    public static TheoryData<string> LoneShapes =>
    [
        "comb 160×3", "comb 150×3", "comb 60×2", "comb 500×3", "chain 600", "chain 2000", "star 200", "star 600",
        "star 1000", "broom 4×352", "binary 1023", "ternary 1093", "fan 6^4",
    ];

    [Theory]
    [MemberData(nameof(LoneShapes))]
    public void Lone_trees_of_any_shape_stay_round(string shape)
    {
        var nodes = LoneShape(shape);

        var dots = SpatialPacking.Arrange(nodes);
        var report = SpatialPacking.Measure(nodes, dots);
        var ratio = report.EnclosingRadius / report.IdealRadius;

        Output($"{shape} ({nodes.Count} dots): enclosing / ideal {ratio:F3}, centroid {report.CentroidOffset:F3}, crossings {report.BranchCrossings} over {report.BranchEdges} lines");
        Assert.Equal(nodes.Count, dots.Count);
        Assert.Equal(0, report.Overlaps);
        Assert.True(ratio <= 2.5, $"A lone {shape} spreads to {ratio:F2} × its ideal radius (at most 2.5).");
    }

    [Fact]
    public void Bushy_lone_trees_rarely_cross()
    {
        // Crossings are the visible tangle; a lone tree's are held to 0.05 per line (Rulings R-P3, R-P7).
        foreach (var nodes in Bushy)
        {
            var report = SpatialPacking.Measure(nodes, SpatialPacking.Arrange(nodes));
            var perEdge = (double)report.BranchCrossings / report.BranchEdges;

            Output($"{nodes.Count}-node balanced tree: {report.BranchCrossings} crossings over {report.BranchEdges} lines ({perEdge:F3} per line), enclosing / ideal {report.EnclosingRadius / report.IdealRadius:F3}");
            Assert.True(perEdge <= 0.05, $"A {nodes.Count}-node balanced tree's lines cross {perEdge:F3} times per line (at most 0.05).");
        }
    }

    [Fact]
    public void A_lone_tree_takes_an_uncrossed_arrangement_before_fan_order_or_stretch()
    {
        // Ruling R-P7: crossings are the visible tangle; fan order and a stretched sub-branch are subtler.
        var crossed = new SpatialPacking.Checks(Ratio: 1.3, CentroidOffset: 0.02, Overlaps: 0, Edges: 100, Crossings: 10, Stretch: 1.5, InOrder: true);
        var uncrossed = crossed with { Ratio = 1.5, Crossings = 2, Stretch = 3.0, InOrder = false };

        Assert.Equal(1, SpatialPacking.Pick([crossed, uncrossed], lone: true));

        // Before crossings: no overlap, and no spread beyond 2.5 × the ideal radius (Ruling R-P6).
        Assert.Equal(0, SpatialPacking.Pick([crossed, uncrossed with { Overlaps = 1 }], lone: true));
        Assert.Equal(0, SpatialPacking.Pick([crossed, uncrossed with { Ratio = 2.6 }], lone: true));

        // Among the uncrossed, fan order still comes first.
        Assert.Equal(1, SpatialPacking.Pick([uncrossed, uncrossed with { InOrder = true, Ratio = 1.59 }], lone: true));
    }

    [Fact]
    public void The_choice_measures_as_the_gates_do()
    {
        // The arrangement is chosen by the same checks the tests hold it to: what the choice saw is what Measure reports.
        var negative = DeepTree.Select((n, i) => i % 3 == 0 ? n with { Mass = -5 } : n).ToList();
        foreach (var nodes in new[] { DeepTree, negative, Bushy[1], Generated[^1], Comb(spine: 60, legs: 2) })
        {
            var (dots, trace) = SpatialPacking.Trace(nodes);
            var report = SpatialPacking.Measure(nodes, dots);
            var seen = trace.KeptChecks;

            Assert.Equal(report.EnclosingRadius / report.IdealRadius, seen.Ratio, 9);
            Assert.Equal(report.CentroidOffset, seen.CentroidOffset, 9);
            Assert.Equal(report.Overlaps, seen.Overlaps);
            Assert.Equal(report.BranchEdges, seen.Edges);
            Assert.Equal(report.BranchCrossings, seen.Crossings);
        }
    }

    [Fact]
    public void A_single_dot_tries_no_arrangement()
    {
        var (dots, trace) = SpatialPacking.Trace([Node(1, null, 0, 3, label: 64)]);

        Assert.Single(dots);
        Assert.Equal([Arrangement.Single], trace.Tried);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Lone_trees_of_any_shape_within_300_ms()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MINDWEAVE_PERF") == "1", "Run through scripts/perf.sh.");

        // Each time (the median of three runs) covers every arrangement the shape is given (Rulings R-P6, R-P10).
        // - The comb 150×3 is too deep to nest within its budget; wedged and spiralled, the spiral alone is uncrossed.
        // - The comb 55×3 sits just under the nesting budget (698,000 of 700,000 moves), so its time includes nesting.
        // - The chain is nested, freely and stiffly, and wedged.
        // - The binary tree and the broom are too large to nest; wedges keep them uncrossed.
        // - The stars of 1,000 and 999 dots are nested and wedged twice each: their time covers the creation-order
        //   check round a fan of a thousand.
        Arrangement[] all = [Arrangement.Nested, Arrangement.StiffNested, Arrangement.Wedges, Arrangement.Spiral];
        Arrangement[] unnested = [Arrangement.Wedges, Arrangement.Spiral];
        (string Shape, Arrangement[] Tried, Arrangement Kept)[] cases =
        [
            ("comb 150×3", unnested, Arrangement.Spiral),
            ("comb 55×3", all, Arrangement.Spiral),
            ("chain 600", all, Arrangement.Wedges),
            ("binary 1023", unnested, Arrangement.Wedges),
            ("broom 4×352", unnested, Arrangement.Wedges),
            ("star 1000", all, Arrangement.Wedges),
            ("star 999", all, Arrangement.Wedges),
        ];
        foreach (var (shape, tried, kept) in cases)
        {
            var nodes = LoneShape(shape);
            SpatialPacking.Arrange(nodes); // warm-up

            var times = new List<TimeSpan>();
            var result = Timed(nodes, times);
            Timed(nodes, times);
            Timed(nodes, times);
            var elapsed = times.Order().ElementAt(1);
            var report = SpatialPacking.Measure(nodes, result.Dots);
            Output($"{shape} ({nodes.Count} dots): median {elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms, tried {string.Join(", ", result.Trace.Tried)}, kept {result.Trace.Kept}; enclosing / ideal {report.EnclosingRadius / report.IdealRadius:F3}, {report.BranchCrossings} crossings over {report.BranchEdges} lines");
            Assert.Equal(tried, result.Trace.Tried);
            Assert.Equal(kept, result.Trace.Kept);
            Assert.Equal(0, report.Overlaps);
            Assert.True(elapsed < TimeSpan.FromMilliseconds(300), $"Arranging a lone {shape} took {elapsed.TotalMilliseconds:F1} ms (budget 300 ms).");
        }
    }

    /// <summary>Arranges <paramref name="nodes"/> once and adds the time it took to <paramref name="times"/>.</summary>
    private static (IReadOnlyDictionary<SummaryNodeId, NodePosition> Dots, ArrangeTrace Trace) Timed(List<PackNode> nodes, List<TimeSpan> times)
    {
        var watch = Stopwatch.StartNew();
        var result = SpatialPacking.Trace(nodes);
        times.Add(watch.Elapsed);
        return result;
    }

    /// <summary>The lone tree a <see cref="LoneShapes"/> name stands for.</summary>
    private static List<PackNode> LoneShape(string shape)
    {
        var (kind, size) = (shape.Split(' ')[0], shape.Split(' ')[1]);
        return kind switch
        {
            "comb" => Comb(Number(size.Split('×')[0]), Number(size.Split('×')[1])),
            "chain" => Full(fan: 1, depth: Number(size) - 1),
            "star" => Full(fan: Number(size) - 1, depth: 1),
            "broom" => Broom(Number(size.Split('×')[0]), Number(size.Split('×')[1])),
            "binary" => Full(fan: 2, depth: 9),
            "ternary" => Full(fan: 3, depth: 6),
            "fan" => Full(fan: 6, depth: 4),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        static int Number(string text) => int.Parse(text, CultureInfo.InvariantCulture);
    }
}
