using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Mindweave.Application.Statistics;
using Mindweave.Application.Storage;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The statistics page's usage calendar (2026-11-17): days bucketed into the empty colour and four steps, week columns
/// with Monday on top, month labels over the column each month starts in, and the drawn control's hit testing and keys.
/// </summary>
public class UsageCalendarTests
{
    /// <summary>A Tuesday, so the current week is partial and the first column starts on a Monday 52 weeks earlier.</summary>
    private static readonly DateOnly Today = new(2026, 11, 17);

    private static List<UsageBucket> Year(Func<DateOnly, long>? total = null)
    {
        var rows = new List<UsageBucket>();
        for (var day = UsageStatisticsService.CalendarStart(Today); day <= Today; day = day.AddDays(1))
        {
            var t = total?.Invoke(day) ?? 0;
            rows.Add(new UsageBucket(day, t == 0 ? default : new TokenCounts(t, t / 4, 1, 0)));
        }

        return rows;
    }

    [Fact]
    public void The_calendar_starts_on_the_monday_52_weeks_before_this_week_and_ends_today()
    {
        Assert.Equal(new DateOnly(2025, 11, 17), UsageStatisticsService.CalendarStart(Today));
        Assert.Equal(DayOfWeek.Monday, UsageStatisticsService.CalendarStart(Today).DayOfWeek);

        // On a Sunday the current week is whole; on a Monday it is one day.
        Assert.Equal(new DateOnly(2025, 11, 17), UsageStatisticsService.CalendarStart(new DateOnly(2026, 11, 22)));
        Assert.Equal(new DateOnly(2025, 11, 24), UsageStatisticsService.CalendarStart(new DateOnly(2026, 11, 23)));

        var calendar = UsageCalendarViewModel.From(Year());
        Assert.Equal(53, calendar.Weeks);
        Assert.Equal((52 * 7) + 2, calendar.Days.Count);
    }

    [Fact]
    public void Each_day_sits_in_its_week_column_with_monday_on_top()
    {
        var calendar = UsageCalendarViewModel.From(Year());

        var first = calendar.Days[0];
        Assert.Equal((new DateOnly(2025, 11, 17), 0, 0), (first.Day, first.Week, first.Weekday));
        var sunday = calendar.Days.Single(d => d.Day == new DateOnly(2025, 11, 23));
        Assert.Equal((0, 6), (sunday.Week, sunday.Weekday));
        var nextMonday = calendar.Days.Single(d => d.Day == new DateOnly(2025, 11, 24));
        Assert.Equal((1, 0), (nextMonday.Week, nextMonday.Weekday));
        var today = calendar.Days[^1];
        Assert.Equal((Today, 52, 1), (today.Day, today.Week, today.Weekday));

        // Cells after today are empty, not days.
        Assert.Same(today, calendar.At(52, 1));
        Assert.Null(calendar.At(52, 2));
        Assert.Null(calendar.At(53, 0));
    }

    [Fact]
    public void Month_labels_sit_over_the_column_each_month_starts_in()
    {
        var calendar = UsageCalendarViewModel.From(Year());

        // 2025-11-17 starts in the middle of November; December 1st (a Monday) is in column 2, so November keeps its
        // label at the left edge only if there is room: two columns are too few.
        Assert.Equal("12月", calendar.Months[0].Label);
        Assert.Equal(2, calendar.Months[0].Week);
        Assert.Equal(12, calendar.Months.Count);
        Assert.Equal("11月", calendar.Months[^1].Label);
        Assert.Equal((new DateOnly(2026, 11, 1).DayNumber - new DateOnly(2025, 11, 17).DayNumber) / 7, calendar.Months[^1].Week);
        Assert.All(calendar.Months.Zip(calendar.Months.Skip(1)), pair => Assert.True(pair.Second.Week > pair.First.Week));

        // A calendar whose first month has room keeps its label at the left edge.
        var later = UsageCalendarViewModel.From(Year().Where(b => b.Start >= new DateOnly(2025, 12, 8)).ToList());
        Assert.Equal(new UsageCalendarMonth(0, "12月"), later.Months[0]);
        Assert.Equal("1月", later.Months[1].Label);
    }

    [Fact]
    public void Days_without_tokens_are_step_0_and_the_busiest_is_the_darkest()
    {
        Assert.Equal(0, UsageCalendarViewModel.LevelOf(0, 100, [10, 20, 30]));
        Assert.Equal(UsageCalendarViewModel.Steps, UsageCalendarViewModel.LevelOf(100, 100, [10, 20, 30]));

        // An empty year: no thresholds, every day step 0.
        var empty = UsageCalendarViewModel.From(Year());
        Assert.All(empty.Days, d => Assert.Equal(0, d.Level));
        Assert.Empty(UsageCalendarViewModel.Thresholds(empty.Days.Select(d => d.Counts.Total)));

        // One day of use is the darkest; all the others stay empty.
        var one = UsageCalendarViewModel.From(Year(d => d == Today.AddDays(-3) ? 500 : 0));
        Assert.Equal(UsageCalendarViewModel.Steps, one.Days.Single(d => d.Day == Today.AddDays(-3)).Level);
        Assert.Equal(1, one.Days.Count(d => d.Level > 0));

        // Days that all used the same amount are all the darkest, not the palest.
        var even = UsageCalendarViewModel.From(Year(d => d.Day % 2 == 0 ? 700 : 0));
        Assert.All(even.Days, d => Assert.Equal(d.Counts.Total > 0 ? 4 : 0, d.Level));
    }

