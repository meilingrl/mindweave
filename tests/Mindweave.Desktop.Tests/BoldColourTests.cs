using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The bold colour (D-027): chosen in Settings › Appearance, Brown by default, applied at once and again at the next
/// start; each colour has a readable light and dark value, and the bold weight follows the colour.
/// </summary>
public class BoldColourTests
{
    [AvaloniaFact]
    public async Task The_bold_colour_is_chosen_in_settings_applied_at_once_and_applied_again_after_a_restart()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车历史");
        Assert.Equal(BoldColour.Brown, app.Theme.BoldColours.Last());

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        window.UpdateLayout();
        Assert.True(settings.IsBoldBrown);

        // Through the real control: the Slate choice in the bold colour row.
        var slate = window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "BoldColourSlate");
        slate.IsChecked = true;
        Assert.Equal(BoldColour.Slate, app.Theme.BoldColours.Last());
        Assert.True(settings.IsBoldSlate);
        Assert.False(settings.IsBoldBrown);
        await settings.Saving;
        Assert.Null(settings.BoldColourStatus);
        Assert.Equal(BoldColour.Slate, await app.Services.GetRequiredService<AppearanceService>().GetBoldColourAsync(CancellationToken.None));

        await app.RestartAsync();
        Assert.Equal(BoldColour.Slate, app.Theme.BoldColours.Last());
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var again = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await again.Loaded;
        Assert.True(again.IsBoldSlate);

        // Loading the stored choice into the page applied nothing more; choosing "same as text" applies once and saves.
        var applied = app.Theme.BoldColours.Count;
        again.IsBoldSameAsText = true;
        await again.Saving;
        Assert.Equal(applied + 1, app.Theme.BoldColours.Count);
        Assert.Equal("same-as-text", await app.Services.GetRequiredService<Application.Storage.IPreferenceStore>().GetAsync(AppearanceService.BoldColourKey, CancellationToken.None));
        window.Close();
    }

    [Fact]
    public void Stored_values_map_to_bold_colours_and_unknown_values_mean_brown()
    {
        foreach (var colour in Enum.GetValues<BoldColour>())
        {
            Assert.Equal(colour, AppearanceService.ParseBoldColour(AppearanceService.FormatBoldColour(colour)));
        }

        Assert.Equal(BoldColour.Brown, AppearanceService.ParseBoldColour(null));
        Assert.Equal(BoldColour.Brown, AppearanceService.ParseBoldColour("teal"));
    }

    /// <summary>Every colour reads as body text against the page in both themes (at least 4.5:1).</summary>
    [AvaloniaFact]
    public void Every_bold_colour_is_readable_on_the_page_in_both_themes()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var page = ThemeColour("WindowBackground", theme);
            foreach (var colour in Enum.GetValues<BoldColour>())
            {
                var ratio = SourceHighlightThemeTests.Contrast(ThemeColour(AvaloniaThemeApplier.ResourceKey(colour), theme), page);
                Assert.True(ratio >= 4.5, $"{theme} {colour}: {ratio:F2}:1");
            }
        }
    }

    /// <summary>
    /// The real applier points BoldText at the chosen colour in both themes, so a later theme switch keeps it. A colour
    /// draws bold in Medium; "same as text" keeps the bold weight, its only mark, in answers already shown as well.
    /// </summary>
    [AvaloniaFact]
    public void Applying_a_colour_sets_bold_text_in_both_themes_and_the_weight_follows()
    {
        var app = Avalonia.Application.Current!;
        var window = new Window { Width = 600, Height = 300 };
        try
        {
            AvaloniaThemeApplier.ApplyBoldColour(app, BoldColour.Slate);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Assert.Equal(ThemeColour("BoldTextSlate", theme), ThemeColour(AvaloniaThemeApplier.BoldTextKey, theme));
            }

            var renderer = new MarkdownRenderer();
            window.Content = renderer;
            window.Show();
            renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse("正文**加粗**正文", MarkdownUpdateProducer.DefaultPipeline));
            window.UpdateLayout();
            var bold = Assert.Single(BoldSpans(renderer));
            Assert.Equal(FontWeight.Medium, bold.FontWeight);
            Assert.Equal(ThemeColour("BoldTextSlate", window.ActualThemeVariant), Assert.IsAssignableFrom<ISolidColorBrush>(bold.Foreground).Color);

            AvaloniaThemeApplier.ApplyBoldColour(app, BoldColour.SameAsText);
            Assert.Equal(FontWeight.Bold, bold.FontWeight);
            AvaloniaThemeApplier.ApplyBoldColour(app, BoldColour.Brown);
            Assert.Equal(FontWeight.Medium, bold.FontWeight);
        }
        finally
        {
            window.Close();
            AvaloniaThemeApplier.ApplyBoldColour(app, BoldColour.Brown);
        }
    }

    internal static IEnumerable<Span> BoldSpans(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .SelectMany(b => b.Inlines is { } inlines ? Spans(inlines) : [])
            .Where(s => s.Classes.Contains("Bold"));

    private static IEnumerable<Span> Spans(InlineCollection inlines) =>
        inlines.OfType<Span>().SelectMany(s => Spans(s.Inlines).Prepend(s));

    private static Color ThemeColour(string key, ThemeVariant theme)
    {
        Assert.True(Avalonia.Application.Current!.TryGetResource(key, theme, out var value), key);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
