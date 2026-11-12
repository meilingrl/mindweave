namespace Mindweave.Application.Statistics;

/// <summary>
/// Local calendar days in a given time zone (the statistics page, 2026-11-12). Days and months are bucketed by the
/// user's own calendar, including across a daylight-saving change, so the zone is always passed in rather than read
/// from the machine (tests use an explicit one).
/// </summary>
public static class LocalCalendar
{
    /// <summary>The day number of 1970-01-01, so a count of days since the Unix epoch maps to a <see cref="DateOnly"/>.</summary>
    public static readonly int EpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    /// <summary>The local calendar day <paramref name="at"/> falls on.</summary>
    public static DateOnly DayOf(DateTimeOffset at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
    }

    /// <summary>
    /// The first instant of a local day: its midnight, or the first valid moment after it where the clock skips
    /// midnight; where midnight happens twice, the earlier one.
    /// </summary>
    public static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>
    /// The zone's offsets from <paramref name="from"/> to <paramref name="to"/>: the first entry holds from the start, each
    /// later one from the instant it takes effect. Enough to turn a timestamp into a local day inside a query without
    /// asking the database about time zones (it only knows the machine's).
    /// </summary>
    public static IReadOnlyList<OffsetChange> Offsets(TimeZoneInfo zone, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var start = from.ToUnixTimeMilliseconds();
        var end = Math.Max(start, to.ToUnixTimeMilliseconds());
        var offset = OffsetAt(zone, start);
        var changes = new List<OffsetChange> { new(start, offset) };
        var at = start;
        while (at < end)
        {
            var next = Math.Min(end, at + DayMs);
            if (OffsetAt(zone, next) == offset)
            {
                at = next;
                continue;
            }

            // The change lies in (at, next]: narrow it to the millisecond.
            long low = at, high = next;
            while (high - low > 1)
            {
                var middle = low + ((high - low) / 2);
                if (OffsetAt(zone, middle) == offset)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            offset = OffsetAt(zone, high);
            changes.Add(new OffsetChange(high, offset));
            at = high;
        }

        return changes;
    }

    /// <summary>A local day from its number of days since 1970-01-01.</summary>
    public static DateOnly FromEpochDay(long days) => DateOnly.FromDayNumber(EpochDayNumber + checked((int)days));

    /// <summary>The first day of the month <paramref name="day"/> is in.</summary>
    public static DateOnly MonthOf(DateOnly day) => new(day.Year, day.Month, 1);

    private const long DayMs = 86_400_000;

    private static long OffsetAt(TimeZoneInfo zone, long ms) =>
        (long)zone.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(ms)).TotalMilliseconds;
}

/// <summary>From <see cref="FromMs"/> (Unix milliseconds) on, local time is UTC plus <see cref="OffsetMs"/>.</summary>
public readonly record struct OffsetChange(long FromMs, long OffsetMs);
