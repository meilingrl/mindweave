using Avalonia;
using Avalonia.Controls;

namespace Mindweave.Desktop.Views;

/// <summary>
/// A label and its actions on one row: the label fills what the actions leave (as a right-docked <see cref="DockPanel"/>
/// would lay them out) while its whole text fits beside them; when it would not, the label takes a line of its own and
/// the actions sit right-aligned on the line below, so neither is cut. The first child is the label, the second the actions.
/// </summary>
public sealed class ReflowRow : Panel
{
    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<ReflowRow, double>(nameof(LineSpacing), 4);

    private bool _stacked;

    static ReflowRow() => AffectsMeasure<ReflowRow>(LineSpacingProperty);

    /// <summary>The gap between the label's line and the actions' line once they are stacked.</summary>
    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2)
        {
            return base.MeasureOverride(availableSize);
        }

        var label = Children[0];
        var actions = Children[1];
        actions.Measure(availableSize.WithWidth(double.PositiveInfinity));
        var actionsSize = actions.DesiredSize;
        label.Measure(availableSize.WithWidth(double.PositiveInfinity));
        var labelWidth = label.DesiredSize.Width;

        _stacked = labelWidth + actionsSize.Width > availableSize.Width;
        if (!_stacked)
        {
            label.Measure(availableSize.WithWidth(Math.Max(0, availableSize.Width - actionsSize.Width)));
            return new Size(label.DesiredSize.Width + actionsSize.Width, Math.Max(label.DesiredSize.Height, actionsSize.Height));
        }

        label.Measure(availableSize);
        return new Size(
            Math.Max(label.DesiredSize.Width, actionsSize.Width),
            label.DesiredSize.Height + LineSpacing + actionsSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
        {
            return base.ArrangeOverride(finalSize);
        }

        var label = Children[0];
        var actions = Children[1];
        var actionsWidth = Math.Min(actions.DesiredSize.Width, finalSize.Width);
        if (!_stacked)
        {
            actions.Arrange(new Rect(finalSize.Width - actionsWidth, 0, actionsWidth, finalSize.Height));
            label.Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - actionsWidth), finalSize.Height));
            return finalSize;
        }

        var labelHeight = label.DesiredSize.Height;
        label.Arrange(new Rect(0, 0, finalSize.Width, labelHeight));
        actions.Arrange(new Rect(finalSize.Width - actionsWidth, labelHeight + LineSpacing, actionsWidth, actions.DesiredSize.Height));
        return finalSize;
    }
}
