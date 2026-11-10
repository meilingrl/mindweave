using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application;
using Mindweave.Application.Preferences;
using Mindweave.Application.Startup;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>Workspace entry page: start a conversation or pick one from the sidebar.</summary>
public sealed partial class ExploreHomeViewModel(Workspace workspace, IContentNavigator content) : PageViewModel(ExploreHomeRoute.Instance)
{
    public Workspace Workspace { get; } = workspace;

    public override string Title => Workspace.Name.Value;

    [RelayCommand]
    private void NewConversation() => content.Push(NewConversationRoute.Instance);
}

/// <summary>Settings: the model connection, the appearance (D-022) and where data lives.</summary>
public sealed partial class SettingsHomeViewModel : PageViewModel
{
    private readonly IContentNavigator _content;
    private readonly IAppearance _appearanceService;
    private readonly IThemeApplier _theme;
    private readonly IWorkspaceDeletionPrompt? _workspaceDeletion;
    private readonly INewsAlerts? _newsAlerts;
    private bool _loading;
    private bool _chosen;
    private bool _soundChosen;
    private bool _inWorkspaceChosen;

    public SettingsHomeViewModel(
        AppEnvironment environment,
        ModelSettingsViewModel model,
        IContentNavigator content,
        IAppearance appearance,
        IThemeApplier theme,
        Workspace? workspace = null,
        IWorkspaceDeletionPrompt? workspaceDeletion = null,
        INewsAlerts? newsAlerts = null)
        : base(SettingsHomeRoute.Instance)
    {
        _newsAlerts = newsAlerts;
        Workspace = workspace;
        _workspaceDeletion = workspaceDeletion;
        Environment = environment;
        Model = model;
        _content = content;
        _appearanceService = appearance;
        _theme = theme;
        Loaded = LoadAsync();
    }

    public AppEnvironment Environment { get; }

    public ModelSettingsViewModel Model { get; }

    /// <summary>The workspace these settings were opened in; its delete entry sits in the 工作区 section (S7 round 4).</summary>
    public Workspace? Workspace { get; }

    public bool CanDeleteWorkspace => Workspace is not null && _workspaceDeletion is not null;

    public override string Title => UiText.SettingsTitle;

    internal Task Loaded { get; }

    /// <summary>The stored choice; changing it applies at once and is saved in the background.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowSystem), nameof(IsLight), nameof(IsDark))]
    public partial Appearance Appearance { get; set; }

    /// <summary>Shown only when saving the choice failed; the choice still applies until the application closes.</summary>
    [ObservableProperty]
    public partial string? AppearanceStatus { get; private set; }

    public bool IsFollowSystem
    {
        get => Appearance == Appearance.FollowSystem;
        set => Choose(value, Appearance.FollowSystem);
    }

    public bool IsLight
    {
        get => Appearance == Appearance.Light;
        set => Choose(value, Appearance.Light);
    }

    public bool IsDark
    {
        get => Appearance == Appearance.Dark;
        set => Choose(value, Appearance.Dark);
    }

    /// <summary>Completes when the last choice made on this page has been written (or failed).</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>The settings page can offer the sound (it is built with the preference).</summary>
    public bool CanChooseNewsSound => _newsAlerts is not null;

    /// <summary>
    /// The system's alert sound when a new-answer hint pops up (2026-11-10); off unless chosen. Changing it applies at
    /// once and is saved in the background.
    /// </summary>
    [ObservableProperty]
    public partial bool PlaysNewsSound { get; set; }

    /// <summary>Shown only when saving the sound choice failed; the choice still applies until the application closes.</summary>
    [ObservableProperty]
    public partial string? NewsSoundStatus { get; private set; }

    /// <summary>
    /// Conversations of the open workspace pop up a hint too, not only other workspaces (2026-11-10); on unless turned
    /// off. Changing it applies at once and is saved in the background.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowsInWorkspaceNews { get; set; } = true;

    /// <summary>Shown only when saving that choice failed; the choice still applies until the application closes.</summary>
    [ObservableProperty]
    public partial string? InWorkspaceNewsStatus { get; private set; }

    [RelayCommand]
    private void ShowStorageDetails() => _content.Push(StorageDetailsRoute.Instance);

    [RelayCommand]
    private void DeleteWorkspace()
    {
        if (Workspace is { } workspace)
        {
            _workspaceDeletion?.Open(workspace);
        }
    }

