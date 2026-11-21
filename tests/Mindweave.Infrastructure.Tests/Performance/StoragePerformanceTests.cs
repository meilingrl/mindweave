using System.Diagnostics;
using Mindweave.Application.Languages;
using Mindweave.Core.Branches;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Performance;

/// <summary>
/// Storage read budgets on generated data (docs/evolution.md E6). The full-size run is opt-in
/// (MINDWEAVE_PERF=1, see scripts/perf.sh) because seeding takes a while; the smoke run keeps the seed and
/// the measured paths working on every pass and asserts nothing about time.
/// </summary>
public class StoragePerformanceTests
{
    /// <summary>Sidebar of a full workspace.</summary>
    private static readonly TimeSpan ListBudget = TimeSpan.FromMilliseconds(200);

    /// <summary>Opening the long conversation: every message and block.</summary>
    private static readonly TimeSpan LoadBudget = TimeSpan.FromMilliseconds(300);

    /// <summary>Assembling what the deepest branch sends: every ancestor cut to its frozen range.</summary>
    private static readonly TimeSpan ContextBudget = TimeSpan.FromMilliseconds(300);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task Smoke() => RunAsync(PerfShape.Smoke, enforceBudgets: false);

    [Fact]
    [Trait("Category", "Performance")]
    public Task Full_size_reads_stay_within_budget()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MINDWEAVE_PERF") == "1", "Run through scripts/perf.sh.");
        return RunAsync(PerfShape.Full, enforceBudgets: true);
    }

    private static async Task RunAsync(PerfShape shape, bool enforceBudgets)
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var seeding = Stopwatch.StartNew();
        var longConversation = await PerfSeed.FillAsync(h.Store, h.Workspace, shape, Ct);
        var deepest = await PerfSeed.FillBranchChainAsync(h.Store, h.Branches, longConversation, shape, Ct);
        seeding.Stop();

        var list = await MedianAsync(async () => Assert.Equal(shape.Conversations + shape.BranchDepth, (await h.Store.ListAsync(h.Workspace, Ct)).Count));
        var load = await MedianAsync(async () => Assert.Equal(shape.TurnsInLongConversation * 2, (await h.Store.GetMessagesAsync(longConversation, Ct)).Count));

        var expectedContext = (shape.TurnsInLongConversation * 2) + ((shape.BranchDepth - 1) * shape.TurnsPerBranch * 2);
        var context = await MedianAsync(async () =>
        {
            var ancestors = await h.Branches.GetAncestorContextAsync(deepest, Ct);
            var own = await h.Store.GetMessagesAsync(deepest, Ct);
            Assert.Equal(expectedContext + own.Count, BranchContext.Assemble(ancestors, new ContextSegment(deepest, "蒸汽机", own), ModelPhrases.Current).Count);
        });

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{shape}: seed {seeding.ElapsedMilliseconds} ms, list {list.TotalMilliseconds:F1} ms, load {load.TotalMilliseconds:F1} ms, deepest branch context {context.TotalMilliseconds:F1} ms");
        if (enforceBudgets)
        {
            Assert.True(list <= ListBudget, $"Listing took {list.TotalMilliseconds:F1} ms (budget {ListBudget.TotalMilliseconds} ms).");
            Assert.True(load <= LoadBudget, $"Loading took {load.TotalMilliseconds:F1} ms (budget {LoadBudget.TotalMilliseconds} ms).");
            Assert.True(context <= ContextBudget, $"Assembling the deepest branch took {context.TotalMilliseconds:F1} ms (budget {ContextBudget.TotalMilliseconds} ms).");
        }
    }

    /// <summary>One warm-up, then the median of five runs.</summary>
    private static async Task<TimeSpan> MedianAsync(Func<Task> action)
    {
        await action();
        var samples = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            await action();
            samples.Add(watch.Elapsed);
        }

        return samples.Order().ElementAt(samples.Count / 2);
    }
}
