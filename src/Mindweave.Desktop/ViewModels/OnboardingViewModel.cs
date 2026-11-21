using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Languages;
using Mindweave.Application.Preferences;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.ViewModels;

/// <summary>Validated workspace-name entry, shared by first-run onboarding and the sidebar's "new workspace".</summary>
public sealed partial class WorkspaceNameForm : ViewModelBase
{
    private readonly Func<WorkspaceName, Task> _submit;

    public WorkspaceNameForm(Func<WorkspaceName, Task> submit) => _submit = submit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        if (!WorkspaceName.TryCreate(Name, out var name, out var error))
        {
            Error = UiText.WorkspaceNameError(error!.Value);
            return;
        }

        Error = null;
        try
        {
            await _submit(name!);
            Name = string.Empty;
        }
#pragma warning disable CA1031 // Saving failed: keep the typed name and show why; nothing is lost.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
            Error = UiText.SaveFailed(ex.Message);
        }
    }

    private bool CanSubmit() => !string.IsNullOrWhiteSpace(Name);

    /// <summary>Back to empty, as when the form is cancelled (the sidebar's and the workspaces page's forms).</summary>
    public void Reset()
    {
        Name = string.Empty;
        Error = null;
    }
}

/// <summary>
/// No workspace exists yet. The first time (首次引导, requirements §6.1 item 1, 2026-11-17) a short guide leads there:
/// what Mindweave is for, connecting a model (or leaving it for later), then the first workspace; it can be skipped at
/// any step, and the step reached is kept so a restart resumes there. Once finished or skipped the guide is never shown
/// again: a later start without a workspace (the last one was deleted) only asks for one. <see cref="Form"/> creates
/// the workspace from whichever step is showing.
/// </summary>
public sealed partial class OnboardingViewModel : ViewModelBase
{
    private readonly IOnboardingProgress? _progress;
    private readonly LanguageSwitch? _language;

    /// <summary>Only the workspace step, as after the guide has been finished.</summary>
    public OnboardingViewModel(Func<WorkspaceName, Task> create)
        : this(create, OnboardingStep.Done, progress: null, model: null)
    {
    }

    /// <param name="start">The step stored by the last run; <see cref="OnboardingStep.Done"/> shows only the workspace step.</param>
    /// <param name="model">The settings page's own model form; without it there is no model step and so no guide.</param>
    /// <param name="language">The language switch; the guide's first step offers the choice (§7).</param>
    public OnboardingViewModel(Func<WorkspaceName, Task> create, OnboardingStep start, IOnboardingProgress? progress, ModelSettingsViewModel? model, LanguageSwitch? language = null)
    {
        _language = language;
        Form = new WorkspaceNameForm(create);
        _progress = progress;
        Model = model;
        IsGuided = start != OnboardingStep.Done && model is not null;
        Step = IsGuided ? start : OnboardingStep.Workspace;
        if (model is not null)
        {
            model.Saved += OnModelSaved;
        }
    }

    public WorkspaceNameForm Form { get; }

    /// <summary>The model connection, the same form and service as 设置 › 模型连接.</summary>
    public ModelSettingsViewModel? Model { get; }

