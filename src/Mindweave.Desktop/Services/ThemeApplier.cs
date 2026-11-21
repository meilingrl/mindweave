using Avalonia.Controls;
using Avalonia.Styling;
using Mindweave.Application.Preferences;

namespace Mindweave.Desktop.Services;

/// <summary>Puts appearance choices into effect at once, without a restart (D-022, D-027).</summary>
public interface IThemeApplier
{
    void Apply(Appearance appearance);

    void ApplyBoldColour(BoldColour colour);
}

/// <summary>
/// Sets the application's requested theme variant ("follow the system" is Avalonia's default variant), and points the
/// bold colour resource at the chosen colour in both themes.
/// </summary>
internal sealed class AvaloniaThemeApplier : IThemeApplier
{
    /// <summary>The resource bold text in answers is drawn with (Styles/Conversation.axaml).</summary>
    public const string BoldTextKey = "BoldText";

    public void Apply(Appearance appearance)
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = Variant(appearance);
        }
    }

    public void ApplyBoldColour(BoldColour colour)
    {
        if (Avalonia.Application.Current is { } app)
        {
            ApplyBoldColour(app, colour);
        }
    }

    /// <summary>
    /// Each theme's BoldText becomes that theme's value of the chosen colour (Themes/Colors.axaml), so the colour follows
    /// a later theme switch too; the bold weight follows the colour (<see cref="Markdown.BoldWeight"/>).
    /// </summary>
    public static void ApplyBoldColour(Avalonia.Application app, BoldColour colour)
    {
        ArgumentNullException.ThrowIfNull(app);
        var source = ResourceKey(colour);
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            if (app.TryGetResource(source, variant, out var brush))
            {
                if (!app.Resources.ThemeDictionaries.TryGetValue(variant, out var provider))
                {
                    provider = new ResourceDictionary();
                    app.Resources.ThemeDictionaries[variant] = provider;
                }

                ((IResourceDictionary)provider)[BoldTextKey] = brush;
            }
        }

        Markdown.BoldWeight.Use(Markdown.BoldWeight.For(colour));
    }

    public static string ResourceKey(BoldColour colour) => BoldTextKey + colour;

    public static ThemeVariant Variant(Appearance appearance) => appearance switch
    {
        Appearance.Light => ThemeVariant.Light,
        Appearance.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