    [Fact]
    public void Days_with_use_are_split_into_four_steps_by_quartile()
    {
        Assert.Equal([1L, 2, 3], UsageCalendarViewModel.Thresholds([0, 4, 3, 0, 2, 1]));
        long[] totals = [1, 2, 3, 4];
        var thresholds = UsageCalendarViewModel.Thresholds(totals);
        Assert.Equal([1, 2, 3, 4], totals.Select(t => UsageCalendarViewModel.LevelOf(t, 4, thresholds)));

        // A single very busy day does not leave every other day in the palest step.
        long[] skewed = [100, 200, 300, 400, 500, 600, 700, 800, 1_000_000];
        var skewedThresholds = UsageCalendarViewModel.Thresholds(skewed);
        var levels = skewed.Select(t => UsageCalendarViewModel.LevelOf(t, 1_000_000, skewedThresholds)).ToList();
        Assert.Equal([1, 1, 1, 2, 2, 3, 3, 4, 4], levels);

        // Through the calendar: every step from 0 to 4 is used, and more tokens never mean a paler square.
        var calendar = UsageCalendarViewModel.From(Year(d => d.DayNumber % 5 * 1_000));
        Assert.Equal([0, 1, 2, 3, 4], calendar.Days.Select(d => d.Level).Distinct().Order());
        var byTotal = calendar.Days.OrderBy(d => d.Counts.Total).Select(d => d.Level).ToList();
        Assert.Equal(byTotal.Order(), byTotal);
    }

    [Fact]
    public void A_day_says_its_date_and_numbers()
    {
        var calendar = UsageCalendarViewModel.From(Year(d => d == Today ? 12_345 : 0));
        Assert.Equal("2026年11月17日：输入 12,345，输出 3,086，1 次请求", calendar.Days[^1].Detail);
        Assert.Equal("2026年11月16日：没有请求", calendar.Days[^2].Detail);
        Assert.Contains("1 天有用量，最多的是 2026年11月17日（15,431）", calendar.Summary, StringComparison.Ordinal);
        Assert.Equal("最近 366 天都没有用量。", UsageCalendarViewModel.From(Year()).Summary);
        Assert.Same(UsageCalendarViewModel.Empty, UsageCalendarViewModel.From([]));
    }

    [AvaloniaFact]
    public void Pointing_at_a_square_or_moving_with_the_keys_shows_that_day()
    {
        var calendar = new UsageCalendar { Calendar = UsageCalendarViewModel.From(Year(d => d.DayNumber % 3 * 100)), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window { Width = 700, Height = 300, Content = new Border { Padding = new Thickness(10), Child = calendar } };
        window.Show();
        window.UpdateLayout();

        var (cell, step) = calendar.CellSize(calendar.Bounds.Width);
        Assert.InRange(cell, 6, 12);
        Assert.Equal(3, step - cell, 6);
        Assert.InRange(calendar.Bounds.Height, 18 + (7 * step) - 4, 18 + (7 * step) - 2);

        // The middle of a square is that day; the gap between squares and the cells after today are none.
        var origin = calendar.CellOrigin(10, 3);
        var middle = new Point(origin.X + (cell / 2), origin.Y + (cell / 2));
        Assert.Equal(calendar.Calendar!.At(10, 3), calendar.DayAt(middle));
        Assert.Null(calendar.DayAt(new Point(origin.X + cell + 1.5, middle.Y)));
        var afterToday = calendar.CellOrigin(52, 4);
        Assert.Null(calendar.DayAt(new Point(afterToday.X + 1, afterToday.Y + 1)));

        window.MouseMove(calendar.TranslatePoint(middle, window)!.Value);
        Assert.Equal(calendar.Calendar.At(10, 3), calendar.Active);
        Assert.Equal(calendar.Active!.Detail, AutomationProperties.GetItemStatus(calendar));

        // With focus, the keys start at today: left a week back, up a day back, Home the first day, and no wrapping.
        calendar.Focus();
        calendar.Active = null;
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Equal(Today.AddDays(-7), calendar.Active!.Day);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal(Today.AddDays(-8), calendar.Active!.Day);
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Equal(calendar.Calendar.Days[0], calendar.Active);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal(calendar.Calendar.Days[0], calendar.Active);
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(Today, calendar.Active!.Day);
        window.Close();
    }

    [AvaloniaFact]
    public void Every_colour_is_defined_in_both_themes()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.All(UsageCalendar.Keys, key => Assert.True(
                Avalonia.Application.Current!.Resources.TryGetResource(key, theme, out var value) && value is Avalonia.Media.IBrush, $"{key} in {theme}"));
        }
    }

    [Fact]
    public void The_report_has_every_day_of_the_calendar_whatever_the_period()
    {
        var rows = new UsageRows([new UsageRow("m", "https://x.example", "answer", new DateOnly(2025, 11, 20), new TokenCounts(10, 2, 1, 0))], 1);
        var days = UsageStatisticsService.Buckets(UsagePeriod.Daily, UsageStatisticsService.CalendarStart(Today), Today, rows);
        Assert.Equal(366, days.Count);
        Assert.Equal(new TokenCounts(10, 2, 1, 0), days.Single(d => d.Start == new DateOnly(2025, 11, 20)).Counts);
        Assert.Equal(365, days.Count(d => d.Counts.Total == 0));
    }
}