    /// <summary>The guide is showing (first run); otherwise only the workspace step.</summary>
    public bool IsGuided { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcomeStep), nameof(IsModelStep), nameof(IsWorkspaceStep), nameof(StepNumber), nameof(StepText), nameof(CanSkip), nameof(CanGoBack))]
    public partial OnboardingStep Step { get; private set; }

    public bool IsWelcomeStep => Step == OnboardingStep.Welcome;

    public bool IsModelStep => Step == OnboardingStep.Model;

    public bool IsWorkspaceStep => Step == OnboardingStep.Workspace;

    /// <summary>1, 2 or 3.</summary>
    public int StepNumber => (int)Step + 1;

    public string StepText => UiText.OnboardingStepOf(StepNumber);

    public bool CanSkip => IsGuided && Step < OnboardingStep.Workspace;

    public bool CanGoBack => IsGuided && Step > OnboardingStep.Welcome;

    /// <summary>
    /// The guide's first step offers the interface language (user, 2026-11-21), before anything is filled in. Choosing
    /// saves it and rebuilds the step in the new language; later steps have no choice.
    /// </summary>
    public bool CanChooseLanguage => IsGuided && _language is not null;

    public bool IsEnglish
    {
        get => CurrentLanguage.Value == UiLanguage.English;
        set => ChooseLanguage(value, UiLanguage.English);
    }

    public bool IsChinese
    {
        get => CurrentLanguage.Value == UiLanguage.Chinese;
        set => ChooseLanguage(value, UiLanguage.Chinese);
    }

    /// <summary>
    /// Beside the choice: the switch waits for something to finish, it could not be made, or saving the choice failed
    /// (the language still applies until the application closes); otherwise nothing.
    /// </summary>
    public string? LanguageStatus => _language?.Notice;

    /// <summary>The status says the switch waits: shown as progress, not as a problem.</summary>
    public bool IsLanguageWaiting => _language?.IsWaiting == true;

    /// <summary>The language was chosen on this step: the step built in its place focuses the choice (review of merge 2, M5).</summary>
    public bool LanguageChosen { get; private set; }

    /// <summary>The step replaces one the language was chosen on: its view puts the keyboard on the choice once, then clears this.</summary>
    public bool FocusesLanguage { get; set; }

    private bool _listening;

    private void ChooseLanguage(bool chosen, UiLanguage language)
    {
        if (chosen && _language is not null)
        {
            LanguageChosen = true;

            // This page is not disposed when the guide ends, so it listens only while its own switch runs.
            if (!_listening)
            {
                _listening = true;
                _language.Changed += OnLanguageSwitchChanged;
            }

            _ = FollowAsync(_language, _language.SwitchAsync(language, this));
        }
    }

    /// <summary>Stops listening once the switch has ended; it never faults (what went wrong is in its notice).</summary>
    private async Task FollowAsync(LanguageSwitch language, Task switching)
    {
        await switching;
        if (_listening)
        {
            _listening = false;
            language.Changed -= OnLanguageSwitchChanged;
        }

        OnLanguageSwitchChanged(language, EventArgs.Empty);
    }

    private void OnLanguageSwitchChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LanguageStatus));
        OnPropertyChanged(nameof(IsLanguageWaiting));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsChinese));
    }

    /// <summary>The latest write of the step reached (tests).</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void Next() => GoTo(Step + 1);

    [RelayCommand]
    private void Back() => GoTo(Step - 1);

    /// <summary>Straight to the workspace, the one thing the application cannot work without.</summary>
    [RelayCommand]
    private void Skip() => GoTo(OnboardingStep.Workspace);

    [RelayCommand]
    private void Later() => GoTo(OnboardingStep.Workspace);

    /// <summary>The settings page's own save; a successful save moves on (<see cref="OnModelSaved"/>), a refusal stays and says why.</summary>
    [RelayCommand]
    private Task SaveModelAsync() =>
        Model is { } model && model.SaveCommand.CanExecute(null) ? model.SaveCommand.ExecuteAsync(null) : Task.CompletedTask;

    [RelayCommand]
    private void UseExample(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            Form.Name = name;
        }
    }

    private void OnModelSaved(object? sender, EventArgs e)
    {
        if (IsModelStep)
        {
            GoTo(OnboardingStep.Workspace);
        }
    }

    private void GoTo(OnboardingStep step)
    {
        if (!IsGuided || step is < OnboardingStep.Welcome or > OnboardingStep.Workspace || step == Step)
        {
            return;
        }

        Step = step;
        if (_progress is not null)
        {
            Saving = SaveAsync(_progress, step, Saving);
        }
    }

    private static async Task SaveAsync(IOnboardingProgress progress, OnboardingStep step, Task previous)
    {
        await previous;
        try
        {
            await progress.SetAsync(step, CancellationToken.None);
        }
#pragma warning disable CA1031 // The step holds for this run; a restart then resumes at an earlier step, nothing is lost.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
        }
    }
}
