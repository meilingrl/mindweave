using Avalonia.Styling;
using Mindweave.Application.Preferences;

namespace Mindweave.Desktop.Services;

/// <summary>Puts an appearance choice into effect at once, without a restart (D-022).</summary>
public interface IThemeApplier
{
    void Apply(Appearance appearance);
}

/// <summary>Sets the application's requested theme variant; "follow the system" is Avalonia's default variant.</summary>
internal sealed class AvaloniaThemeApplier : IThemeApplier
{
    public void Apply(Appearance appearance)
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = Variant(appearance);
        }
    }

    public static ThemeVariant Variant(Appearance appearance) => appearance switch
    {
        Appearance.Light => ThemeVariant.Light,
        Appearance.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
