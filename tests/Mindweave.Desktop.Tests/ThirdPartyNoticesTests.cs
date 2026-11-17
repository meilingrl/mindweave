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
        foreach (var package in new[] { "Avalonia ", "LiveMarkdown.Avalonia ", "Markdig ", "PdfPig ", "RaTeX ", "SkiaSharp ", "SQLitePCLRaw.core " })
        {
            Assert.Contains("\n" + package, text, StringComparison.Ordinal);
        }
    }

    /// <summary>2026-11-17: Apache-2.0 and MIT need their text to travel with the software, so a package that names one
    /// of them but ships no text points to the standard text printed at the end of the file.</summary>
    [Fact]
    public void Packages_without_their_own_text_get_the_standard_apache_and_mit_texts()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));
        Assert.DoesNotContain("Licence: Apache-2.0\nProject: https://github.com/UglyToad/PdfPig\n\n(No licence text in the package; see", text, StringComparison.Ordinal);
        foreach (var licence in new[] { "Apache-2.0", "MIT" })
        {
            if (text.Contains($"the standard {licence} text is at the end of this file", StringComparison.Ordinal))
            {
                Assert.Contains($"Standard licence text: {licence}\n", text, StringComparison.Ordinal);
            }
        }

        Assert.Contains("Standard licence text: Apache-2.0\n", text, StringComparison.Ordinal);
        Assert.Contains("TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION", text, StringComparison.Ordinal);
    }
}
