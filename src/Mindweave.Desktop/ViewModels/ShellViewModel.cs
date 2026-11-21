using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Preferences;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

/// <summary>The workbench: sidebar (workspaces, sections) and the content area. State lives in <see cref="Navigation.Workbench"/>.</summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly IWorkspaces _workspaces;
    private readonly LayoutPromptViewModel? _layoutPrompt;
    private readonly SummaryNetwork.SummaryNetworkSessions? _summaryNetworks;
    private readonly ISidebarLayout? _sidebarLayout;
    private readonly IWorkspaceDeletionPrompt? _workspaceDeletion;
    private readonly WorkspaceSwitchRequests? _switches;
    private PageViewModel? _observedPage;
    private double _sidebarWidth = SidebarLayout.DefaultWidth;

    public ShellViewModel(
        Workbench workbench,
        IWorkspaces workspaces,
        Conversations.ConversationListViewModel conversations,
        LayoutPromptViewModel? layoutPrompt = null,
        SummaryNetwork.SummaryNetworkSessions? summaryNetworks = null,
        ISidebarLayout? sidebarLayout = null,
        IWorkspaceDeletionPrompt? workspaceDeletion = null,
        WorkspaceNewsViewModel? workspaceNews = null,
        WorkspaceSwitchRequests? switches = null,
        SummaryNetwork.SessionNetworkGate? sessionNetwork = null)
    {
        SummaryNetworkLabel = sessionNetwork is { Enabled: true } ? UiText.SessionNetworkTitle : UiText.SectionSummaryNetwork;
        WorkspaceNews = workspaceNews;
        _workspaceDeletion = workspaceDeletion;
        _layoutPrompt = layoutPrompt;
        _summaryNetworks = summaryNetworks;
        _sidebarLayout = sidebarLayout;
        ArgumentNullException.ThrowIfNull(workbench);
        Workbench = workbench;
        Conversations = conversations;
        _workspaces = workspaces;
        NewWorkspace = new WorkspaceNameForm(CreateWorkspaceAsync);
        Workbench.PropertyChanged += OnWorkbenchChanged;
        Workbench.Workspaces.CollectionChanged += OnWorkspacesChanged;
        Conversations.PropertyChanged += OnConversationsChanged;
        if (workspaceNews is not null)
        {
            workspaceNews.PropertyChanged += OnNewsChanged;
        }

        _switches = switches;
        if (switches is not null)
        {
            switches.Requested += OnSwitchRequested;
        }
    }

    /// <summary>Stops listening to the workbench, the sidebar, the hints and the switch requests; the view is being replaced.</summary>
    public void Dispose()
    {
        Workbench.PropertyChanged -= OnWorkbenchChanged;
        Workbench.Workspaces.CollectionChanged -= OnWorkspacesChanged;
        Conversations.PropertyChanged -= OnConversationsChanged;
        if (WorkspaceNews is not null)
        {
            WorkspaceNews.PropertyChanged -= OnNewsChanged;
        }

        if (_switches is not null)
        {
            _switches.Requested -= OnSwitchRequested;
        }

        ObservePage(null);
    }

    private void OnWorkspacesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshChoices();

    /// <summary>设置 › 工作区's 切换到此工作区 goes the switcher's way, so an unsaved layout is asked about first.</summary>
    private void OnSwitchRequested(object? sender, Workspace workspace) => SelectedWorkspace = workspace;

    public Workbench Workbench { get; }

    public WorkspaceNameForm NewWorkspace { get; }

    /// <summary>New answers in the other workspaces, for the picker; null where the shell is built without it (tests).</summary>
    public WorkspaceNewsViewModel? WorkspaceNews { get; }

    public Conversations.ConversationListViewModel Conversations { get; }

    public PageViewModel? CurrentPage => Workbench.CurrentPage;

    public string Title => CurrentPage?.Title ?? string.Empty;

    public bool IsExplore => Workbench.CurrentSection == MainSection.Explore;

    public bool IsSummaryNetwork => Workbench.CurrentSection == MainSection.SummaryNetwork;

    /// <summary>The sidebar entry of the network: "Mindweave" behind the session network's gate, "总结网络" without it.</summary>
    public string SummaryNetworkLabel { get; }

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

    /// <summary>The switcher button's accessible name: what it is and which workspace is open.</summary>
    public string SwitcherName => UiText.WorkspaceSwitcherName(Workbench.CurrentWorkspace?.Name.Value);

    /// <summary>The switcher's menu is open (2026-11-10): every workspace, the open one checked, then 新建 and 管理.</summary>
    [ObservableProperty]
    public partial bool IsWorkspaceMenuOpen { get; set; }

    /// <summary>The menu's rows, rebuilt whenever it opens and while open when the list or the open workspace changes.</summary>
    public ObservableCollection<WorkspaceChoice> WorkspaceChoices { get; } = [];

    /// <summary>The inline new-workspace form under the switcher is open (the view moves the focus into its box).</summary>
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

    /// <summary>
    /// After an in-app restore (2026-11-12): "已恢复到 … 的备份", or why it did not happen. Above the page, never a dialog,
    /// until closed.
    /// </summary>
    [ObservableProperty]
    public partial string? RestoreNotice { get; private set; }

    [ObservableProperty]
    public partial bool RestoreNoticeIsError { get; private set; }

    public void ShowRestoreNotice(string text, bool isError)
    {
        RestoreNoticeIsError = isError;
        RestoreNotice = text;
    }

    [RelayCommand]
    private void CloseRestoreNotice() => RestoreNotice = null;

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
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            stored = SidebarLayout.Default;
        }

        UseSidebar(stored);
    }

    /// <summary>The sidebar as <paramref name="layout"/> says, without saving it (a stored layout, or the old view's after a switch).</summary>
    public void UseSidebar(SidebarLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        SidebarWidth = layout.Width;
        IsSidebarCollapsed = layout.IsCollapsed;
    }

    /// <summary>The sidebar as it is now.</summary>
    public SidebarLayout Sidebar => new(SidebarWidth, IsSidebarCollapsed);

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
            catch (Exception swallowed)
