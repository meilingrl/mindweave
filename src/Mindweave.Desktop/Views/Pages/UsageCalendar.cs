using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// The statistics page's usage calendar (2026-11-17): a square a day for the last year, a column a week (Monday on top),
/// in the empty colour or one of four shades of the accent, month labels above and weekday labels at the left. Hovering a
/// square, or moving with the arrow keys once the calendar has focus (left and right a week, up and down a day), shows
/// that day's numbers in a small card; the day's words are also the automation item status. Every colour is a theme
/// resource. Drawn directly, like <see cref="UsageChart"/>: 371 squares do not need a charting library.
/// </summary>
public sealed class UsageCalendar : Control
{
    public static readonly StyledProperty<UsageCalendarViewModel?> CalendarProperty =
        AvaloniaProperty.Register<UsageCalendar, UsageCalendarViewModel?>(nameof(Calendar));

    /// <summary>The empty colour, then the steps from palest to darkest; the page's legend uses the same keys.</summary>
    public static IReadOnlyList<string> LevelKeys { get; } =
        ["UsageCalendarEmpty", "UsageCalendarLevel1", "UsageCalendarLevel2", "UsageCalendarLevel3", "UsageCalendarLevel4"];

    /// <summary>Every theme resource the calendar draws with; both themes must define each one as a brush.</summary>
    public static IReadOnlyList<string> Keys { get; } =
        [.. LevelKeys, "UsageCalendarActive", "FloatingBackground", "FloatingBorder", "PrimaryText", "MutedText", "FocusRing"];

    private const double MonthRowHeight = 18;
    private const double WeekdayColumnWidth = 20;
    private const double Gap = 3;
    private const double MaxCell = 12;
    private const double LabelSize = 11;

    private UsageCalendarDay? _active;
    private bool _keyboardFocus;

    static UsageCalendar()
    {
        AffectsRender<UsageCalendar>(CalendarProperty);
        AffectsMeasure<UsageCalendar>(CalendarProperty);
        FocusableProperty.OverrideDefaultValue<UsageCalendar>(true);
        ClipToBoundsProperty.OverrideDefaultValue<UsageCalendar>(false);
    }

    public UsageCalendar() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public UsageCalendarViewModel? Calendar
    {
        get => GetValue(CalendarProperty);
        set => SetValue(CalendarProperty, value);
    }

    /// <summary>The day whose numbers are shown (hovered or reached with the keys); null for none.</summary>
    public UsageCalendarDay? Active
    {
        get => _active;
        set
        {
            if (_active == value)
            {
                return;
            }

            _active = value;
            AutomationProperties.SetItemStatus(this, value?.Detail);
            InvalidateVisual();
        }
    }

    /// <summary>The side of a square and the distance from one square to the next, for the current width.</summary>
    public (double Cell, double Step) CellSize(double width)
    {
        var weeks = Math.Max(1, Calendar?.Weeks ?? 1);
        var step = Math.Min(MaxCell + Gap, Math.Max(4, (width - WeekdayColumnWidth + Gap) / weeks));
        return (step - Gap, step);
    }

    /// <summary>The top left corner of a day's square.</summary>
    public Point CellOrigin(int week, int weekday)
    {
        var (_, step) = CellSize(Bounds.Width);
        return new Point(WeekdayColumnWidth + (week * step), MonthRowHeight + (weekday * step));
    }

