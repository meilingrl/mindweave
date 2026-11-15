namespace Mindweave.Desktop.Navigation;

/// <summary>Menu-level areas of the workbench.</summary>
public enum MainSection
{
    Explore,
    SummaryNetwork,
    Settings,
}

public static class MainSectionKeys
{
    public static string ToKey(this MainSection section) => section switch
    {
        MainSection.Explore => "explore",
        MainSection.SummaryNetwork => "network",
        MainSection.Settings => "settings",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };

    /// <summary>Unknown keys (e.g. written by a newer build) fall back to the explore area.</summary>
    public static MainSection FromKey(string? key) => key switch
    {
        "network" => MainSection.SummaryNetwork,
        "settings" => MainSection.Settings,
        _ => MainSection.Explore,
    };
}
