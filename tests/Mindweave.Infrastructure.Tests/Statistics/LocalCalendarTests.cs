using Mindweave.Application.Statistics;

namespace Mindweave.Infrastructure.Tests.Statistics;

/// <summary>Local days in an explicit zone (2026-11-12), the base of the statistics page's buckets.</summary>
public class LocalCalendarTests
{
    private static readonly TimeZoneInfo Berlin = UsageStatisticsTests.Berlin;

    [Fact]
    public void A_day_starts_at_local_midnight_with_that_day_s_offset()
    {
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.FromHours(1)), LocalCalendar.StartOf(new DateOnly(2026, 3, 29), Berlin));
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 0, 0, 0, TimeSpan.FromHours(2)), LocalCalendar.StartOf(new DateOnly(2026, 3, 30), Berlin));
        Assert.Equal(new DateOnly(2026, 3, 30), LocalCalendar.DayOf(new DateTimeOffset(2026, 3, 29, 22, 30, 0, TimeSpan.Zero), Berlin));
        Assert.Equal(new DateOnly(2026, 3, 28), LocalCalendar.DayOf(new DateTimeOffset(2026, 3, 28, 22, 30, 0, TimeSpan.Zero), Berlin));
    }

    [Fact]
    public void Offsets_list_each_change_at_its_instant()
    {
        var changes = LocalCalendar.Offsets(Berlin, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(
            [
                new OffsetChange(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), 3_600_000),
                new OffsetChange(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), 7_200_000),
                new OffsetChange(new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), 3_600_000),
            ],
            changes);
        Assert.Single(LocalCalendar.Offsets(TimeZoneInfo.Utc, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(60)));
    }

    [Fact]
    public void A_zone_that_skips_midnight_starts_the_day_at_its_first_valid_moment()
    {
        // Clocks go from 00:00 straight to 01:00 on 1 April (a rule some zones have used).
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/SkipMidnight", TimeSpan.Zero, "Skip", "Skip", "Skip Summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 4, 1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 1)),
        ]);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 1, 0, 0, TimeSpan.FromHours(1)), LocalCalendar.StartOf(new DateOnly(2026, 4, 1), zone));
    }
}
