using System.Collections.ObjectModel;
using Avalonia.Threading;
using Mindweave.Application.Navigation;
using Mindweave.Application.Storage;
using Mindweave.Application.Chat;
using Mindweave.Core.Workspaces;
using Mindweave.Core.Chat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Mindweave.Desktop.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IWorkspaceStore store;
    private readonly IChatStore? chatStore;
    private readonly GenerationCoordinator? coordinator;
    private readonly ChatConfigurationService? configuration;
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly Dictionary<Guid, WorkspaceRuntimeState> sessions = [];
    private bool closing, localPersistenceFailure, detached;
    private readonly Dictionary<Guid, ConversationDraft> unsavedDrafts = [];
    private WorkspaceSelection? pendingSelection;
    public MainViewModel(IWorkspaceStore store, IMenuNavigationService menu, IContentNavigationService content,
        GenerationCoordinator? coordinator = null, ChatConfigurationService? configuration = null)
    {
        this.store = store; chatStore = store as IChatStore; this.coordinator = coordinator; this.configuration = configuration;
        CreateWorkspaceCommand = new AsyncRelayCommand(CreateWorkspaceAsync);
        SwitchWorkspaceCommand = new AsyncRelayCommand<Workspace>(workspace => workspace is null ? Task.CompletedTask : SwitchWorkspaceAsync(workspace.Id));
        ExploreCommand = new AsyncRelayCommand(() => menu.NavigateAsync(MainArea.Explore));
        NetworkCommand = new AsyncRelayCommand(() => menu.NavigateAsync(MainArea.Network));
        SettingsCommand = new AsyncRelayCommand(() => menu.NavigateAsync(MainArea.Settings));
        BackCommand = new AsyncRelayCommand(content.GoBackAsync);
        NewConversationCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            RequireMutation(); if (CurrentWorkspace is null || chatStore is null || coordinator is null) return;
            await FlushCurrentAsync();
            var conversation = await chatStore.CreateConversationAsync(CurrentWorkspace.Id);
            await OpenRequestAsync(new(MainArea.Explore, ContentKind.Conversation, conversation.Id));
            await RefreshConversationsAsync();
        }));
        OpenConversationCommand = new AsyncRelayCommand<ConversationItemViewModel>(item => item is null ? Task.CompletedTask : NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, item.Id)));
        RetrySaveCommand = new AsyncRelayCommand(async () => { try { await RetryPersistenceAsync(); } catch { /* ErrorMessage retains the failure; keep the window available. */ } });
        if (coordinator is not null) coordinator.Changed += CoordinatorChanged;
    }
    public ObservableCollection<Workspace> Workspaces { get; } = [];
    public ObservableCollection<ContentViewModel> ContentStack { get; } = [];
    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];
    [ObservableProperty] private Workspace? currentWorkspace;
    [ObservableProperty] private MainArea currentArea;
    [ObservableProperty] private string newWorkspaceName = "";
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private bool isBusy;
    public bool IsInteractionEnabled => !IsBusy && !closing;
    public bool CanMutate => IsInteractionEnabled && !HasPersistenceFailure;
    public bool HasPersistenceFailure => localPersistenceFailure || coordinator?.HasPersistenceFailure == true;
    public bool IsClosing => closing;
    public bool HasPendingChatAcceptance => ActiveContent is ChatViewModel { IsAccepting: true };
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasWorkspace => CurrentWorkspace is not null;
    public bool CanGoBack => ContentStack.Count > 1;
    public bool ShowConversations => HasWorkspace && CurrentArea == MainArea.Explore && coordinator is not null;
    public string WorkspaceHeading => CurrentWorkspace?.Name ?? "你的探索，从一个主题开始";
    public ContentViewModel? ActiveContent => ContentStack.LastOrDefault();
    public bool NeedsWorkspace => CurrentWorkspace is null;
    public IAsyncRelayCommand CreateWorkspaceCommand { get; }
    public IAsyncRelayCommand<Workspace> SwitchWorkspaceCommand { get; }
    public IAsyncRelayCommand ExploreCommand { get; }
    public IAsyncRelayCommand NetworkCommand { get; }
    public IAsyncRelayCommand SettingsCommand { get; }
    public IAsyncRelayCommand BackCommand { get; }
    public IAsyncRelayCommand NewConversationCommand { get; }
    public IAsyncRelayCommand<ConversationItemViewModel> OpenConversationCommand { get; }
    public IAsyncRelayCommand RetrySaveCommand { get; }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsInteractionEnabled)); OnPropertyChanged(nameof(CanMutate)); }
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnCurrentWorkspaceChanged(Workspace? value)
    { OnPropertyChanged(nameof(HasWorkspace)); OnPropertyChanged(nameof(NeedsWorkspace)); OnPropertyChanged(nameof(WorkspaceHeading)); OnPropertyChanged(nameof(ShowConversations)); }
    partial void OnCurrentAreaChanged(MainArea value) => OnPropertyChanged(nameof(ShowConversations));
    public Task InitializeAsync() => RunAsync(async () =>
    {
        var workspaces = await store.ListAsync(); var saved = await store.LoadSelectionAsync();
        Workspaces.Clear(); foreach (var workspace in workspaces) Workspaces.Add(workspace);
        var target = workspaces.FirstOrDefault(w => w.Id == saved?.WorkspaceId) ?? workspaces.FirstOrDefault();
        if (target is null) return;
        var area = target.Id == saved?.WorkspaceId ? saved.Area : MainArea.Explore;
        CurrentWorkspace = target; sessions[target.Id] = new() { ActiveArea = area }; await ActivateAsync(area);
        if (saved is { Kind: ContentKind.Conversation, TargetId: { } id } && chatStore is not null && coordinator is not null &&
            (await chatStore.LoadConversationAsync(id))?.WorkspaceId == target.Id)
            await AddDetailAsync(new(MainArea.Explore, ContentKind.Conversation, id, NavigationIntent.Restore));
        await RefreshConversationsAsync();
    });
    public Task CreateWorkspaceAsync() => RunAsync(async () =>
    {
        RequireMutation(); await FlushCurrentAsync();
        var workspace = await store.CreateAsync(NewWorkspaceName);
        Detach(); Workspaces.Add(workspace); CurrentWorkspace = workspace; sessions[workspace.Id] = new();
        await ActivateAsync(MainArea.Explore); await RefreshConversationsAsync(); NewWorkspaceName = "";
    });
    public Task SwitchWorkspaceAsync(Guid id) => RunAsync(async () =>
    {
        var target = Workspaces.FirstOrDefault(w => w.Id == id) ?? throw new ArgumentException("找不到该主题工作区。");
        if (CurrentWorkspace?.Id == id) return;
        await FlushForNavigationAsync();
        var state = sessions.GetValueOrDefault(id) ?? new();
        var destination = state.InactiveHistories.GetValueOrDefault(state.ActiveArea)?.LastOrDefault() ?? new(state.ActiveArea);
        await SaveSelectionAsync(id, destination);
        Detach(); sessions[id] = state; CurrentWorkspace = target; await ActivateAsync(state.ActiveArea); await RefreshConversationsAsync();
    });
    public Task NavigateAsync(NavigationRequest request) => RunAsync(async () =>
    {
        if (CurrentWorkspace is null) return;
        await FlushForNavigationAsync(); await OpenRequestAsync(request);
    });
    private async Task OpenRequestAsync(NavigationRequest request)
    {
        Validate(request);
        if (request.Kind == ContentKind.Conversation && chatStore is not null &&
            (await chatStore.LoadConversationAsync(request.TargetId!.Value))?.WorkspaceId != CurrentWorkspace!.Id)
            throw new ArgumentException("会话不属于当前工作区。");
        var effective = request;
        if (request.Kind == ContentKind.Overview && CurrentArea != request.Area &&
            sessions[CurrentWorkspace!.Id].InactiveHistories.TryGetValue(request.Area, out var history)) effective = history[^1];
        await SaveSelectionAsync(CurrentWorkspace!.Id, effective);
        if (CurrentArea != request.Area) { Detach(); await ActivateAsync(request.Area); }
        if (request.Kind != ContentKind.Overview) await AddDetailAsync(request);
        NotifyContent();
    }
    private async Task AddDetailAsync(NavigationRequest request)
    {
        if (ActiveContent?.Request is { } previous && previous.Kind == request.Kind && previous.TargetId == request.TargetId) ActiveContent.SetParameter(request);
        else { DeactivatePage(); ContentStack.Add(await CreateContentAsync(request)); }
        NotifyContent();
    }
    public Task GoBackAsync() => RunAsync(async () =>
    {
        await FlushForNavigationAsync();
        if (ContentStack.Count > 1)
        {
            await SaveSelectionAsync(CurrentWorkspace!.Id, ContentStack[^2].Request);
            DeactivatePage(); ContentStack.RemoveAt(ContentStack.Count - 1);
            // Pages lower in the stack are snapshots too: reload on activation to project latest memory/draft.
            var request = ActiveContent!.Request; ContentStack[^1] = await CreateContentAsync(request);
        }
        NotifyContent();
    });
    private async Task SaveSelectionAsync(Guid workspaceId, NavigationRequest request)
    {
        var selection = new WorkspaceSelection(workspaceId, request.Area,
            request.Kind == ContentKind.Conversation ? request.Kind : ContentKind.Overview,
            request.Kind == ContentKind.Conversation ? request.TargetId : null);
        try { await store.SaveSelectionAsync(selection); pendingSelection = null; }
        catch (Exception error) when (error is not ArgumentException)
        { pendingSelection = selection; MarkPersistenceFailure(); ErrorMessage = "阅读位置尚未保存；可继续阅读，请重试保存。"; }
    }
    public async Task PrepareCloseAsync()
    {
        closing = true; NotifyAvailability(); await changes.WaitAsync();
        changes.Release();
        if (ActiveContent is not null) await ActiveContent.WaitForPendingOperationsAsync();
    }
    public async Task SaveDraftForCloseAsync()
    {
        try { await FlushAllDraftsAsync(); if (pendingSelection is not null) { await store.SaveSelectionAsync(pendingSelection); pendingSelection = null; } }
        catch { MarkPersistenceFailure(); throw; }
    }
    public void ReturnToWorkbench() { closing = false; coordinator?.ResumeAccepting(); NotifyAvailability(); }
    public async Task RetryPersistenceAsync()
    {
        try
        {
            await FlushAllDraftsAsync();
            if (pendingSelection is not null) { await store.SaveSelectionAsync(pendingSelection); pendingSelection = null; }
            if (coordinator is not null) await coordinator.RetryPersistenceAsync();
            localPersistenceFailure = false; ErrorMessage = null;
        }
        catch { localPersistenceFailure = true; ErrorMessage = "保存仍未完成，内容保留在窗口中。"; throw; }
        finally { NotifyAvailability(); }
    }
    private async Task RunAsync(Func<Task> action)
    {
        await changes.WaitAsync();
        try { if (closing) return; IsBusy = true; ErrorMessage = null; await action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { ErrorMessage = error.Message; }
        catch { MarkPersistenceFailure(); ErrorMessage = "保存或读取失败，当前内容与输入已保留。请重试保存。"; }
        finally { IsBusy = false; changes.Release(); }
    }
    private void RequireMutation() { if (HasPersistenceFailure) throw new InvalidOperationException("保存失败，请先重试保存。"); }
    private async Task FlushCurrentAsync()
    {
        if (ActiveContent is not null) await ActiveContent.WaitForPendingOperationsAsync();
        if (ActiveContent is not ChatViewModel page) return;
        await page.FlushDraftAsync(); unsavedDrafts.Remove(page.ConversationId);
    }
    private async Task FlushForNavigationAsync()
    {
        try { await FlushCurrentAsync(); }
        catch
        {
            if (ActiveContent is ChatViewModel page) unsavedDrafts[page.ConversationId] = page.CurrentDraft;
            MarkPersistenceFailure(); ErrorMessage = "草稿仍在内存中。可继续阅读，请重试保存。";
        }
    }
    private async Task FlushAllDraftsAsync()
    {
        await FlushCurrentAsync();
        if (chatStore is null) return;
        foreach (var (id, draft) in unsavedDrafts.ToArray())
        {
            await chatStore.SaveDraftAsync(id, draft.Text, draft.Revision); unsavedDrafts.Remove(id);
        }
    }
    private void DeactivatePage() { if (ActiveContent is ChatViewModel page) page.Detach(); }
    private void Detach()
    {
        if (CurrentWorkspace is not null) sessions[CurrentWorkspace.Id].InactiveHistories[CurrentArea] = ContentStack.Select(page => page.Request).ToArray();
        DeactivatePage(); ContentStack.Clear();
    }
    private async Task ActivateAsync(MainArea area)
    {
        var state = sessions[CurrentWorkspace!.Id]; state.ActiveArea = area; CurrentArea = area; ContentStack.Clear();
        if (state.InactiveHistories.Remove(area, out var history))
            foreach (var request in history) ContentStack.Add(await CreateContentAsync(request));
        if (ContentStack.Count == 0) ContentStack.Add(await CreateContentAsync(new(area)));
        NotifyContent();
    }
    private void NotifyContent()
    { OnPropertyChanged(nameof(ActiveContent)); OnPropertyChanged(nameof(CanGoBack)); foreach (var item in Conversations) item.IsCurrent = item.Id == ActiveContent?.Request.TargetId; }
    private async Task<ContentViewModel> CreateContentAsync(NavigationRequest request)
    {
        ContentViewModel page;
        if (coordinator is not null && chatStore is not null && request.Kind == ContentKind.Conversation)
        {
            var chat = new ChatViewModel(request.TargetId!.Value, chatStore, coordinator, () => HasPersistenceFailure || closing, MarkPersistenceFailure);
            await chat.InitializeAsync(); if (unsavedDrafts.TryGetValue(chat.ConversationId, out var draft)) chat.RestoreDraft(draft); page = chat;
        }
        else if (request.Area == MainArea.Settings && chatStore is not null && configuration is not null)
        {
            var settings = new SettingsViewModel(chatStore, configuration, () => HasPersistenceFailure || closing, MarkPersistenceFailure);
            await settings.InitializeAsync(); page = settings;
        }
        else page = new();
        page.SetParameter(request); return page;
    }
    private async Task RefreshConversationsAsync()
    {
        if (chatStore is null || CurrentWorkspace is null) return;
        var workspace = CurrentWorkspace.Id; var items = await chatStore.ListConversationsAsync(workspace);
        if (CurrentWorkspace?.Id != workspace || detached) return;
        foreach (var conversation in items)
        {
            var item = Conversations.FirstOrDefault(i => i.Id == conversation.Id);
            if (item is null) Conversations.Add(new(conversation)); else item.Title = conversation.Title;
            if (ActiveContent is ChatViewModel active && active.ConversationId == conversation.Id) active.RefreshTitle(conversation.Title);
        }
        foreach (var item in Conversations.Where(i => !items.Any(c => c.Id == i.Id)).ToArray()) Conversations.Remove(item);
        NotifyContent();
    }
    private void CoordinatorChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (detached) return;
        (ActiveContent as ChatViewModel)?.Project(); NotifyAvailability();
        _ = RefreshTitlesSafelyAsync();
    });
    private async Task RefreshTitlesSafelyAsync() { try { await RefreshConversationsAsync(); } catch { /* Reading old content remains possible. */ } }
    private void MarkPersistenceFailure() { localPersistenceFailure = true; NotifyAvailability(); }
    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(HasPersistenceFailure)); OnPropertyChanged(nameof(CanMutate)); OnPropertyChanged(nameof(IsClosing)); OnPropertyChanged(nameof(IsInteractionEnabled));
        (ActiveContent as ChatViewModel)?.Project(); (ActiveContent as SettingsViewModel)?.RefreshAvailability();
    }
    public void DetachObservers() { detached = true; DeactivatePage(); if (coordinator is not null) coordinator.Changed -= CoordinatorChanged; }
    private static void Validate(NavigationRequest request)
    {
        if (!Enum.IsDefined(request.Area) || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Intent) ||
            (request.Kind == ContentKind.Overview ? request.TargetId is not null : request.TargetId is null || request.TargetId == Guid.Empty) ||
            (request.Kind == ContentKind.Conversation && request.Area != MainArea.Explore) || (request.Kind == ContentKind.Summary && request.Area != MainArea.Network))
            throw new ArgumentException("导航目标或参数无效。");
    }
    private sealed class WorkspaceRuntimeState
    { public MainArea ActiveArea { get; set; } = MainArea.Explore; public Dictionary<MainArea, NavigationRequest[]> InactiveHistories { get; } = []; }
}