    /// <summary>The day under a point, or null between squares, after today and outside the calendar.</summary>
    public UsageCalendarDay? DayAt(Point point)
    {
        if (Calendar is not { Weeks: > 0 } calendar)
        {
            return null;
        }

        var (cell, step) = CellSize(Bounds.Width);
        var x = point.X - WeekdayColumnWidth;
        var y = point.Y - MonthRowHeight;
        if (x < 0 || y < 0)
        {
            return null;
        }

        var week = (int)(x / step);
        var weekday = (int)(y / step);
        if (weekday > 6 || x - (week * step) > cell || y - (weekday * step) > cell)
        {
            return null;
        }

        return calendar.At(week, weekday);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        var (_, step) = CellSize(width);
        return new Size(width, MonthRowHeight + (7 * step) - Gap);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CalendarProperty)
        {
            Active = null;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ArgumentNullException.ThrowIfNull(e);
        _keyboardFocus = false;
        Active = DayAt(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!IsFocused)
        {
            Active = null;
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        ArgumentNullException.ThrowIfNull(e);
        _keyboardFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
        if (Active is null && Calendar is { Days.Count: > 0 } calendar)
        {
            Active = calendar.Days[^1];
        }

        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _keyboardFocus = false;
        Active = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        ArgumentNullException.ThrowIfNull(e);
        if (Calendar is not { Days.Count: > 0 } calendar)
        {
            return;
        }

        var current = Active ?? calendar.Days[^1];
        var target = e.Key switch
        {
            Key.Left => current.Day.AddDays(-7),
            Key.Right => current.Day.AddDays(7),
            Key.Up => current.Day.AddDays(-1),
            Key.Down => current.Day.AddDays(1),
            Key.Home => calendar.Days[0].Day,
            Key.End => calendar.Days[^1].Day,
            _ => (DateOnly?)null,
        };
        if (target is { } day)
        {
            // Stops at either end of the calendar rather than wrapping.
            var first = calendar.Days[0].Day;
            var last = calendar.Days[^1].Day;
            var clamped = day < first ? first : day > last ? last : day;
            _keyboardFocus = true;
            Active = calendar.Days[clamped.DayNumber - first.DayNumber];
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Calendar is not { Days.Count: > 0 } calendar || Bounds.Width <= WeekdayColumnWidth)
        {
            return;
        }

        var (cell, step) = CellSize(Bounds.Width);
        var radius = Math.Min(2.5, cell / 4);
        var muted = Brush("MutedText");

        foreach (var month in calendar.Months)
        {
            context.DrawText(Text(month.Label, LabelSize, muted), new Point(WeekdayColumnWidth + (month.Week * step), 0));
        }

        for (var row = 0; row < 7; row++)
        {
            var label = UiText.CalendarWeekdayLabels[row];
            if (label.Length > 0)
            {
                var text = Text(label, LabelSize, muted);
                context.DrawText(text, new Point(0, MonthRowHeight + (row * step) + ((cell - text.Height) / 2)));
            }
        }

        var brushes = LevelKeys.Select(Brush).ToArray();
        foreach (var day in calendar.Days)
        {
            var origin = new Point(WeekdayColumnWidth + (day.Week * step), MonthRowHeight + (day.Weekday * step));
            context.DrawRectangle(brushes[day.Level], null, new RoundedRect(new Rect(origin, new Size(cell, cell)), radius));
        }

        if (Active is { } active)
        {
            var origin = new Point(WeekdayColumnWidth + (active.Week * step), MonthRowHeight + (active.Weekday * step));
            var square = new Rect(origin, new Size(cell, cell)).Inflate(1);
            var ring = _keyboardFocus && IsFocused ? Brush("FocusRing") : Brush("UsageCalendarActive");
            context.DrawRectangle(null, new Pen(ring, 1.5), new RoundedRect(square, radius + 1));
            DrawCard(context, active, square);
        }
    }

    /// <summary>The day's date and numbers beside its square, kept inside the calendar's width.</summary>
    private void DrawCard(DrawingContext context, UsageCalendarDay day, Rect square)
    {
        var primary = Brush("PrimaryText");
        var muted = Brush("MutedText");
        var lines = new List<FormattedText> { Text(UiText.FullDay(day.Day), 12, primary, FontWeight.SemiBold) };
        if (day.Counts.Requests == 0 && day.Counts.Unreported == 0)
        {
            lines.Add(Text(UiText.NoRequests, 12, muted));
        }
        else
        {
            lines.Add(Text(string.Format(CultureInfo.CurrentCulture, "{0}  {1}", UiText.ChartInput, UiText.Tokens(day.Counts.Input)), 12, primary));
            lines.Add(Text(string.Format(CultureInfo.CurrentCulture, "{0}  {1}", UiText.ChartOutput, UiText.Tokens(day.Counts.Output)), 12, primary));
            lines.Add(Text(UiText.RequestCount(day.Counts.Requests) + UiText.RequestsLabel, 11, muted));
            if (day.Counts.Unreported > 0)
            {
                lines.Add(Text(UiText.BarUnreported(day.Counts.Unreported), 11, muted));
            }
        }

        const double Padding = 8;
        const double LineGap = 3;
        var cardWidth = lines.Max(l => l.Width) + (Padding * 2);
        var cardHeight = lines.Sum(l => l.Height) + (LineGap * (lines.Count - 1)) + (Padding * 2);
        var left = square.Right + 6 + cardWidth <= Bounds.Width ? square.Right + 6 : Math.Max(0, square.Left - 6 - cardWidth);
        var top = Math.Max(0, Math.Min(square.Top - 4, Bounds.Height - cardHeight));
        var card = new Rect(left, top, cardWidth, cardHeight);
        context.DrawRectangle(Brush("FloatingBackground"), new Pen(Brush("FloatingBorder"), 1), new RoundedRect(card, 6));
        var y = card.Top + Padding;
        foreach (var line in lines)
        {
            context.DrawText(line, new Point(card.Left + Padding, y));
            y += line.Height + LineGap;
        }
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;

    private FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(GetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty), FontStyle.Normal, weight), size, brush);
}
