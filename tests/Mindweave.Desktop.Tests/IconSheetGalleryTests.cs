using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// G56: every icon at the sizes the app shows (12–16 px, and 20 px for the large marks) in the text, accent, danger and
/// navigation colours, rasterised at 100 % and 200 % (icon-sheet-1x / -2x). For checking crispness and contrast after
/// changing Themes/IconStrokes.xml. Runs with the gallery (MINDWEAVE_SCREENSHOT_DIR, MINDWEAVE_GALLERY_THEME).
/// </summary>
[Trait("Category", "Gallery")]
public class IconSheetGalleryTests
{
    private static readonly double[] Sizes = [12, 13, 14, 16, 20];

    [AvaloniaFact]
    public void G56_icon_sheet_at_100_and_200_percent()
    {
        var directory = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "Set MINDWEAVE_SCREENSHOT_DIR to render the gallery.");
        var theme = string.Equals(Environment.GetEnvironmentVariable("MINDWEAVE_GALLERY_THEME"), "Dark", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Dark : ThemeVariant.Light;

        var sheet = new WrapPanel { Orientation = Orientation.Horizontal, Width = 1060, Margin = new Thickness(12) };
        foreach (var key in IconResourceTests.IconKeys().Order(StringComparer.Ordinal))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Width = 345, Height = 30 };
            row.Children.Add(new TextBlock { Text = key[4..], Width = 128, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            foreach (var size in Sizes)
            {
                row.Children.Add(Icon(key, size, "PrimaryText"));
            }

            row.Children.Add(Icon(key, 13, "AccentBrush"));
            row.Children.Add(Icon(key, 13, "DangerText"));
            row.Children.Add(Icon(key, 13, "NavIcon"));
            sheet.Children.Add(row);
        }

        var surface = new Border { Child = sheet };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("WindowBackground"));
        var window = new Window { RequestedThemeVariant = theme, Content = surface, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        window.UpdateLayout();

        Directory.CreateDirectory(directory!);
        foreach (var scale in new[] { 1, 2 })
        {
            var size = new PixelSize((int)Math.Ceiling(surface.Bounds.Width * scale), (int)Math.Ceiling(surface.Bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(surface);
            bitmap.Save(Path.Combine(directory!, $"g56-icon-sheet-{scale}x.png"), new PngBitmapEncoderOptions());
        }

        window.Close();
    }

    private static PathIcon Icon(string key, double size, string brush)
    {
        var icon = new PathIcon { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(key));
        icon.Bind(PathIcon.ForegroundProperty, icon.GetResourceObservable(brush));
        return icon;
    }
}
