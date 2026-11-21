using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Languages;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Where the user chooses the interface language (bilingual-ui.md §7): Settings › Appearance, and the first step of
/// the guide. Choosing saves the choice and switches in place; a choice that cannot be saved still applies, with a
/// notice; quick choices end in the last one, one rebuild at a time; a first start is in English.
/// </summary>
[Collection(Text.LanguageTests.Name)]
public class LanguageChoiceUiTests
{
    [AvaloniaFact]
    public async Task Choosing_in_settings_switches_and_saves()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 520);
            await app.StartWithWorkspaceAsync("火车");
            var settings = await OpenSettingsAsync(app, window);
            Assert.True(settings.IsChinese);
            Assert.Equal("English", Label(Find<RadioButton>(window, "LanguageEnglish")));
            Assert.Equal("简体中文", Label(Find<RadioButton>(window, "LanguageChinese")));
            Screenshots.Capture(window, "language-01-settings-zh");

            UiInput.Click(window, Find<RadioButton>(window, "LanguageEnglish"));
            await Switch(app).Running;

            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);
            Assert.Equal("en", File.ReadAllText(Path.Combine(app.Paths.DataDirectory, "language")).Trim());
            var again = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(settings, again);
            Assert.True(again.IsEnglish);
            Assert.Null(again.LanguageStatus);

            // The same section, in English, with the language row in view and its choice focused.
            await SettleAsync(window, again);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Appearance");
            Assert.True(InView(window, Find<Border>(window, "LanguagePicker")));
            Assert.True(Find<RadioButton>(window, "LanguageEnglish").IsFocused);
            Screenshots.Capture(window, "language-02-settings-en");
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [AvaloniaFact]
    public async Task Choosing_on_onboarding_rebuilds_the_first_step()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 720);
            await app.StartAsync();
            var guide = Assert.IsType<OnboardingViewModel>(app.Root.Current);
            Assert.True(guide.IsWelcomeStep);
            Assert.True(guide.CanChooseLanguage);
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == UiText.OnboardingHeading);
            Screenshots.Capture(window, "language-03-onboarding-zh");

            UiInput.Click(window, Find<RadioButton>(window, "OnboardingEnglish"));
            await Switch(app).Running;

            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);
            var again = Assert.IsType<OnboardingViewModel>(app.Root.Current);
            Assert.NotSame(guide, again);
            Assert.True(again.IsWelcomeStep);
            Assert.True(again.IsEnglish);
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == UiText.OnboardingHeading);
            Assert.True(Find<RadioButton>(window, "OnboardingEnglish").IsChecked);
            Screenshots.Capture(window, "language-04-onboarding-en");
            Assert.Equal("en", File.ReadAllText(Path.Combine(app.Paths.DataDirectory, "language")).Trim());

            // Later steps have no choice.
            again.NextCommand.Execute(null);
            window.UpdateLayout();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<RadioButton>(), r => r.Name == "OnboardingEnglish" && r.IsEffectivelyVisible);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>Review Focus 1: arrows back and forth on the picker while a switch is under way.</summary>
    [AvaloniaFact]
    public async Task Two_quick_choices_end_in_the_last()
    {
        try
        {
            var choice = new ScriptedChoice { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var app = DesktopHarness.Create(s => s.AddSingleton<ILanguageChoice>(choice));
            var window = app.ShowWindow(1000, 720);
            await app.StartWithWorkspaceAsync("火车");
            await OpenSettingsAsync(app, window);
            var (open, most) = (0, 0);
            app.Session.ViewDetaching += (_, _) => most = Math.Max(most, ++open);
            app.Session.ViewReplaced += (_, _) => open--;

            Find<RadioButton>(window, "LanguageChinese").Focus();
            window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
            await UiWait.UntilAsync(() => choice.Writes.Count == 1);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
            Assert.Equal(UiLanguage.Chinese, CurrentLanguage.Value);

            choice.Gate.TrySetResult();
            await Switch(app).Running;

            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);
            Assert.Equal(UiLanguage.English, choice.Stored);

            // The choices made while the first was being saved joined it: one write, one rebuild, never two at once.
            Assert.Equal([UiLanguage.English], choice.Writes);
            Assert.Equal(1, choice.MostAtOnce);
            Assert.Equal(1, Switch(app).Rebuilds);
            Assert.Equal(1, most);
            Assert.Equal(0, open);
            var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            Assert.True(settings.IsEnglish);
            window.UpdateLayout();
            Assert.True(Find<RadioButton>(window, "LanguageEnglish").IsChecked);

            // And back, on the new view: one more rebuild.
            choice.Gate = null;
            UiInput.Click(window, Find<RadioButton>(window, "LanguageChinese"));
            await Switch(app).Running;
            Assert.Equal((UiLanguage.Chinese, UiLanguage.Chinese, 2), (CurrentLanguage.Value, choice.Stored, Switch(app).Rebuilds));
            Assert.Equal([UiLanguage.English, UiLanguage.Chinese], choice.Writes);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>Review M5: after the first step is rebuilt, the keyboard is where it was, on the language chosen.</summary>
    [AvaloniaFact]
    public async Task Onboarding_choice_keeps_the_keyboard_focus()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 720);
            await app.StartAsync();
            window.UpdateLayout();
            UiInput.Click(window, Find<RadioButton>(window, "OnboardingEnglish"));
            await Switch(app).Running;

            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);
            Assert.IsType<OnboardingViewModel>(app.Root.Current);
            await UiWait.UntilAsync(() =>
            {
                window.UpdateLayout();
                return Find<RadioButton>(window, "OnboardingEnglish").IsFocused;
            });
            Assert.True(Find<RadioButton>(window, "OnboardingEnglish").IsChecked);
            Assert.False(Find<Button>(window, "NextButton").IsFocused);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>
    /// Review M6 (2): a choice that changes while the last one is being written is written too, and only the last one is
    /// applied: English (held), Chinese (held), English again ends in English with one rebuild.
    /// </summary>
    [AvaloniaFact]
    public async Task A_choice_made_during_a_write_is_written_after_it()
    {
        try
        {
            var choice = new ScriptedChoice { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var app = DesktopHarness.Create(s => s.AddSingleton<ILanguageChoice>(choice));
            var window = app.ShowWindow(1000, 720);
            await app.StartWithWorkspaceAsync("火车");
            var settings = await OpenSettingsAsync(app, window);

            settings.IsEnglish = true;
            await UiWait.UntilAsync(() => choice.Writes.Count == 1);
            settings.IsChinese = true;
            var first = choice.Gate;
            choice.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.TrySetResult();
            await UiWait.UntilAsync(() => choice.Writes.Count == 2);
            Assert.Equal(UiLanguage.Chinese, CurrentLanguage.Value);
            settings.IsEnglish = true;
            choice.Gate.TrySetResult();
            await Switch(app).Running;

            Assert.Equal([UiLanguage.English, UiLanguage.Chinese, UiLanguage.English], choice.Writes);
            Assert.Equal((UiLanguage.English, UiLanguage.English), (CurrentLanguage.Value, choice.Stored));
            Assert.Equal(1, Switch(app).Rebuilds);
            Assert.Equal(1, choice.MostAtOnce);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>Review M6 (2): English, then back to Chinese while English is written: the language in use, no rebuild.</summary>
    [AvaloniaFact]
    public async Task Back_to_the_language_in_use_during_a_write_rebuilds_nothing()
    {
        try
        {
            var choice = new ScriptedChoice { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var app = DesktopHarness.Create(s => s.AddSingleton<ILanguageChoice>(choice));
            var window = app.ShowWindow(1000, 720);
            await app.StartWithWorkspaceAsync("火车");
            var settings = await OpenSettingsAsync(app, window);
            var detached = 0;
            app.Session.ViewDetaching += (_, _) => detached++;

            settings.IsEnglish = true;
            await UiWait.UntilAsync(() => choice.Writes.Count == 1);
            settings.IsChinese = true;
            choice.Gate.TrySetResult();
            await Switch(app).Running;

            Assert.Equal([UiLanguage.English, UiLanguage.Chinese], choice.Writes);
            Assert.Equal((UiLanguage.Chinese, UiLanguage.Chinese), (CurrentLanguage.Value, choice.Stored));
            Assert.Equal(0, Switch(app).Rebuilds);
            Assert.Equal(0, detached);
            Assert.Same(settings, app.Workbench.CurrentPage);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>Review Focus 4: the choice cannot be written; it still applies for this run, and the row says so.</summary>
    [AvaloniaFact]
    public async Task Write_failure_switches_with_a_notice()
    {
        try
        {
            var choice = new ScriptedChoice { Succeeds = false };
            await using var app = DesktopHarness.Create(s => s.AddSingleton<ILanguageChoice>(choice));
            var window = app.ShowWindow(1000, 720);
            await app.StartWithWorkspaceAsync("火车");
            await OpenSettingsAsync(app, window);

            UiInput.Click(window, Find<RadioButton>(window, "LanguageEnglish"));
            await Switch(app).Running;

            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);
            Assert.Equal([UiLanguage.English], choice.Writes);
            var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            Assert.True(settings.IsEnglish);
            Assert.Equal("The language setting couldn’t be saved. It still applies until you quit.", settings.LanguageStatus);
            await SettleAsync(window, settings);
            var status = Find<TextBlock>(window, "LanguageStatus");
            Assert.True(status.IsEffectivelyVisible);
            Assert.Equal(settings.LanguageStatus, status.Text);
            Screenshots.Capture(window, "language-05-save-failed-en");
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>A data folder without a choice (a first start, or the user's data from before) opens in English.</summary>
    [AvaloniaFact]
    public async Task First_start_is_English()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            Assert.False(File.Exists(Path.Combine(app.Paths.DataDirectory, "language")));
            Program.UseStoredLanguage(app.Paths);
            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);

            var window = app.ShowWindow(1000, 720);
            await app.StartAsync();
            var guide = Assert.IsType<OnboardingViewModel>(app.Root.Current);
            Assert.True(guide.IsEnglish);
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Welcome to Mindweave");
            Assert.True(Find<RadioButton>(window, "OnboardingEnglish").IsChecked);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    private static LanguageSwitch Switch(DesktopHarness app) => app.Services.GetRequiredService<LanguageSwitch>();

    private static async Task<SettingsHomeViewModel> OpenSettingsAsync(DesktopHarness app, Window window)
    {
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        window.UpdateLayout();
        return settings;
    }

    private static async Task SettleAsync(Window window, SettingsHomeViewModel page)
    {
        await page.Loaded;
        await UiWait.UntilAsync(() =>
        {
            window.UpdateLayout();
            return !page.FocusesLanguage;
        });
        window.UpdateLayout();
    }

    private static string Label(RadioButton segment) =>
        segment.Content as string ?? string.Concat(segment.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static bool InView(Window window, Control control)
    {
        var scroller = window.GetVisualDescendants().OfType<SettingsHomeView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();
        var top = control.TranslatePoint(default, scroller)!.Value.Y;
        return top >= 0 && top + control.Bounds.Height <= scroller.Viewport.Height;
    }

    /// <summary>A stored choice whose writes are recorded, can be held, and can fail.</summary>
    internal sealed class ScriptedChoice : ILanguageChoice
    {
        public TaskCompletionSource? Gate { get; set; }

        public bool Succeeds { get; init; } = true;

        public List<UiLanguage> Writes { get; } = [];

        public UiLanguage Stored { get; private set; } = UiLanguage.English;

        /// <summary>The most writes under way at one time.</summary>
        public int MostAtOnce { get; private set; }

        private int _atOnce;

        public UiLanguage Read() => Stored;

        public Task<UiLanguage> ReadAsync() => Task.FromResult(Stored);

        public async Task<bool> WriteAsync(UiLanguage language)
        {
            Writes.Add(language);
            MostAtOnce = Math.Max(MostAtOnce, ++_atOnce);
            try
            {
                if (Gate is { } gate)
                {
                    await gate.Task;
                }

                if (Succeeds)
                {
                    Stored = language;
                }

                return Succeeds;
            }
            finally
            {
                _atOnce--;
            }
        }
    }
}