    partial void OnAppearanceChanged(Appearance value)
    {
        if (_loading)
        {
            return;
        }

        _chosen = true;
        _theme.Apply(value);
        Saving = SaveAsync(value);
    }

    partial void OnPlaysNewsSoundChanged(bool value)
    {
        if (_loading || _newsAlerts is null)
        {
            return;
        }

        _soundChosen = true;
        Saving = SaveSoundAsync(_newsAlerts, value, Saving);
    }

    partial void OnShowsInWorkspaceNewsChanged(bool value)
    {
        if (_loading || _newsAlerts is null)
        {
            return;
        }

        _inWorkspaceChosen = true;
        Saving = SaveInWorkspaceAsync(_newsAlerts, value, Saving);
    }

    private async Task SaveInWorkspaceAsync(INewsAlerts alerts, bool on, Task previous)
    {
        // One write after another, so the last choice made is the one that stays.
        await previous;
        try
        {
            await alerts.SetInWorkspaceAsync(on, CancellationToken.None);
            InWorkspaceNewsStatus = null;
        }
#pragma warning disable CA1031 // As for the sound: reported beside the setting, still applies for this run.
        catch (Exception)
#pragma warning restore CA1031
        {
            InWorkspaceNewsStatus = UiText.NewsInWorkspaceSaveFailed;
        }
    }

    private async Task SaveSoundAsync(INewsAlerts alerts, bool on, Task previous)
    {
        // One write after another, so the last choice made is the one that stays.
        await previous;
        try
        {
            await alerts.SetSoundAsync(on, CancellationToken.None);
            NewsSoundStatus = null;
        }
#pragma warning disable CA1031 // As for the appearance: reported beside the setting, still applies for this run.
        catch (Exception)
#pragma warning restore CA1031
        {
            NewsSoundStatus = UiText.NewsSoundSaveFailed;
        }
    }

    private void Choose(bool chosen, Appearance appearance)
    {
        if (chosen)
        {
            _chosen = true;
            Appearance = appearance;
        }
    }

    private async Task SaveAsync(Appearance value)
    {
        try
        {
            await _appearanceService.SetAsync(value, CancellationToken.None);
            AppearanceStatus = null;
        }
#pragma warning disable CA1031 // A lost appearance choice is reported beside the setting; it still applies for this run.
        catch (Exception)
#pragma warning restore CA1031
        {
            AppearanceStatus = UiText.AppearanceSaveFailed;
        }
    }

    private async Task LoadAsync()
    {
        Appearance stored;
        try
        {
            stored = await _appearanceService.GetAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Unreadable means the default, as at startup.
        catch (Exception)
#pragma warning restore CA1031
        {
            stored = Appearance.FollowSystem;
        }

        var sound = false;
        var inWorkspace = true;
        if (_newsAlerts is not null)
        {
            try
            {
                sound = await _newsAlerts.GetSoundAsync(CancellationToken.None);
            }
#pragma warning disable CA1031 // Unreadable means off, the default.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            try
            {
                inWorkspace = await _newsAlerts.GetInWorkspaceAsync(CancellationToken.None);
            }
#pragma warning disable CA1031 // Unreadable means on, the default.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        // A choice the reader made while this was loading wins over the stored one it would otherwise overwrite.
        _loading = true;
        if (!_chosen)
        {
            Appearance = stored;
        }

        if (!_soundChosen)
        {
            PlaysNewsSound = sound;
        }

        if (!_inWorkspaceChosen)
        {
            ShowsInWorkspaceNews = inWorkspace;
        }

        _loading = false;
    }
}

/// <summary>Where data lives and which migration backups exist. Read-only; restoring is offered only on the recovery page.</summary>
public sealed partial class StorageDetailsViewModel : PageViewModel
{
    private readonly ISystemShell _shell;

    public StorageDetailsViewModel(AppEnvironment environment, IReadOnlyList<BackupInfo> backups, ISystemShell shell)
        : base(StorageDetailsRoute.Instance)
    {
        Environment = environment;
        Backups = new ObservableCollection<BackupInfo>(backups);
        _shell = shell;
    }

    public AppEnvironment Environment { get; }

    public ObservableCollection<BackupInfo> Backups { get; }

    public bool HasBackups => Backups.Count > 0;

    public override string Title => UiText.StorageTitle;

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(Environment.DataDirectory);
}
