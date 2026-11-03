using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Preferences;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels;

/// <summary>The workbench: sidebar (workspaces, sections) and the content area. State lives in <see cref="Navigation.Workbench"/>.</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly IWorkspaces _workspaces;
    private readonly LayoutPromptViewModel? _layoutPrompt;
    private readonly Network.NetworkSessions? _networks;
    private readonly ISidebarLayout? _sidebarLayout;
    private readonly IWorkspaceDeletionPrompt? _workspaceDeletion;
    private PageViewModel? _observedPage;
    private double _sidebarWidth = SidebarLayout.DefaultWidth;

    public ShellViewModel(
        Workbench workbench,
        IWorkspaces workspaces,
        Conversations.ConversationListViewModel conversations,
        LayoutPromptViewModel? layoutPrompt = null,
        Network.NetworkSessions? networks = null,
        ISidebarLayout? sidebarLayout = null,
        IWorkspaceDeletionPrompt? workspaceDeletion = null)
    {
        _workspaceDeletion = workspaceDeletion;
        _layoutPrompt = layoutPrompt;
        _networks = networks;
        _sidebarLayout = sidebarLayout;
        ArgumentNullException.ThrowIfNull(workbench);
        Workbench = workbench;
        Conversations = conversations;
        _workspaces = workspaces;
        NewWorkspace = new WorkspaceNameForm(CreateWorkspaceAsync);
        Workbench.PropertyChanged += OnWorkbenchChanged;
        Conversations.PropertyChanged += OnConversationsChanged;
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

    /// <summary>The sidebar's width while expanded; always within <see cref="SidebarLayout"/>'s bounds.</summary>
    public double SidebarWidth
    {
        get => _sidebarWidth;
        set => SetProperty(ref _sidebarWidth, SidebarLayout.ClampWidth(value));
    }

    /// <summary>Collapsed, the content takes the whole window and an expand button sits in the page header.</summary>
    [ObservableProperty]
    public partial bool IsSidebarCollapsed { get; private set; }

    /// <summary>The expand button's tooltip also says when a conversation needs attention or has a new answer, since the tree is out of sight.</summary>
    public string ExpandSidebarHint =>
        Conversations.HasAttention ? UiText.ExpandSidebarAttention : Conversations.ShowsUnread ? UiText.ExpandSidebarUnread : UiText.ExpandSidebarTip;

    /// <summary>The expand button's automation name: the same news as <see cref="ExpandSidebarHint"/>, without the shortcut.</summary>
    public string ExpandSidebarName =>
        Conversations.HasAttention ? UiText.ExpandSidebar + " · " + UiText.SidebarNeedsAttention
        : Conversations.ShowsUnread ? UiText.ExpandSidebar + " · " + UiText.MarkerNewAnswer
        : UiText.ExpandSidebar;

    /// <summary>Completes when the last layout change has been written (or failed); tests await it.</summary>
    internal Task SidebarSaving { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        SaveSidebar();
    }

    /// <summary>Called when a resize ends (the splitter is released): the width is kept once, not at every pointer move.</summary>
    public void CommitSidebarWidth() => SaveSidebar();

    /// <summary>Applies the stored layout; called at start once the database is open, before the workbench shows.</summary>
    public async Task RestoreSidebarAsync()
    {
        if (_sidebarLayout is null)
        {
            return;
        }

        SidebarLayout stored;
        try
        {
            stored = await _sidebarLayout.GetAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // The layout is cosmetic: unreadable means the default, as for the appearance.
        catch (Exception)
#pragma warning restore CA1031
        {
            stored = SidebarLayout.Default;
        }

        SidebarWidth = stored.Width;
        IsSidebarCollapsed = stored.IsCollapsed;
    }

    private void SaveSidebar()
    {
        if (_sidebarLayout is null)
        {
            return;
        }

        // One write after another, so the last change made is the one that stays.
        var layout = new SidebarLayout(SidebarWidth, IsSidebarCollapsed);
        var previous = SidebarSaving;
        SidebarSaving = SaveAsync();

        async Task SaveAsync()
        {
            await previous;
            try
            {
                await _sidebarLayout.SetAsync(layout, CancellationToken.None);
            }
#pragma warning disable CA1031 // A lost layout is not worth an error: the sidebar stays as it is for this run.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

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

    /// <summary>From the workspace picker's context menu: asks to delete the workspace shown (S7 round 4).</summary>
    [RelayCommand]
    private void DeleteWorkspace()
    {
        if (Workbench.CurrentWorkspace is { } workspace)
        {
            _workspaceDeletion?.Open(workspace);
        }
    }

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

    private void OnConversationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Conversations.HasAttention) or nameof(Conversations.ShowsUnread))
        {
            OnPropertyChanged(nameof(ExpandSidebarHint));
            OnPropertyChanged(nameof(ExpandSidebarName));
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
