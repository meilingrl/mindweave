using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// The statistics page's bar chart (2026-11-12): one bar per day or month, input below and output stacked above in two
/// shades of the accent, a day without requests a short flat mark on the baseline so the gap is visible. Hovering a bar,
/// or moving along them with the arrow keys once the chart has focus, shows its exact numbers in a small card; the bar's
/// words are also its automation item status, and the chart's help text (bound by the page) sums it up. Every colour is a
/// theme resource. Drawn directly: thirty rectangles do not need a charting library.
/// </summary>
public sealed class UsageChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<UsageBarViewModel>?> BarsProperty =
        AvaloniaProperty.Register<UsageChart, IReadOnlyList<UsageBarViewModel>?>(nameof(Bars));

    public static readonly DirectProperty<UsageChart, int> ActiveIndexProperty =
        AvaloniaProperty.RegisterDirect<UsageChart, int>(nameof(ActiveIndex), c => c.ActiveIndex, (c, v) => c.ActiveIndex = v);

    /// <summary>Every theme resource the chart draws with; both themes must define each one as a brush.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "UsageChartInput", "UsageChartOutput", "UsageChartBaseline", "UsageChartGrid", "UsageChartEmpty", "UsageChartActive",
        "FloatingBackground", "FloatingBorder", "PrimaryText", "MutedText", "FocusRing",
    ];

    private const double AxisHeight = 22;
    private const double TopPadding = 22;
    private const double MaxBarWidth = 26;
    private const double LabelSize = 11;

    private int _activeIndex = -1;
    private bool _keyboardFocus;

    static UsageChart()
    {
        AffectsRender<UsageChart>(BarsProperty, ActiveIndexProperty);
        FocusableProperty.OverrideDefaultValue<UsageChart>(true);
        ClipToBoundsProperty.OverrideDefaultValue<UsageChart>(false);
    }

    public UsageChart() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public IReadOnlyList<UsageBarViewModel>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>The bar whose numbers are shown (hovered or reached with the keys); -1 for none.</summary>
    public int ActiveIndex
    {
        get => _activeIndex;
        set
        {
            var count = Bars?.Count ?? 0;
            var clamped = count == 0 || value < 0 ? -1 : Math.Min(value, count - 1);
            if (SetAndRaise(ActiveIndexProperty, ref _activeIndex, clamped))
            {
                AutomationProperties.SetItemStatus(this, clamped >= 0 ? Bars![clamped].Detail : null);
            }
        }
    }

    /// <summary>The bar under a point, or -1 outside the bars.</summary>
    public int IndexAt(Point point)
    {
        var count = Bars?.Count ?? 0;
        if (count == 0 || Bounds.Width <= 0 || point.X < 0 || point.X >= Bounds.Width)
        {
            return -1;
        }

        return Math.Clamp((int)(point.X / (Bounds.Width / count)), 0, count - 1);
    }

    /// <summary>The centre of a bar's slot, for pointer tests.</summary>
    public Point SlotCentre(int index)
    {
        var count = Math.Max(1, Bars?.Count ?? 1);
        var slot = Bounds.Width / count;
        return new Point((index * slot) + (slot / 2), TopPadding + ((Bounds.Height - TopPadding - AxisHeight) / 2));
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 180 : availableSize.Height);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BarsProperty)
        {
            ActiveIndex = -1;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ArgumentNullException.ThrowIfNull(e);
        _keyboardFocus = false;
        ActiveIndex = IndexAt(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!IsFocused)
        {
            ActiveIndex = -1;
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        ArgumentNullException.ThrowIfNull(e);
        _keyboardFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
        if (ActiveIndex < 0 && Bars is { Count: > 0 } bars)
        {
            ActiveIndex = bars.Count - 1;
        }

        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _keyboardFocus = false;
        ActiveIndex = -1;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        ArgumentNullException.ThrowIfNull(e);
        if (Bars is not { Count: > 0 } bars)
        {
            return;
        }

        var current = ActiveIndex < 0 ? bars.Count - 1 : ActiveIndex;
        int? next = e.Key switch
        {
            Key.Left => Math.Max(0, current - 1),
            Key.Right => Math.Min(bars.Count - 1, current + 1),
            Key.Home => 0,
            Key.End => bars.Count - 1,
            _ => null,
        };
        if (next is { } index)
        {
            _keyboardFocus = true;
            ActiveIndex = index;
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Bars is not { Count: > 0 } bars || Bounds.Width <= 0 || Bounds.Height <= AxisHeight + TopPadding)
        {
            return;
        }

        var width = Bounds.Width;
        var plotTop = TopPadding;
        var plotBottom = Bounds.Height - AxisHeight;
        var plotHeight = plotBottom - plotTop;
        var slot = width / bars.Count;
        var barWidth = Math.Max(2, Math.Min(slot * 0.64, MaxBarWidth));
        var max = bars.Max(b => b.Total);

        // Gridlines at the top (the busiest bar's height) and half way, with the busiest value written above.
        var grid = new Pen(Brush("UsageChartGrid"), 1);
        context.DrawLine(grid, new Point(0, Snap(plotTop)), new Point(width, Snap(plotTop)));
        context.DrawLine(grid, new Point(0, Snap(plotTop + (plotHeight / 2))), new Point(width, Snap(plotTop + (plotHeight / 2))));
        if (max > 0)
        {
            context.DrawText(Text(UiText.ChartMaxLabel(max), LabelSize, Brush("MutedText")), new Point(0, 2));
        }

        if (ActiveIndex >= 0)
        {
            context.DrawRectangle(Brush("UsageChartActive"), null, new RoundedRect(new Rect(ActiveIndex * slot, plotTop - 6, slot, plotHeight + 6), 4));
        }

        var input = Brush("UsageChartInput");
        var output = Brush("UsageChartOutput");
        var empty = Brush("UsageChartEmpty");
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var x = (i * slot) + ((slot - barWidth) / 2);
            if (bar.Total == 0 || max == 0)
            {
                context.DrawRectangle(empty, null, new RoundedRect(new Rect(x, plotBottom - 2, barWidth, 2), 1));
                continue;
            }

            // Any use at all stays visible: at least 2 px for the bar, 1 px for a part that has some.
            var total = Math.Max(2, bar.Total * plotHeight / max);
            var inputHeight = bar.Input == 0 ? 0 : Math.Max(1, total * bar.Input / bar.Total);
            var outputHeight = Math.Max(0, total - inputHeight);
            if (inputHeight > 0)
            {
                context.DrawRectangle(input, null, new RoundedRect(new Rect(x, plotBottom - inputHeight, barWidth, inputHeight), outputHeight > 0 ? 0 : 2, outputHeight > 0 ? 0 : 2, 0, 0));
            }

            if (outputHeight > 0)
            {
                context.DrawRectangle(output, null, new RoundedRect(new Rect(x, plotBottom - total, barWidth, outputHeight), 2, 2, 0, 0));
            }
        }

        context.DrawLine(new Pen(Brush("UsageChartBaseline"), 1), new Point(0, Snap(plotBottom)), new Point(width, Snap(plotBottom)));

        // Axis labels, kept inside the chart at both ends.
        var muted = Brush("MutedText");
        for (var i = 0; i < bars.Count; i++)
        {
            if (!bars[i].ShowsAxisLabel && i != ActiveIndex)
            {
                continue;
            }

            var label = Text(bars[i].AxisLabel, LabelSize, i == ActiveIndex ? Brush("PrimaryText") : muted);
            var left = Math.Clamp((i * slot) + (slot / 2) - (label.Width / 2), 0, Math.Max(0, width - label.Width));
            context.DrawText(label, new Point(left, plotBottom + 5));
        }

        if (ActiveIndex >= 0)
        {
            if (_keyboardFocus && IsFocused)
            {
                context.DrawRectangle(null, new Pen(Brush("FocusRing"), 1.5), new RoundedRect(new Rect(ActiveIndex * slot, plotTop - 6, slot, plotHeight + 6).Deflate(0.75), 4));
            }

            DrawCard(context, bars[ActiveIndex], ActiveIndex, slot, plotTop, width);
        }
    }

    /// <summary>The hovered or focused bar's exact numbers, beside the bar and inside the chart.</summary>
    private void DrawCard(DrawingContext context, UsageBarViewModel bar, int index, double slot, double top, double width)
    {
        var primary = Brush("PrimaryText");
        var muted = Brush("MutedText");
        var lines = new List<FormattedText>
        {
            Text(bar.Name, 12, primary, FontWeight.SemiBold),
            Text(string.Format(CultureInfo.CurrentCulture, "{0}  {1}", UiText.ChartInput, bar.InputText), 12, primary),
            Text(string.Format(CultureInfo.CurrentCulture, "{0}  {1}", UiText.ChartOutput, bar.OutputText), 12, primary),
            Text(UiText.RequestCount(bar.Requests) + UiText.RequestsLabel, 11, muted),
        };
        if (bar.Unreported > 0)
        {
            lines.Add(Text(string.Format(CultureInfo.CurrentCulture, "另有 {0} 次未返回用量", bar.Unreported), 11, muted));
        }

        const double Padding = 8;
        const double Gap = 3;
        var cardWidth = lines.Max(l => l.Width) + (Padding * 2);
        var cardHeight = lines.Sum(l => l.Height) + (Gap * (lines.Count - 1)) + (Padding * 2);
        var right = (index + 1) * slot + 6;
        var left = right + cardWidth <= width ? right : Math.Max(0, (index * slot) - 6 - cardWidth);
        var card = new Rect(left, top, cardWidth, cardHeight);
        context.DrawRectangle(Brush("FloatingBackground"), new Pen(Brush("FloatingBorder"), 1), new RoundedRect(card, 6));
        var y = card.Top + Padding;
        foreach (var line in lines)
        {
            context.DrawText(line, new Point(card.Left + Padding, y));
            y += line.Height + Gap;
        }
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;

    private FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(GetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty), FontStyle.Normal, weight), size, brush);

    private static double Snap(double y) => Math.Round(y) + 0.5;
}
