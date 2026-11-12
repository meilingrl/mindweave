using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// A thin bar under a statistics row showing its part of the period's tokens (2026-11-12): a track and a filled part, in
/// theme colours. Decoration only: the row's own text says the share, so the bar is hidden from screen readers.
/// </summary>
public sealed class ShareBar : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<ShareBar, double>(nameof(Fraction));

    static ShareBar()
    {
        AffectsRender<ShareBar>(FractionProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ShareBar>(false);
        Avalonia.Automation.AutomationProperties.AccessibilityViewProperty.OverrideDefaultValue<ShareBar>(Avalonia.Automation.AccessibilityView.Raw);
    }

    public ShareBar() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    /// <summary>0 to 1.</summary>
    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 4);

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bounds = new Rect(Bounds.Size);
        var radius = bounds.Height / 2;
        context.DrawRectangle(Brush("UsageShareTrack"), null, new RoundedRect(bounds, radius));
        var filled = Math.Clamp(Fraction, 0, 1) * bounds.Width;
        if (Fraction > 0)
        {
            context.DrawRectangle(Brush("UsageChartInput"), null, new RoundedRect(new Rect(0, 0, Math.Max(bounds.Height, filled), bounds.Height), radius));
        }
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;
}
