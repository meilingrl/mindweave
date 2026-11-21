using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>How the application looks (D-022): follow the system, or a fixed light or dark theme.</summary>
public enum Appearance
{
    FollowSystem,
    Light,
    Dark,
}

/// <summary>
/// Reads and keeps the appearance choices: the theme (unknown or missing means "follow the system", the default) and the
/// bold colour (D-027).
/// </summary>
public sealed class AppearanceService(IPreferenceStore store) : IAppearance
{
    public const string Key = "appearance";

    public async Task<Appearance> GetAsync(CancellationToken cancellationToken) =>
        Parse(await store.GetAsync(Key, cancellationToken).ConfigureAwait(false));

    public Task SetAsync(Appearance appearance, CancellationToken cancellationToken) =>
        store.SetAsync(Key, Format(appearance), cancellationToken);

    public static Appearance Parse(string? stored) => stored switch
    {
        "light" => Appearance.Light,
        "dark" => Appearance.Dark,
        _ => Appearance.FollowSystem,
    };

    public static string Format(Appearance appearance) => appearance switch
    {
        Appearance.Light => "light",
        Appearance.Dark => "dark",
        _ => "system",
    };

    /// <summary>The bold colour (D-027); a missing or unknown value means Brown, the default.</summary>
    public const string BoldColourKey = "bold-colour";

    public async Task<BoldColour> GetBoldColourAsync(CancellationToken cancellationToken) =>
        ParseBoldColour(await store.GetAsync(BoldColourKey, cancellationToken).ConfigureAwait(false));

    public Task SetBoldColourAsync(BoldColour colour, CancellationToken cancellationToken) =>
        store.SetAsync(BoldColourKey, FormatBoldColour(colour), cancellationToken);

    public static BoldColour ParseBoldColour(string? stored) => stored switch
    {
        "slate" => BoldColour.Slate,
        "sage" => BoldColour.Sage,
        "plum" => BoldColour.Plum,
        "same-as-text" => BoldColour.SameAsText,
        _ => BoldColour.Brown,
    };

    public static string FormatBoldColour(BoldColour colour) => colour switch
    {
        BoldColour.Slate => "slate",
        BoldColour.Sage => "sage",
        BoldColour.Plum => "plum",
        BoldColour.SameAsText => "same-as-text",
        _ => "brown",
    };
}
