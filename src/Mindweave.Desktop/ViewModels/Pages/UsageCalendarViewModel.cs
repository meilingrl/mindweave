using Mindweave.Application.Statistics;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// One day of the usage calendar: its column (<see cref="Week"/>, 0 the oldest) and row (<see cref="Weekday"/>, 0 for
/// Monday), its intensity step (0 for a day without tokens, 1 to <see cref="UsageCalendarViewModel.Steps"/>), and its words for the
/// hover card and a screen reader.
/// </summary>
public sealed record UsageCalendarDay(DateOnly Day, int Week, int Weekday, int Level, TokenCounts Counts, string Detail);

/// <summary>A month's label above the calendar, over the week column the month starts in.</summary>
public sealed record UsageCalendarMonth(int Week, string Label);

/// <summary>
/// The statistics page's usage calendar (2026-11-17): the last year of daily token use as week columns and day rows,
/// Monday on top, coloured in <see cref="Steps"/> shades of the accent by how the day compares with the other days that
/// had use. Built from the report's daily buckets; the control only draws it.
/// </summary>
public sealed class UsageCalendarViewModel
{
    /// <summary>Shades for days with use; a day without tokens is the empty colour, so the legend shows five squares.</summary>
    public const int Steps = 4;

    /// <summary>Week columns at least this far apart get both month labels; a closer first label is left out.</summary>
    public const int MinLabelGap = 3;

    private readonly Dictionary<(int Week, int Weekday), UsageCalendarDay> _byCell;

    private UsageCalendarViewModel(IReadOnlyList<UsageCalendarDay> days, IReadOnlyList<UsageCalendarMonth> months, int weeks, string summary)
    {
        Days = days;
        Months = months;
        Weeks = weeks;
        Summary = summary;
        _byCell = days.ToDictionary(d => (d.Week, d.Weekday));
    }

    public static UsageCalendarViewModel Empty { get; } = new([], [], 0, string.Empty);

    public IReadOnlyList<UsageCalendarDay> Days { get; }

    public IReadOnlyList<UsageCalendarMonth> Months { get; }

    /// <summary>Week columns, the current (partial) week included.</summary>
    public int Weeks { get; }

    /// <summary>The calendar as a screen reader hears it.</summary>
    public string Summary { get; }

    /// <summary>The day in a cell, or null for a cell after today (or outside the calendar).</summary>
    public UsageCalendarDay? At(int week, int weekday) => _byCell.GetValueOrDefault((week, weekday));

    /// <summary>Monday is row 0, Sunday row 6.</summary>
    public static int WeekdayOf(DateOnly day) => ((int)day.DayOfWeek + 6) % 7;

    public static UsageCalendarViewModel From(IReadOnlyList<UsageBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Count == 0)
        {
            return Empty;
        }

        var firstMonday = buckets[0].Start.AddDays(-WeekdayOf(buckets[0].Start));
        var thresholds = Thresholds(buckets.Select(b => b.Counts.Total));
        var max = buckets.Max(b => b.Counts.Total);
        var days = buckets.Select(b => new UsageCalendarDay(
            b.Start,
            (b.Start.DayNumber - firstMonday.DayNumber) / 7,
            WeekdayOf(b.Start),
            LevelOf(b.Counts.Total, max, thresholds),
            b.Counts,
            UiText.CalendarDayDetail(b.Start, b.Counts))).ToList();
        var weeks = days[^1].Week + 1;
        return new UsageCalendarViewModel(days, MonthLabels(days), weeks, UiText.CalendarSummary(buckets));
    }

    /// <summary>
    /// The totals that separate the steps: the quartiles of the days that had use (nearest lower rank), so a few very
    /// busy days do not leave every other day in the palest shade. Fewer than <see cref="Steps"/> - 1 values for a
    /// calendar without use.
    /// </summary>
    public static IReadOnlyList<long> Thresholds(IEnumerable<long> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        var used = totals.Where(t => t > 0).Order().ToList();
        if (used.Count == 0)
        {
            return [];
        }

        return Enumerable.Range(1, Steps - 1).Select(k => used[(int)Math.Floor(k * (used.Count - 1) / (double)Steps)]).ToList();
    }

    /// <summary>
    /// A day's step: 0 without tokens; the busiest day always the darkest; otherwise 1 plus the thresholds it is above.
    /// </summary>
    public static int LevelOf(long total, long max, IReadOnlyList<long> thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (total <= 0)
        {
            return 0;
        }

        return total >= max ? Steps : Math.Min(Steps, 1 + thresholds.Count(t => total > t));
    }

    /// <summary>
    /// A label over the column each month starts in; the first column is labelled with its own month when the next
    /// label is at least <see cref="MinLabelGap"/> columns away, so the left edge says where the calendar begins.
    /// </summary>
    private static List<UsageCalendarMonth> MonthLabels(List<UsageCalendarDay> days)
    {
        var labels = days.Where(d => d.Day.Day == 1).Select(d => new UsageCalendarMonth(d.Week, UiText.CalendarMonthLabel(d.Day))).ToList();
        if (days[0].Day.Day != 1 && (labels.Count == 0 || labels[0].Week >= MinLabelGap))
        {
            labels.Insert(0, new UsageCalendarMonth(0, UiText.CalendarMonthLabel(days[0].Day)));
        }

        return labels;
    }
}