#pragma warning restore CA1031
            {
                DesktopLog.Swallowed(swallowed);
            }
        }
    }

    [RelayCommand]
    private void ShowSection(MainSection section)
    {
        // Entering the network from its section entry fits everything into view; coming back from a summary does not.
        if (section == MainSection.SummaryNetwork && Workbench.CurrentWorkspace is { } workspace && _summaryNetworks is not null)
        {
            _summaryNetworks.Get(workspace.Id).Viewport = null;
        }

        Workbench.Navigate(new NavigationRequest(section));
    }

    public bool CanGoBack => Workbench.CanGoBack;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Workbench.GoBack();

    [RelayCommand]
    private void ToggleCreateWorkspace() => IsCreatingWorkspace = !IsCreatingWorkspace;

    /// <summary>A row of the switcher's menu: closes it and switches the way <see cref="SelectedWorkspace"/> does.</summary>
    [RelayCommand]
    private void ChooseWorkspace(WorkspaceChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        IsWorkspaceMenuOpen = false;
        SelectedWorkspace = choice.Workspace;
    }

    /// <summary>The menu's 新建工作区…: the form under the switcher opens (no dialog: the switcher stays in view).</summary>
    [RelayCommand]
    private void BeginCreateWorkspace()
    {
        IsWorkspaceMenuOpen = false;
        IsCreatingWorkspace = true;
    }

    /// <summary>Esc or 取消 in the form: it closes and forgets what was typed.</summary>
    [RelayCommand]
    private void CancelCreateWorkspace()
    {
        IsCreatingWorkspace = false;
        NewWorkspace.Reset();
    }

    /// <summary>The menu's 管理工作区…: 设置 › 工作区, where every workspace is listed.</summary>
    [RelayCommand]
    private void ManageWorkspaces()
    {
        IsWorkspaceMenuOpen = false;
        Workbench.Navigate(new NavigationRequest(MainSection.Settings, WorkspacesRoute.Instance));
    }

    partial void OnIsWorkspaceMenuOpenChanged(bool value) => RefreshChoices();

    private void RefreshChoices()
    {
        if (!IsWorkspaceMenuOpen)
        {
            return;
        }

        var current = Workbench.CurrentWorkspace?.Id;
        var news = WorkspaceNews?.Workspaces;
        WorkspaceChoices.Clear();
        foreach (var workspace in Workbench.Workspaces)
        {
            WorkspaceChoices.Add(new WorkspaceChoice(workspace, workspace.Id == current, news?.Contains(workspace.Id) == true, ChooseWorkspaceCommand));
        }
    }

    private void OnNewsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceNewsViewModel.Workspaces) && WorkspaceNews is { } news)
        {
            foreach (var choice in WorkspaceChoices)
            {
                choice.HasNews = news.Workspaces.Contains(choice.Workspace.Id);
            }
        }
    }

    /// <summary>Asks to delete the workspace shown (S7 round 4); the entries are now on 设置 › 工作区, one per workspace.</summary>
    [RelayCommand]
    private void DeleteWorkspace()
    {
        if (Workbench.CurrentWorkspace is { } workspace)
        {
            _workspaceDeletion?.Open(workspace);
        }
    }

    /// <summary>
    /// A click on a pop-up hint (2026-11-10): switches to its workspace the same way the picker does (unless it is open),
    /// and opens the conversation when exactly one there has a new answer, which a conversation's hint always has; with
    /// several the sidebar shows which. The hint goes when its workspace, or its conversation, opens
    /// (<see cref="WorkspaceNewsViewModel"/>).
    /// </summary>
    [RelayCommand]
    private void OpenWorkspaceNews(WorkspaceToastViewModel toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        if (Workbench.Workspaces.FirstOrDefault(w => w.Id == toast.Workspace) is not { } workspace)
        {
            WorkspaceNews?.Dismiss(toast);
            return;
        }

        ConversationId? only = toast.Conversations.Count == 1 ? toast.Conversations[0] : null;
        void Open()
        {
            Workbench.SwitchWorkspace(workspace.Id);
            if (only is { } conversation && Workbench.CurrentWorkspace?.Id == workspace.Id)
            {
                Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(conversation)));
            }
        }

        if (workspace.Id == Workbench.CurrentWorkspace?.Id)
        {
            Open();
            WorkspaceNews?.Dismiss(toast);
            return;
        }

        Leave(Open);
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
            if (current is not null && _summaryNetworks is not null)
            {
                _summaryNetworks.Get(current.Id).Viewport = null;
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
                OnPropertyChanged(nameof(IsSummaryNetwork));
                OnPropertyChanged(nameof(IsSettings));
                break;
            case nameof(Workbench.CanGoBack):
                OnPropertyChanged(nameof(CanGoBack));
                GoBackCommand.NotifyCanExecuteChanged();
                break;
            case nameof(Workbench.CurrentWorkspace):
                OnPropertyChanged(nameof(SelectedWorkspace));
                OnPropertyChanged(nameof(SwitcherName));
                RefreshChoices();
                break;
        }
    }
}
