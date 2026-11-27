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

    /// <summary>
    /// macOS's save panel sees an extension it knows (.html, .md) in the suggested name, but takes one it does not know
    /// (.mwbackup) for part of the name and adds its own: "Mindweave 2026-11-27.mwbackup.mwbackup" (found with the owner
    /// on 2026-11-27). Such a name goes to the panel without it.
    /// </summary>
    [Theory]
    [InlineData("Mindweave 2026-11-27.mwbackup", "mwbackup", false, "Mindweave 2026-11-27")]
    [InlineData("Mindweave 2026-11-27.MWBACKUP", "mwbackup", false, "Mindweave 2026-11-27")]
    [InlineData("Mindweave 2026-11-27", "mwbackup", false, "Mindweave 2026-11-27")]
    [InlineData("Railways.html", "html", true, "Railways.html")]
    [InlineData("Notes v1.2.md", "md", true, "Notes v1.2.md")]
    [InlineData("a.mwbackup.zip", "mwbackup", false, "a.mwbackup.zip")]
    public void The_save_panel_gets_a_name_it_will_not_extend_twice(string suggested, string extension, bool known, string inField) =>
        Assert.Equal(inField, MacFilePanel.NameInField(suggested, extension, known));
}
