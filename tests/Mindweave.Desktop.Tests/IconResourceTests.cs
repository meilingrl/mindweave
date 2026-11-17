using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Icons and corner radii (direction A, 2026-11-17): every icon a view draws comes from Themes/Icons.axaml, whose glyphs
/// are generated from the stroked source Themes/IconStrokes.xml; corners come from Themes/Radii.axaml.
/// </summary>
public partial class IconResourceTests
{
    private static readonly string Desktop = Path.Combine(RepositoryRoot(), "src", "Mindweave.Desktop");

    [Fact]
    public void Every_PathIcon_in_the_views_and_styles_takes_its_data_from_an_icon_resource()
    {
        var keys = IconKeys();
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Desktop, "*.axaml", SearchOption.AllDirectories).Where(IsSource))
        {
            var relative = Path.GetRelativePath(Desktop, file);
            foreach (Match icon in PathIconElement().Matches(File.ReadAllText(file)))
            {
                var data = DataAttribute().Match(icon.Value);
                if (!data.Success)
                {
                    // A PathIcon with no Data set here is a style target or gets its Data from code (checked below).
                    continue;
                }

                var key = ResourceReference().Match(data.Groups[1].Value);
                if (!key.Success)
                {
                    problems.Add($"{relative}: PathIcon draws inline data {Shorten(data.Groups[1].Value)}; add it to Themes/IconStrokes.xml");
                }
                else if (!keys.Contains(key.Groups[1].Value))
                {
                    problems.Add($"{relative}: PathIcon uses {key.Groups[1].Value}, which Themes/Icons.axaml does not define");
                }
            }
        }

        // Icons chosen in code (the copy button, the branch entries) name their resource as a string.
        foreach (var file in Directory.EnumerateFiles(Desktop, "*.cs", SearchOption.AllDirectories).Where(IsSource))
        {
            foreach (Match key in CodeIconKey().Matches(File.ReadAllText(file)))
            {
                if (!keys.Contains(key.Groups[1].Value))
                {
                    problems.Add($"{Path.GetRelativePath(Desktop, file)}: code asks for {key.Groups[1].Value}, which Themes/Icons.axaml does not define");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Icons_axaml_is_generated_from_the_current_stroke_source()
    {
        var source = File.ReadAllText(Path.Combine(Desktop, "Themes", "IconStrokes.xml")).ReplaceLineEndings("\n");
        var icons = File.ReadAllText(Path.Combine(Desktop, "Themes", "Icons.axaml"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
        Assert.True(icons.Contains($"from IconStrokes.xml (sha256 {hash})", StringComparison.Ordinal),
            "Themes/IconStrokes.xml changed since Icons.axaml was generated: run `dotnet run scripts/icons/outline-icons.cs`.");

        var drawn = XDocument.Parse(source).Root!.Elements("icon").Select(e => (string)e.Attribute("key")!).ToList();
        Assert.Equal(drawn.Count, drawn.Distinct().Count());
        Assert.Equal(drawn.Order(), IconKeys().Order());
    }

    [AvaloniaFact]
    public void Every_icon_is_a_filled_outline_inside_the_16_unit_grid()
    {
        // The tree connector runs to the top edge, to meet the guide line of the row above.
        foreach (var key in IconKeys().Where(k => k != "IconTreeBranch"))
        {
            Assert.True(Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Light, out var value), key);
            var bounds = Assert.IsAssignableFrom<Geometry>(value).Bounds;
            // Strokes keep a 1.5 safe margin; with half of a 1.75 stroke the ink stays at least 0.5 inside the edge.
            Assert.True(Math.Max(bounds.Width, bounds.Height) > 4, $"{key}: {bounds}");
            Assert.True(bounds.X >= 0.5 && bounds.Y >= 0.5 && bounds.Right <= 15.6 && bounds.Bottom <= 15.6, $"{key}: {bounds}");
        }
    }

    [AvaloniaFact]
    public void Outlines_keep_their_holes_and_strokes_do_not_double_where_they_cross()
    {
        // The read ring is a stroke around an empty centre; the plus is two strokes that cross in the middle.
        var ring = Render("IconRead", 32);
        Assert.Equal(0, Alpha(ring, 16, 16));
        Assert.Equal(255, Alpha(ring, 16, 5));
        var plus = Render("IconPlus", 32);
        Assert.Equal(255, Alpha(plus, 16, 16));
        Assert.Equal(0, Alpha(plus, 4, 4));
        var select = Render("IconSelect", 32);
        Assert.Equal(0, Alpha(select, 8, 24));
    }

    [Fact]
    public void Corner_radii_in_styles_and_views_come_from_the_shared_radii()
    {
        // Shapes whose radius follows from their own size or drawing, not from the control family.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Styles/Conversation.axaml: 14",  // the question bubble
            "Styles/Conversation.axaml: 17",  // the round stop button (34 px), twice
            "Styles/Conversation.axaml: 1.5", // the branch marks in the margin: 3 px bars
            "Styles/Conversation.axaml: 2",
            "Styles/Conversation.axaml: 1",
            "Styles/Pages.axaml: 28",         // the round home mark (56 px)
            "Views/OnboardingView.axaml: 3",  // the step dots (6 px)
            "Views/OnboardingView.axaml: 8",  // the cards drawn in the illustration
            "Views/OnboardingView.axaml: 12", // the 44 px mark tile
            "Views/RecoveryView.axaml: 13",   // a round 26 px badge
            "Views/Pages/UsageStatisticsView.axaml: 2", // 10 px legend swatches
        };
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Desktop, "*.axaml", SearchOption.AllDirectories).Where(IsSource))
        {
            var relative = Path.GetRelativePath(Desktop, file).Replace('\\', '/');
            foreach (Match literal in LiteralCornerRadius().Matches(File.ReadAllText(file)))
            {
                found.Add($"{relative}: {literal.Groups[1].Value}");
            }
        }

        Assert.Empty(found.Except(allowed).Order());
    }

    [AvaloniaFact]
    public void Controls_take_the_direction_A_radii()
    {
        Assert.Equal(new CornerRadius(8), Radius("RadiusControl"));
        Assert.Equal(new CornerRadius(12), Radius("RadiusCard"));
        Assert.Equal(new CornerRadius(14), Radius("RadiusDialog"));
        Assert.Equal(new CornerRadius(16), Radius("RadiusComposer"));

        var primary = new Button { Classes = { "primary" } };
        var quiet = new Button { Classes = { "quiet" } };
        var card = new Border { Classes = { "card" } };
        var composer = new Border { Classes = { "composer" } };
        var dialog = new Border { Classes = { "dialog" } };
        var badge = new Border { Classes = { "badge" } };
        var field = new TextBox();
        var window = new Window
        {
            RequestedThemeVariant = ThemeVariant.Dark,
            Content = new StackPanel { Children = { primary, quiet, card, composer, dialog, badge, field } },
        };
        window.Show();
        Assert.Equal(new CornerRadius(8), primary.CornerRadius);
        Assert.Equal(new CornerRadius(8), quiet.CornerRadius);
        Assert.Equal(new CornerRadius(8), field.CornerRadius);
        Assert.Equal(new CornerRadius(12), card.CornerRadius);
        Assert.Equal(new CornerRadius(16), composer.CornerRadius);
        Assert.Equal(new CornerRadius(14), dialog.CornerRadius);
        Assert.Equal(Radius("RadiusPill"), badge.CornerRadius);

        // One set of shapes for both themes: the radii are not theme resources.
        window.RequestedThemeVariant = ThemeVariant.Light;
        Assert.Equal(new CornerRadius(12), card.CornerRadius);
        foreach (var theme in Avalonia.Application.Current!.Resources.ThemeDictionaries.Values.OfType<ResourceDictionary>())
        {
            Assert.DoesNotContain(theme.Keys.Cast<object>(), k => k is string s && s.StartsWith("Radius", StringComparison.Ordinal));
        }

        window.Close();
    }

    private static CornerRadius Radius(string key)
    {
        Assert.True(Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Light, out var value), key);
        return Assert.IsType<CornerRadius>(value);
    }

    private static WriteableBitmap Render(string key, int size)
    {
        Assert.True(Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Light, out var value), key);
        var icon = new PathIcon { Data = (Geometry)value!, Width = size, Height = size, Foreground = Brushes.Black };
        var window = new Window { Content = icon, Background = Brushes.Transparent, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        var target = new RenderTargetBitmap(new PixelSize(size, size));
        target.Render(icon);
        window.Close();
        var copy = new WriteableBitmap(target.PixelSize, target.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = copy.Lock())
        {
            target.CopyPixels(new PixelRect(target.PixelSize), buffer.Address, buffer.RowBytes * size, buffer.RowBytes);
        }

        return copy;
    }

    private static byte Alpha(WriteableBitmap bitmap, int x, int y)
    {
        using var buffer = bitmap.Lock();
        var row = new byte[buffer.RowBytes];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes), row, 0, row.Length);
        return row[(x * 4) + 3];
    }

    /// <summary>The icon keys Themes/Icons.axaml defines.</summary>
    internal static HashSet<string> IconKeys() =>
        IconKey().Matches(File.ReadAllText(Path.Combine(Desktop, "Themes", "Icons.axaml")))
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static bool IsSource(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Shorten(string data) => data.Length > 24 ? data[..24] + "…" : data;

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mindweave.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"<StreamGeometry x:Key=""(Icon\w+)"">")]
    private static partial Regex IconKey();

    [GeneratedRegex(@"<PathIcon\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex PathIconElement();

    [GeneratedRegex(@"\bData=""([^""]*)""")]
    private static partial Regex DataAttribute();

    [GeneratedRegex(@"^\{(?:Static|Dynamic)Resource (Icon\w+)\}$")]
    private static partial Regex ResourceReference();

    [GeneratedRegex(@"""(Icon[A-Z]\w+)""")]
    private static partial Regex CodeIconKey();

    [GeneratedRegex(@"CornerRadius(?:=|""\s+Value=)""([\d.]+)""")]
    private static partial Regex LiteralCornerRadius();
}
