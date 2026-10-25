using System.Collections.ObjectModel;
using Mindweave.Application.Navigation;
using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mindweave.Desktop.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IWorkspaceStore store;
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly Dictionary<Guid, WorkspaceRuntimeState> sessions = [];
    private bool closing;
    public MainViewModel(IWorkspaceStore store, IMenuNavigationService menu, IContentNavigationService content)
    {
        this.store = store;
        CreateWorkspaceCommand = new AsyncRelayCommand(CreateWorkspaceAsync);
        SwitchWorkspaceCommand = new AsyncRelayCommand<Workspace>(workspace => workspace is null ? Task.CompletedTask : SwitchWorkspaceAsync(workspace.Id));
        ExploreCommand = new AsyncRelayCommand(() => menu.NavigateAsync(MainArea.Explore));
        NetworkCommand = new AsyncRelayCommand(() => menu.NavigateAsync(MainArea.Network));
        SettingsCommand = new AsyncRelayCommand(() => menu.NavigateAsync(MainArea.Settings));
        BackCommand = new AsyncRelayCommand(content.GoBackAsync);
    }
    public ObservableCollection<Workspace> Workspaces { get; } = [];
    public ObservableCollection<ContentViewModel> ContentStack { get; } = [];
    [ObservableProperty] private Workspace? currentWorkspace;
    [ObservableProperty] private MainArea currentArea;
    [ObservableProperty] private string newWorkspaceName = "";
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private bool isBusy;
    public bool IsInteractionEnabled => !IsBusy && !closing;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasWorkspace => CurrentWorkspace is not null;
    public bool CanGoBack => ContentStack.Count > 1;
    public string WorkspaceHeading => CurrentWorkspace?.Name ?? "你的探索，从一个主题开始";
    public ContentViewModel? ActiveContent => ContentStack.LastOrDefault();
    public bool NeedsWorkspace => CurrentWorkspace is null;
    public IAsyncRelayCommand CreateWorkspaceCommand { get; }
    public IAsyncRelayCommand<Workspace> SwitchWorkspaceCommand { get; }
    public IAsyncRelayCommand ExploreCommand { get; }
    public IAsyncRelayCommand NetworkCommand { get; }
    public IAsyncRelayCommand SettingsCommand { get; }
    public IAsyncRelayCommand BackCommand { get; }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsInteractionEnabled));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnCurrentWorkspaceChanged(Workspace? value)
    {
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(NeedsWorkspace));
        OnPropertyChanged(nameof(WorkspaceHeading));
    }

    public Task InitializeAsync() => RunAsync(async () =>
    {
        var workspaces = await store.ListAsync();
        var saved = await store.LoadSelectionAsync();
        Workspaces.Clear();
        foreach (var workspace in workspaces) Workspaces.Add(workspace);
        var target = workspaces.FirstOrDefault(w => w.Id == saved?.WorkspaceId) ?? workspaces.FirstOrDefault();
        if (target is null) return;
        var area = target.Id == saved?.WorkspaceId ? saved.Area : MainArea.Explore;
        CurrentWorkspace = target;
        sessions[target.Id] = new WorkspaceRuntimeState { ActiveArea = area };
        Activate(area);
    });

    public Task CreateWorkspaceAsync() => RunAsync(async () =>
    {
        var workspace = await store.CreateAsync(NewWorkspaceName);
        Detach();
        Workspaces.Add(workspace);
        CurrentWorkspace = workspace;
        sessions[workspace.Id] = new WorkspaceRuntimeState();
        Activate(MainArea.Explore);
        NewWorkspaceName = "";
    });

    public Task SwitchWorkspaceAsync(Guid id) => RunAsync(async () =>
    {
        var target = Workspaces.FirstOrDefault(w => w.Id == id) ?? throw new ArgumentException("找不到该主题工作区。");
        if (CurrentWorkspace?.Id == id) return;
        var state = sessions.GetValueOrDefault(id) ?? new WorkspaceRuntimeState();
        await store.SaveSelectionAsync(new(id, state.ActiveArea));
        Detach();
        sessions[id] = state;
        CurrentWorkspace = target;
        Activate(state.ActiveArea);
    });

    public Task NavigateAsync(NavigationRequest request) => RunAsync(async () =>
    {
        if (CurrentWorkspace is null) return;
        Validate(request);
        await store.SaveSelectionAsync(new(CurrentWorkspace.Id, request.Area));
        if (CurrentArea != request.Area)
        {
            Detach();
            Activate(request.Area);
        }
        if (request.Kind != ContentKind.Overview)
        {
            var previous = ActiveContent?.Request;
            if (previous?.Kind == request.Kind && previous.TargetId == request.TargetId)
                ActiveContent!.SetParameter(request);
            else ContentStack.Add(CreateContent(request));
            NotifyContent();
        }
    });

    public Task GoBackAsync() => RunAsync(() =>
    {
        if (ContentStack.Count > 1) ContentStack.RemoveAt(ContentStack.Count - 1);
        NotifyContent();
        return Task.CompletedTask;
    });

    public async Task PrepareCloseAsync()
    {
        closing = true;
        OnPropertyChanged(nameof(IsInteractionEnabled));
        await changes.WaitAsync();
        changes.Release();
    }

    private async Task RunAsync(Func<Task> action)
    {
        await changes.WaitAsync();
        try
        {
            if (closing) return;
            IsBusy = true;
            ErrorMessage = null;
            await action();
        }
        catch (Exception error) { ErrorMessage = error.Message; }
        finally { IsBusy = false; changes.Release(); }
    }

    private void Detach()
    {
        if (CurrentWorkspace is not null)
            sessions[CurrentWorkspace.Id].InactiveHistories[CurrentArea] = ContentStack.Select(page => page.Request).ToArray();
        ContentStack.Clear();
    }

    private void Activate(MainArea area)
    {
        var state = sessions[CurrentWorkspace!.Id];
        state.ActiveArea = area;
        CurrentArea = area;
        ContentStack.Clear();
        if (state.InactiveHistories.Remove(area, out var history))
            foreach (var request in history) ContentStack.Add(CreateContent(request));
        if (ContentStack.Count == 0) ContentStack.Add(CreateContent(new(area)));
        NotifyContent();
    }

    private void NotifyContent()
    {
        OnPropertyChanged(nameof(ActiveContent));
        OnPropertyChanged(nameof(CanGoBack));
    }

    private static ContentViewModel CreateContent(NavigationRequest request)
    {
        var page = new ContentViewModel();
        page.SetParameter(request);
        return page;
    }

    private static void Validate(NavigationRequest request)
    {
        if (!Enum.IsDefined(request.Area) || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Intent) ||
            (request.Kind == ContentKind.Overview ? request.TargetId is not null : request.TargetId is null || request.TargetId == Guid.Empty) ||
            (request.Kind == ContentKind.Conversation && request.Area != MainArea.Explore) ||
            (request.Kind == ContentKind.Summary && request.Area != MainArea.Network))
            throw new ArgumentException("导航目标或参数无效。");
    }

    private sealed class WorkspaceRuntimeState
    {
        public MainArea ActiveArea { get; set; } = MainArea.Explore;
        public Dictionary<MainArea, NavigationRequest[]> InactiveHistories { get; } = [];
    }
}
