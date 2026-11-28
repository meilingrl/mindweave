using System.Globalization;

namespace Mindweave.Testing;

/// <summary>
/// The time a performance budget allows. Budgets are written for the development machines; on a CI runner each is
/// three times as long. The CI job that runs them on <c>main</c> does not block a merge and is there to catch a large
/// regression early, not to time the runner (docs/design/diagnostics-and-upkeep.md §3.3, ci-build-9).
/// <c>MINDWEAVE_PERF_BUDGET_SCALE</c> (a whole number, 1 or more) sets the factor anywhere.
/// </summary>
public static class PerfBudget
{
    public static int Scale { get; } = ReadScale();

    /// <summary>The time allowed for a budget written as <paramref name="budget"/>.</summary>
    public static TimeSpan Of(TimeSpan budget) => budget * Scale;

    /// <summary>The time allowed for a budget written as <paramref name="milliseconds"/>.</summary>
    public static TimeSpan Ms(double milliseconds) => Of(TimeSpan.FromMilliseconds(milliseconds));

    private static int ReadScale()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("MINDWEAVE_PERF_BUDGET_SCALE"), NumberStyles.None, CultureInfo.InvariantCulture, out var scale)
            && scale >= 1)
        {
            return scale;
        }

        return string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
    }
}
