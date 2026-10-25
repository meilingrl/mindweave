using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels;

/// <summary>The workbench: sidebar (workspaces, sections) and the content area. State lives in <see cref="Navigation.Workbench"/>.</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly WorkspaceService _workspaces;

    public ShellViewModel(Workbench workbench, WorkspaceService workspaces)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        Workbench = workbench;
        _workspaces = workspaces;
        NewWorkspace = new WorkspaceNameForm(CreateWorkspaceAsync);
        Workbench.PropertyChanged += OnWorkbenchChanged;
    }

    public Workbench Workbench { get; }

    public WorkspaceNameForm NewWorkspace { get; }

    public PageViewModel? CurrentPage => Workbench.CurrentPage;

    public string Title => CurrentPage?.Title ?? string.Empty;

    public bool IsExplore => Workbench.CurrentSection == MainSection.Explore;

    public bool IsNetwork => Workbench.CurrentSection == MainSection.Network;

    public bool IsSettings => Workbench.CurrentSection == MainSection.Settings;

    /// <summary>Two-way bound to the workspace picker; setting it switches workspace.</summary>
    public Workspace? SelectedWorkspace
    {
        get => Workbench.CurrentWorkspace;
        set
        {
            if (value is not null)
            {
                Workbench.SwitchWorkspace(value.Id);
            }
        }
    }

    [ObservableProperty]
    public partial bool IsCreatingWorkspace { get; set; }

    [RelayCommand]
    private void ShowSection(MainSection section) => Workbench.Navigate(new NavigationRequest(section));

    public bool CanGoBack => Workbench.CanGoBack;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Workbench.GoBack();

    [RelayCommand]
    private void ToggleCreateWorkspace() => IsCreatingWorkspace = !IsCreatingWorkspace;

    private async Task CreateWorkspaceAsync(WorkspaceName name)
    {
        var workspace = await _workspaces.CreateAsync(name, CancellationToken.None);
        Workbench.AddWorkspace(workspace);
        IsCreatingWorkspace = false;
    }

    private void OnWorkbenchChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Workbench.CurrentPage):
                OnPropertyChanged(nameof(CurrentPage));
                OnPropertyChanged(nameof(Title));
                break;
            case nameof(Workbench.CurrentSection):
                OnPropertyChanged(nameof(IsExplore));
                OnPropertyChanged(nameof(IsNetwork));
                OnPropertyChanged(nameof(IsSettings));
                break;
            case nameof(Workbench.CanGoBack):
                OnPropertyChanged(nameof(CanGoBack));
                GoBackCommand.NotifyCanExecuteChanged();
                break;
            case nameof(Workbench.CurrentWorkspace):
                OnPropertyChanged(nameof(SelectedWorkspace));
                break;
        }
    }
}
