namespace Mindweave.Architecture.Tests;

/// <summary>
/// THIRD-PARTY-NOTICES.txt (written by scripts/third-party-notices.py) holds the licence text of everything the app ships:
/// a name or an address alone is not what the licences ask for, and an embedded font has a licence of its own.
/// </summary>
public class ThirdPartyNoticesTests
{
    private static string Notices() => File.ReadAllText(Path.Combine(LayeringTests.RepositoryRoot(), "THIRD-PARTY-NOTICES.txt"));

    [Fact]
    public void Every_package_has_its_licence_text()
    {
        var without = Notices().Split("\n\n" + new string('=', 100) + "\n\n")
            .Where(section => section.Contains("see the licence or URL above", StringComparison.Ordinal))
            .Select(section => section.Split('\n')[0])
            .ToList();
        Assert.True(without.Count == 0, "No licence text (add it to scripts/licences and to the script): " + string.Join(", ", without));
    }

    [Theory]
    [InlineData("Copyright 2020 The Inter Project Authors")] // the font inside Avalonia.Fonts.Inter
    [InlineData("\nKaTeX 0.19.1\n")] // draws the formulas of a shared page; kept in the repository, not a package
    [InlineData("Copyright (c) 2013-2020 Khan Academy and other contributors")]
    [InlineData("It contains KaTeX fonts")]
    [InlineData("SIL OPEN FONT LICENSE")] // of Inter, and of the KaTeX fonts inside RaTeX.Avalonia
    public void The_embedded_fonts_are_named_with_their_licence(string text) => Assert.Contains(text, Notices(), StringComparison.Ordinal);
}
