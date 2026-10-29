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
    private readonly AppearanceService _appearanceService;
    private readonly IThemeApplier _theme;
    private bool _loading;

    public SettingsHomeViewModel(AppEnvironment environment, ModelSettingsViewModel model, IContentNavigator content, AppearanceService appearance, IThemeApplier theme)
        : base(SettingsHomeRoute.Instance)
    {
        Environment = environment;
        Model = model;
        _content = content;
        _appearanceService = appearance;
        _theme = theme;
        Loaded = LoadAsync();
    }

    public AppEnvironment Environment { get; }

    public ModelSettingsViewModel Model { get; }

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

    [RelayCommand]
    private void ShowStorageDetails() => _content.Push(StorageDetailsRoute.Instance);

    partial void OnAppearanceChanged(Appearance value)
    {
        if (_loading)
        {
            return;
        }

        _theme.Apply(value);
        Saving = SaveAsync(value);
    }

    private void Choose(bool chosen, Appearance appearance)
    {
        if (chosen)
        {
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

        _loading = true;
        Appearance = stored;
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
