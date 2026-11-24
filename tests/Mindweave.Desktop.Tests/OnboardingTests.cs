using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Application.Storage;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// First-run onboarding (首次引导, requirements §6.1 item 1, 2026-11-17): each step, skipping, resuming after a restart,
/// never again once finished, and 新探索's note while no model is connected.
/// </summary>
public class OnboardingTests
{
    [AvaloniaFact]
    public async Task First_run_walks_welcome_model_workspace_and_lands_in_new_exploration()
    {
        await using var app = DesktopHarness.Create();
        await app.StartAsync();

        var guide = Assert.IsType<OnboardingViewModel>(app.Root.Current);
        Assert.True(guide.IsGuided);
        Assert.Equal(OnboardingStep.Welcome, guide.Step);
        Assert.Equal(UiText.OnboardingStepOf(1), guide.StepText);
        Assert.True(guide.CanSkip);
        Assert.False(guide.CanGoBack);

        guide.NextCommand.Execute(null);
        Assert.Equal(OnboardingStep.Model, guide.Step);
        Assert.Equal(UiText.OnboardingStepOf(2), guide.StepText);
        Assert.True(guide.CanGoBack);

        guide.LaterCommand.Execute(null);
        Assert.Equal(OnboardingStep.Workspace, guide.Step);
        Assert.False(guide.CanSkip);
        Assert.True(guide.CanGoBack);

        guide.Form.Name = "火车历史";
        await guide.Form.SubmitCommand.ExecuteAsync(null);

        Assert.Same(app.ShellPage, app.Root.Current);
        Assert.Equal("火车历史", app.Workbench.CurrentWorkspace!.Name.Value);
        Assert.IsType<NewConversationViewModel>(app.ShellPage.CurrentPage);

        // Back leads to the new workspace's home, as from any 新探索.
        app.ShellPage.GoBackCommand.Execute(null);
        Assert.IsType<ExploreHomeViewModel>(app.ShellPage.CurrentPage);
        Assert.Equal("done", await Preference(app));
    }

    [AvaloniaFact]
    public async Task Back_returns_to_the_previous_step()
    {
        await using var app = DesktopHarness.Create();
        await app.StartAsync();
        var guide = Assert.IsType<OnboardingViewModel>(app.Root.Current);

        guide.NextCommand.Execute(null);
        guide.LaterCommand.Execute(null);
        guide.BackCommand.Execute(null);
        Assert.Equal(OnboardingStep.Model, guide.Step);
        guide.BackCommand.Execute(null);
        Assert.Equal(OnboardingStep.Welcome, guide.Step);

        // Nothing before the first step.
        guide.BackCommand.Execute(null);
        Assert.Equal(OnboardingStep.Welcome, guide.Step);
    }

