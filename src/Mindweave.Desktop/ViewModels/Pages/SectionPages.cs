using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application;
using Mindweave.Application.Startup;
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

/// <summary>Summary network; populated from summaries in S5.</summary>
public sealed class NetworkHomeViewModel(Workspace workspace) : PageViewModel(NetworkHomeRoute.Instance)
{
    public Workspace Workspace { get; } = workspace;

    public override string Title => UiText.NetworkTitle;
}

public sealed partial class SettingsHomeViewModel(AppEnvironment environment, ModelSettingsViewModel model, IContentNavigator content) : PageViewModel(SettingsHomeRoute.Instance)
{
    public AppEnvironment Environment { get; } = environment;

    public ModelSettingsViewModel Model { get; } = model;

    public override string Title => UiText.SettingsTitle;

    [RelayCommand]
    private void ShowStorageDetails() => content.Push(StorageDetailsRoute.Instance);
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
