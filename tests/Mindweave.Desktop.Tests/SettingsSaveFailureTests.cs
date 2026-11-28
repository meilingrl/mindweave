using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Preferences;
using Mindweave.Application.Storage;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The settings page saves each choice in the background. A write that fails is reported beside its setting while the
/// choice still applies; the next write that succeeds clears the report. Each setting's failure text is checked here.
/// </summary>
public class SettingsSaveFailureTests
{
    [AvaloniaFact]
    public async Task A_choice_that_could_not_be_saved_is_reported_beside_it_and_the_next_save_clears_the_report()
    {
        var preferences = new SwitchablePreferences();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IPreferenceStore>(preferences));
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        Assert.True(settings.CanChooseNewsSound);
        Assert.True(settings.CanChooseRecommendations);

        preferences.FailWrites = true;
        settings.IsDark = true;
        settings.IsBoldSlate = true;
        settings.PlaysNewsSound = !settings.PlaysNewsSound;
        settings.ShowsInWorkspaceNews = !settings.ShowsInWorkspaceNews;
        settings.PreparesNames = !settings.PreparesNames;
        settings.RecommendsQuestions = !settings.RecommendsQuestions;
        await settings.Saving;

        Assert.Equal(UiText.AppearanceSaveFailed, settings.AppearanceStatus);
        Assert.Equal(UiText.BoldColourSaveFailed, settings.BoldColourStatus);
        Assert.Equal(UiText.NewsSoundSaveFailed, settings.NewsSoundStatus);
        Assert.Equal(UiText.NewsInWorkspaceSaveFailed, settings.InWorkspaceNewsStatus);
        Assert.Equal(UiText.PrepareNamesSaveFailed, settings.PrepareNamesStatus);
        Assert.Equal(UiText.RecommendationsSaveFailed, settings.RecommendationsStatus);

        // The choices still apply for this run.
        Assert.True(settings.IsDark);
        Assert.True(settings.IsBoldSlate);

        preferences.FailWrites = false;
        settings.IsLight = true;
        settings.IsBoldPlum = true;
        settings.PlaysNewsSound = !settings.PlaysNewsSound;
        settings.ShowsInWorkspaceNews = !settings.ShowsInWorkspaceNews;
        settings.PreparesNames = !settings.PreparesNames;
        settings.RecommendsQuestions = !settings.RecommendsQuestions;
        await settings.Saving;

        Assert.Null(settings.AppearanceStatus);
        Assert.Null(settings.BoldColourStatus);
        Assert.Null(settings.NewsSoundStatus);
        Assert.Null(settings.InWorkspaceNewsStatus);
        Assert.Null(settings.PrepareNamesStatus);
        Assert.Null(settings.RecommendationsStatus);
    }

    [AvaloniaFact]
    public async Task Choices_are_written_one_after_another_so_the_last_one_made_is_stored()
    {
        var preferences = new SwitchablePreferences();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IPreferenceStore>(preferences));
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;

        preferences.WriteDelay = TimeSpan.FromMilliseconds(30);
        settings.IsDark = true;
        settings.IsLight = true;
        settings.IsDark = true;
        await settings.Saving;

        Assert.Equal(["dark", "light", "dark"], preferences.Writes.Where(w => w.Key == AppearanceService.Key).Select(w => w.Value));
    }

    [AvaloniaFact]
    public async Task A_personal_note_that_could_not_be_saved_is_reported_and_still_applies()
    {
        var preferences = new SwitchablePreferences();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IPreferenceStore>(preferences));
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Note!.Loaded;

        preferences.FailWrites = true;
        settings.Note.Text = "我是护士。";
        await settings.Note.Saving;
        Assert.Equal(UiText.PersonalNoteSaveFailed, settings.Note.Status);
        Assert.Equal("我是护士。", await app.Services.GetRequiredService<IPersonalNote>().GetAsync(CancellationToken.None));

        preferences.FailWrites = false;
        settings.Note.Text = "我是护士，懂基础化学。";
        await settings.Note.Saving;
        Assert.Null(settings.Note.Status);
        Assert.Equal("我是护士，懂基础化学。", await preferences.GetAsync(PersonalNoteService.Key, CancellationToken.None));

        // Emptied: the key is removed, and nothing is sent.
        settings.Note.Text = string.Empty;
        await settings.Note.Saving;
        Assert.Null(await preferences.GetAsync(PersonalNoteService.Key, CancellationToken.None));
    }

    /// <summary>Preferences in memory whose writes can be made to fail or to take a while; writes are recorded in order.</summary>
    private sealed class SwitchablePreferences : IPreferenceStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public volatile bool FailWrites;

        public TimeSpan WriteDelay { get; set; }

        public ConcurrentQueue<KeyValuePair<string, string>> Writes { get; } = new();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            if (WriteDelay > TimeSpan.Zero)
            {
                await Task.Delay(WriteDelay, cancellationToken);
            }

            if (FailWrites)
            {
                throw new IOException("The preference could not be written.");
            }

            _values[key] = value;
            Writes.Enqueue(new(key, value));
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }
}
