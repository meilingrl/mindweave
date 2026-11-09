using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Preferences;
using Mindweave.Application.Storage;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Appearance (D-022): follow the system by default; light or dark from the settings page, applied at once, kept in
/// the local data and applied again at the next start. Both themes define the same resources.
/// </summary>
public class ThemeTests
{
    [AvaloniaFact]
    public async Task Choosing_dark_applies_at_once_and_is_applied_again_after_a_restart()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车历史");
        Assert.Equal(Appearance.FollowSystem, app.Theme.Applied.Last());

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        window.UpdateLayout();
        Assert.True(settings.IsFollowSystem);

        // Through the real control: the "深色" radio button.
        var dark = window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "AppearanceDark");
        dark.IsChecked = true;
        Assert.Equal(Appearance.Dark, app.Theme.Applied.Last());
        Assert.True(settings.IsDark);
        Assert.False(settings.IsFollowSystem);
        await settings.Saving;
        Assert.Null(settings.AppearanceStatus);
        Assert.Equal(Appearance.Dark, await app.Services.GetRequiredService<AppearanceService>().GetAsync(CancellationToken.None));

        await app.RestartAsync();
        Assert.Equal(Appearance.Dark, app.Theme.Applied.Last());
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var again = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await again.Loaded;
        Assert.True(again.IsDark);

        // Loading the stored choice into the page applied nothing more; choosing light applies once and saves.
        var applied = app.Theme.Applied.Count;
        Assert.Equal(Appearance.Dark, app.Theme.Applied.Last());
        again.IsLight = true;
        await again.Saving;
        Assert.Equal(applied + 1, app.Theme.Applied.Count);
        Assert.Equal(Appearance.Light, await app.Services.GetRequiredService<AppearanceService>().GetAsync(CancellationToken.None));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_choice_made_before_the_stored_one_has_loaded_is_kept()
    {
        var store = new GatedPreferences();
        store.Values[AppearanceService.Key] = AppearanceService.Format(Appearance.Light);
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IPreferenceStore>(store));
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车历史");

        // The settings page opens while reading the stored choice is still slow; the reader picks dark meanwhile.
        store.Hold = new TaskCompletionSource();
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        window.UpdateLayout();
        var dark = window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "AppearanceDark");
        dark.IsChecked = true;
        Assert.Equal(Appearance.Dark, app.Theme.Applied.Last());

        store.Hold.SetResult();
        await settings.Loaded;
        await settings.Saving;
        Assert.True(settings.IsDark);
        Assert.Equal(Appearance.Dark, app.Theme.Applied.Last());
        Assert.Equal("dark", store.Values[AppearanceService.Key]);
        window.Close();
    }

    [Fact]
    public void Stored_values_map_to_appearances_and_unknown_values_follow_the_system()
    {
        foreach (var appearance in Enum.GetValues<Appearance>())
        {
            Assert.Equal(appearance, AppearanceService.Parse(AppearanceService.Format(appearance)));
        }

        Assert.Equal(Appearance.FollowSystem, AppearanceService.Parse(null));
        Assert.Equal(Appearance.FollowSystem, AppearanceService.Parse("sepia"));
        Assert.Equal(ThemeVariant.Default, AvaloniaThemeApplier.Variant(Appearance.FollowSystem));
        Assert.Equal(ThemeVariant.Light, AvaloniaThemeApplier.Variant(Appearance.Light));
        Assert.Equal(ThemeVariant.Dark, AvaloniaThemeApplier.Variant(Appearance.Dark));
    }

    [AvaloniaFact]
    public void Light_and_dark_define_the_same_keys_and_dark_is_the_network_sample_palette()
    {
        var themes = Avalonia.Application.Current!.Resources.ThemeDictionaries;
        var light = Assert.IsType<ResourceDictionary>(themes[ThemeVariant.Light]);
        var dark = Assert.IsType<ResourceDictionary>(themes[ThemeVariant.Dark]);
        Assert.Equal(light.Keys.Cast<string>().Order(), dark.Keys.Cast<string>().Order());

        var window = new Window { RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        Assert.Equal(Color.Parse("#18191B"), Brush(window, "NetworkBackground"));
        Assert.Equal(Color.Parse("#B4BACD"), Brush(window, "NetworkNode"));
        window.RequestedThemeVariant = ThemeVariant.Light;
        Assert.Equal(Color.Parse("#4B6BFB"), Brush(window, "NetworkNode"));
        window.Close();
    }

    [AvaloniaFact]
    public void The_dialog_shadow_comes_from_each_theme()
    {
        var dialog = new Border { Classes = { "dialog" } };
        var window = new Window { RequestedThemeVariant = ThemeVariant.Light, Content = dialog };
        window.Show();
        Assert.True(window.TryFindResource("DialogShadow", ThemeVariant.Light, out var light));
        Assert.Equal(Assert.IsType<BoxShadows>(light), dialog.BoxShadow);

        window.RequestedThemeVariant = ThemeVariant.Dark;
        Assert.True(window.TryFindResource("DialogShadow", ThemeVariant.Dark, out var dark));
        Assert.Equal(Assert.IsType<BoxShadows>(dark), dialog.BoxShadow);
        Assert.NotEqual(light, dark);
        window.Close();
    }

    /// <summary>Preferences in memory; a read takes the value at once but answers only when <see cref="Hold"/> is released.</summary>
    private sealed class GatedPreferences : IPreferenceStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public TaskCompletionSource? Hold { get; set; }

        public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        {
            var value = Values.GetValueOrDefault(key);
            if (Hold is { } hold)
            {
                await hold.Task;
            }

            return value;
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    private static Color Brush(Window window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), key);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