    [AvaloniaFact]
    public async Task Model_step_saves_through_the_settings_service_and_moves_on()
    {
        await using var app = DesktopHarness.Create();
        await app.StartAsync();
        var guide = Assert.IsType<OnboardingViewModel>(app.Root.Current);
        guide.NextCommand.Execute(null);

        // The settings page's own form: a preset fills the address and the connection's name.
        var model = guide.Model!;
        await model.Loaded;
        model.SelectedProvider = ProviderPresets.DeepSeek;
        Assert.Equal(ProviderPresets.DeepSeek.BaseUrl, model.BaseUrl);
        model.ApiKey = "sk-guide";
        model.Model = "deepseek-chat";
        await guide.SaveModelCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingStep.Workspace, guide.Step);
        var saved = await app.Services.GetRequiredService<IModelSettings>().GetAsync(CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal("deepseek-chat", saved.Model);
        Assert.Equal("DeepSeek", saved.ConnectionName);
        Assert.True(await app.Services.GetRequiredService<IModelSettings>().SavedKeyExistsAsync(CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task A_refused_model_stays_on_its_step_and_says_why()
    {
        await using var app = DesktopHarness.Create();
        await app.StartAsync();
        var guide = Assert.IsType<OnboardingViewModel>(app.Root.Current);
        guide.NextCommand.Execute(null);
        await guide.Model!.Loaded;

        guide.Model.SelectedProvider = ProviderPresets.OpenAI;
        guide.Model.Model = "gpt-test";
        await guide.SaveModelCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingStep.Model, guide.Step);
        Assert.True(guide.Model.StatusIsError);
        Assert.Equal(UiText.SettingsProblem(ModelSettingsProblem.MissingKey), guide.Model.Status);
        Assert.Null(await app.Services.GetRequiredService<IModelSettings>().GetAsync(CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task Skip_goes_straight_to_the_workspace_and_is_kept()
    {
        await using var app = DesktopHarness.Create();
        await app.StartAsync();
        var guide = Assert.IsType<OnboardingViewModel>(app.Root.Current);

        guide.SkipCommand.Execute(null);
        Assert.Equal(OnboardingStep.Workspace, guide.Step);
        Assert.False(guide.CanSkip);
        await guide.Saving;
        Assert.Equal("workspace", await Preference(app));

        // Skipped from welcome: a restart opens the workspace step, not the welcome again.
        await app.RestartAsync();
        Assert.Equal(OnboardingStep.Workspace, Assert.IsType<OnboardingViewModel>(app.Root.Current).Step);
    }

    [AvaloniaFact]
    public async Task A_restart_resumes_at_the_step_reached()
    {
        await using var app = DesktopHarness.Create();
        await app.StartAsync();
        var guide = Assert.IsType<OnboardingViewModel>(app.Root.Current);
        guide.NextCommand.Execute(null);
        await guide.Saving;

        await app.RestartAsync();
        guide = Assert.IsType<OnboardingViewModel>(app.Root.Current);
        Assert.True(guide.IsGuided);
        Assert.Equal(OnboardingStep.Model, guide.Step);

        guide.LaterCommand.Execute(null);
        await guide.Saving;
        await app.RestartAsync();
        Assert.Equal(OnboardingStep.Workspace, Assert.IsType<OnboardingViewModel>(app.Root.Current).Step);
    }

    [AvaloniaFact]
    public async Task A_finished_guide_is_never_shown_again()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("唯一");
        Assert.Equal("done", await Preference(app));

        // The last workspace deleted: only a workspace is asked for.
        app.AskToDeleteWorkspace();
        var dialog = app.Root.WorkspaceDeletion;
        await dialog.Loading;
        dialog.TypedName = "唯一";
        await dialog.DeleteCommand.ExecuteAsync(null);
        var plain = Assert.IsType<OnboardingViewModel>(app.Root.Current);
        Assert.False(plain.IsGuided);
        Assert.Equal(OnboardingStep.Workspace, plain.Step);
        Assert.False(plain.CanGoBack);

        // Nor after a restart with no workspace.
        await app.RestartAsync();
        plain = Assert.IsType<OnboardingViewModel>(app.Root.Current);
        Assert.False(plain.IsGuided);
        Assert.Equal(OnboardingStep.Workspace, plain.Step);
    }

    [AvaloniaFact]
    public async Task An_installation_from_before_the_guide_is_treated_as_finished()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("旧数据");
        var store = app.Services.GetRequiredService<IPreferenceStore>();
        await store.RemoveAsync(OnboardingProgressService.Key, CancellationToken.None);

        await app.RestartAsync();
        Assert.Same(app.ShellPage, app.Root.Current);
        Assert.Equal("done", await Preference(app));
    }

    [AvaloniaFact]
    public async Task Example_names_fill_the_workspace_name()
    {
        await using var app = DesktopHarness.Create();
        await app.StartAsync();
        var guide = Assert.IsType<OnboardingViewModel>(app.Root.Current);
        guide.SkipCommand.Execute(null);

        guide.UseExampleCommand.Execute(UiText.WorkspaceExamples[1]);
        Assert.Equal(UiText.WorkspaceExamples[1], guide.Form.Name);
        await guide.Form.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(UiText.WorkspaceExamples[1], app.Workbench.CurrentWorkspace!.Name.Value);
    }

    [AvaloniaFact]
    public async Task New_exploration_notes_a_missing_model_until_one_is_saved()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        Assert.IsType<ExploreHomeViewModel>(app.ShellPage.CurrentPage).NewConversationCommand.Execute(null);
        var page = Assert.IsType<NewConversationViewModel>(app.ShellPage.CurrentPage);
        await page.ModelNote!.Checked;
        Assert.True(page.ModelNote.IsShown);

        // Saved anywhere (here as from 设置): the note goes.
        await app.ConfigureModelAsync();
        await UiWait.UntilAsync(() => !page.ModelNote.IsShown);

        // Its action opens 设置.
        page.OpenSettingsCommand.Execute(null);
        Assert.IsType<SettingsHomeViewModel>(app.ShellPage.CurrentPage);
    }

    [AvaloniaFact]
    public async Task New_exploration_has_no_note_once_a_model_is_connected()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        await app.RestartAsync();

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        if (app.ShellPage.CurrentPage is not NewConversationViewModel)
        {
            Assert.IsType<ExploreHomeViewModel>(app.ShellPage.CurrentPage);
            ((ExploreHomeViewModel)app.ShellPage.CurrentPage).NewConversationCommand.Execute(null);
        }

        var page = Assert.IsType<NewConversationViewModel>(app.ShellPage.CurrentPage);
        await page.ModelNote!.Checked;
        Assert.False(page.ModelNote.IsShown);
    }

    [AvaloniaFact]
    public async Task The_guide_works_through_the_real_controls()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartAsync();
        window.UpdateLayout();

        var view = window.GetVisualDescendants().OfType<OnboardingView>().Single();
        Assert.True(Find<StackPanel>(view, "WelcomeStep").IsVisible);
        Assert.False(Find<StackPanel>(view, "ModelStep").IsVisible);
        Assert.Equal(["NextButton"], VisiblePrimaries(view));

        // Enter takes the step's one primary action.
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.UpdateLayout();
        Assert.True(Find<StackPanel>(view, "ModelStep").IsVisible);
        Assert.Equal(["SaveModelButton"], VisiblePrimaries(view));

        // The model step is the settings form, showing only what a first connection needs.
        var form = Find<ModelSettingsForm>(view, "ModelForm");
        Assert.False(form.ShowsDetails);
        Assert.False(Find<Grid>(form, "DetailRows").IsVisible);
        Assert.True(Find<ComboBox>(form, "ProviderPicker").IsVisible);
        Assert.True(Find<TextBox>(form, "ApiKey").IsVisible);

        UiInput.Click(window, Find<Button>(view, "LaterButton"));
        window.UpdateLayout();
        Assert.True(Find<StackPanel>(view, "WorkspaceStep").IsVisible);
        Assert.Equal(["CreateButton"], VisiblePrimaries(view));

        // A chip fills the box; typing replaces it; Enter creates.
        var chip = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ExampleNameChip");
        UiInput.Click(window, chip);
        var name = Find<TextBox>(view, "NameBox");
        Assert.Equal(UiText.WorkspaceExamples[0], name.Text);
        name.Focus();
        name.SelectAll();
        window.KeyTextInput("铁路史");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => app.Root.Current is ShellViewModel);
        window.UpdateLayout();

