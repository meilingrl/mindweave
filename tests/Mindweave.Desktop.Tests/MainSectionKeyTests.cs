using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The section keys are stored as the last page location in the user's data, so they are a contract: renaming
/// <see cref="MainSection"/> members must not change them (the summary network's key stays "network").
/// </summary>
public class MainSectionKeyTests
{
    [Theory]
    [InlineData(MainSection.Explore, "explore")]
    [InlineData(MainSection.SummaryNetwork, "network")]
    [InlineData(MainSection.Settings, "settings")]
    public void Stored_section_keys_never_change(MainSection section, string key)
    {
        Assert.Equal(key, section.ToKey());
        Assert.Equal(section, MainSectionKeys.FromKey(key));
    }
}
