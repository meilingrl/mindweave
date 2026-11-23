using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// One setting (D-037): its name on the left with a short line under it when that has to be in sight (cost, privacy), its
/// control on the right, a hairline below, and the longer explanation behind an icon (hover, press or keyboard).
/// </summary>
public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> CaptionProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Caption));

    public static readonly StyledProperty<string?> InfoProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Info));

    public static readonly StyledProperty<object?> BelowProperty = AvaloniaProperty.Register<SettingRow, object?>(nameof(Below));

    public static readonly StyledProperty<bool> IsFoundProperty = AvaloniaProperty.Register<SettingRow, bool>(nameof(IsFound), true);

    static SettingRow()
    {
        LabelProperty.Changed.AddClassHandler<SettingRow>((row, _) => AutomationProperties.SetName(row, row.Label));
        BelowProperty.Changed.AddClassHandler<SettingRow>((row, e) =>
        {
            // In the row's own logical tree, as its content is: bindings and resources are found the same way.
            if (e.OldValue is Control old)
            {
                row.LogicalChildren.Remove(old);
            }

            if (e.NewValue is Control below)
            {
                row.LogicalChildren.Add(below);
            }
        });
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>A short line that stays in sight.</summary>
    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>The longer explanation, behind the icon.</summary>
    public string? Info
    {
        get => GetValue(InfoProperty);
        set => SetValue(InfoProperty, value);
    }

    /// <summary>Across the row, under the rest: why a choice could not be saved, a form that belongs to it.</summary>
    public object? Below
    {
        get => GetValue(BelowProperty);
        set => SetValue(BelowProperty, value);
    }

    /// <summary>Shown while searching the settings (apart from <see cref="Visual.IsVisible"/>, which says whether it is offered at all).</summary>
    public bool IsFound
    {
        get => GetValue(IsFoundProperty);
        set => SetValue(IsFoundProperty, value);
    }

    /// <summary>The words keep at least this much beside the control, or the control goes under them.</summary>
    internal const double MinWords = 140;

    private double _contentWidth;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // What the control wants beside the words; under them it may be given less, so that measure is kept.
        if (!Classes.Contains("stacked") && Content is Control control)
        {
            _contentWidth = control.DesiredSize.Width;
        }

        Classes.Set("stacked", Bounds.Width > 0 && _contentWidth > Bounds.Width - MinWords);
    }

    protected override Type StyleKeyOverride => typeof(SettingRow);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_Info") is { } info)
        {
            AutomationProperties.SetName(info, UiText.SettingInfo(Label));
        }
    }
}
