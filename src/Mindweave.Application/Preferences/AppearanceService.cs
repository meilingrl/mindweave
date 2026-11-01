using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>How the application looks (D-022): follow the system, or a fixed light or dark theme.</summary>
public enum Appearance
{
    FollowSystem,
    Light,
    Dark,
}

/// <summary>Reads and keeps the appearance choice. Unknown or missing values mean "follow the system" (the default).</summary>
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
}
