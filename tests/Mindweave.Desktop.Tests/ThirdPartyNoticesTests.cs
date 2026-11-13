namespace Mindweave.Desktop.Tests;

/// <summary>Health check 2026-11-12, dependencies-1: the third-party licences travel with the application.</summary>
public class ThirdPartyNoticesTests
{
    [Fact]
    public void The_notices_are_next_to_the_application_and_name_its_key_packages()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        Assert.True(File.Exists(path), path);
        var text = File.ReadAllText(path);
        foreach (var package in new[] { "Avalonia ", "LiveMarkdown.Avalonia ", "Markdig ", "RaTeX ", "SkiaSharp ", "SQLitePCLRaw.core " })
        {
            Assert.Contains("\n" + package, text, StringComparison.Ordinal);
        }
    }
}
