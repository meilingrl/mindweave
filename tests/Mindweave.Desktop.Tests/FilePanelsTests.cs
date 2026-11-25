using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.Tests;

/// <summary>What the app does with a file panel's answer; the panel itself is the system's and is tried by hand.</summary>
public class FilePanelsTests
{
    [Fact]
    public void A_path_picked_in_the_system_panel_is_written_atomically_under_its_own_name()
    {
        var picked = Path.Combine(Path.GetTempPath(), "火车 2026-11-25.html");

        var destination = Assert.IsType<Application.Export.ExportDestination>(FilePanels.DestinationOf(picked));

        Assert.Equal("火车 2026-11-25.html", destination.DisplayName);
        Assert.Equal(picked, destination.LocalPath);
        Assert.Null(destination.OpenWrite);
    }

    [Fact]
    public void A_cancelled_panel_gives_nothing() => Assert.Null(FilePanels.DestinationOf(null));
}
