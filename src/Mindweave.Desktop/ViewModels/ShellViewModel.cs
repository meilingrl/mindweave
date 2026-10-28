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
    private readonly LayoutPromptViewModel? _layoutPrompt;
    private readonly Network.NetworkSessions? _networks;
    private PageViewModel? _observedPage;

    public ShellViewModel(
        Workbench workbench,
        WorkspaceService workspaces,
        Conversations.ConversationListViewModel conversations,
        LayoutPromptViewModel? layoutPrompt = null,
        Network.NetworkSessions? networks = null)
    {
        _layoutPrompt = layoutPrompt;
        _networks = networks;
        ArgumentNullException.ThrowIfNull(workbench);
        Workbench = workbench;
        Conversations = conversations;
        _workspaces = workspaces;
        NewWorkspace = new WorkspaceNameForm(CreateWorkspaceAsync);
        Workbench.PropertyChanged += OnWorkbenchChanged;
    }

    public Workbench Workbench { get; }

    public WorkspaceNameForm NewWorkspace { get; }

    public Conversations.ConversationListViewModel Conversations { get; }

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
            if (value is not null && value.Id != Workbench.CurrentWorkspace?.Id)
            {
                Leave(() => Workbench.SwitchWorkspace(value.Id));

                // If leaving waits for an answer, the picker shows the workspace still active.
                OnPropertyChanged(nameof(SelectedWorkspace));
            }
        }
    }

    [ObservableProperty]
    public partial bool IsCreatingWorkspace { get; set; }

    [RelayCommand]
    private void ShowSection(MainSection section)
    {
        // Entering the network from its section entry fits everything into view; coming back from a summary does not.
        if (section == MainSection.Network && Workbench.CurrentWorkspace is { } workspace && _networks is not null)
        {
            _networks.Get(workspace.Id).Viewport = null;
        }

        Workbench.Navigate(new NavigationRequest(section));
    }

    public bool CanGoBack => Workbench.CanGoBack;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Workbench.GoBack();

    [RelayCommand]
    private void ToggleCreateWorkspace() => IsCreatingWorkspace = !IsCreatingWorkspace;

    private async Task CreateWorkspaceAsync(WorkspaceName name)
    {
        var workspace = await _workspaces.CreateAsync(name, CancellationToken.None);
        IsCreatingWorkspace = false;

        // The new workspace is listed at once; switching to it waits if the current layout is unsaved.
        Workbench.Workspaces.Add(workspace);
        Leave(() => Workbench.SwitchWorkspace(workspace.Id));
    }

    /// <summary>
    /// Leaves the current workspace: an unsaved network layout is asked about first (§7.4). The viewport is not kept:
    /// coming back fits everything into view (§11.3).
    /// </summary>
    private void Leave(Action switchTo)
    {
        var current = Workbench.CurrentWorkspace;
        void Go()
        {
            if (current is not null && _networks is not null)
            {
                _networks.Get(current.Id).Viewport = null;
            }

            switchTo();
            OnPropertyChanged(nameof(SelectedWorkspace));
        }

        if (current is null || _layoutPrompt?.HoldLeaving(current.Id, Go) != true)
        {
            Go();
        }
    }

    /// <summary>Page titles can change after the page is shown (loaded, auto-titled, renamed).</summary>
    private void ObservePage(PageViewModel? page)
    {
        if (ReferenceEquals(page, _observedPage))
        {
            return;
        }

        if (_observedPage is not null)
        {
            _observedPage.PropertyChanged -= OnPageChanged;
        }

        _observedPage = page;
        if (page is not null)
        {
            page.PropertyChanged += OnPageChanged;
        }
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModel.Title))
        {
            OnPropertyChanged(nameof(Title));
        }
    }

    private void OnWorkbenchChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Workbench.CurrentPage):
                ObservePage(Workbench.CurrentPage);
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
