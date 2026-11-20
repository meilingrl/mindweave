using System.Globalization;

namespace Mindweave.Testing;

/// <summary>
/// How long a test waits for a condition before failing. On a CI runner every wait is three times as long: the shared
/// runners are slow and uneven, and a different 5-second wait ran out there on almost every run (2026-11-20/21). A wait
/// that is met returns at once, so passing tests take no longer; only a real failure is reported later.
/// <c>MINDWEAVE_TEST_WAIT_SCALE</c> (a whole number, 1 or more) sets the factor anywhere.
/// </summary>
public static class TestWaits
{
    public static int Scale { get; } = ReadScale();

    /// <summary>The time to wait, in milliseconds, for a wait written as <paramref name="timeoutMs"/>.</summary>
    public static int Ms(int timeoutMs) => checked(timeoutMs * Scale);

    private static int ReadScale()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("MINDWEAVE_TEST_WAIT_SCALE"), NumberStyles.None, CultureInfo.InvariantCulture, out var scale)
            && scale >= 1)
        {
            return scale;
        }

        return string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
    }
}