        // Landed in 新探索 with the input focused, ready for the first question, and told a model is missing.
        var fresh = window.GetVisualDescendants().OfType<NewConversationView>().Single();
        await UiWait.UntilAsync(() => Find<TextBox>(fresh, "Input").IsFocused);
        await UiWait.UntilAsync(() => { window.UpdateLayout(); return Find<Border>(fresh, "ModelNote").IsVisible; });
        Assert.Equal("铁路史", app.Workbench.CurrentWorkspace!.Name.Value);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Skip_through_the_real_controls()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartAsync();
        window.UpdateLayout();

        var view = window.GetVisualDescendants().OfType<OnboardingView>().Single();
        UiInput.Click(window, Find<Button>(view, "SkipButton"));
        window.UpdateLayout();
        Assert.True(Find<StackPanel>(view, "WorkspaceStep").IsVisible);
        Assert.False(Find<Button>(view, "SkipButton").IsVisible);
        window.Close();
    }

    private static async Task<string?> Preference(DesktopHarness app)
    {
        await app.Services.GetRequiredService<Services.AppStartup>().OnboardingFinished;
        return await app.Services.GetRequiredService<IPreferenceStore>().GetAsync(OnboardingProgressService.Key, CancellationToken.None);
    }

    private static List<string?> VisiblePrimaries(Control root) =>
        [.. root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("primary") && b.IsEffectivelyVisible).Select(b => b.Name)];
}
