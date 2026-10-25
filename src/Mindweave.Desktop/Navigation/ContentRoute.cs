namespace Mindweave.Desktop.Navigation;

/// <summary>
/// What to show in the content area, by stable identity only — never a control or a view model.
/// Routes are compared by value, so pushing the page already on top is a no-op.
/// </summary>
public abstract record ContentRoute
{
    public static ContentRoute HomeOf(MainSection section) => section switch
    {
        MainSection.Explore => ExploreHomeRoute.Instance,
        MainSection.Network => NetworkHomeRoute.Instance,
        MainSection.Settings => SettingsHomeRoute.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };
}

public sealed record ExploreHomeRoute : ContentRoute
{
    public static ExploreHomeRoute Instance { get; } = new();
}

public sealed record NetworkHomeRoute : ContentRoute
{
    public static NetworkHomeRoute Instance { get; } = new();
}

public sealed record SettingsHomeRoute : ContentRoute
{
    public static SettingsHomeRoute Instance { get; } = new();
}

public sealed record StorageDetailsRoute : ContentRoute
{
    public static StorageDetailsRoute Instance { get; } = new();
}
