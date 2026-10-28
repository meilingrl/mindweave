using Mindweave.Application.Storage;
using Mindweave.Application.Chat;
using Mindweave.Desktop.Services;
using Mindweave.Application.Navigation;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Network;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Mindweave.Desktop.ViewModels;

public sealed partial class RootViewModel : ViewModelBase
{
    private readonly Func<Task<IWorkspaceStore>> openStore;
    private readonly Func<IWorkspaceStore, DesktopChatSession>? chatFactory;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly IRootNavigationService navigation;
    private readonly StartupViewModel startup;
    private IWorkspaceStore? store;
    private MainViewModel? main;
    private DesktopChatSession? chat;
    private bool closing, confirmed;
    private Task? closeAttempt;
    private LayoutLeavePermit? layoutPermit;
    [ObservableProperty] private ViewModelBase content;
    [ObservableProperty] private string? closeError;
    [ObservableProperty] private bool isCloseConfirmationVisible;
    [ObservableProperty] private bool isClosed;
    [ObservableProperty] private bool isClosing;
    public bool HasCloseError => CloseError is not null;
    public IRelayCommand CancelCloseCommand { get; }
    public IRelayCommand ReturnToWorkbenchCommand { get; }
    public IAsyncRelayCommand ConfirmCloseCommand { get; }
    public IAsyncRelayCommand RetrySaveCommand { get; }
    public IAsyncRelayCommand RetryCloseCommand { get; }
    public event Action? CloseCompleted;
    public RootViewModel(Func<Task<IWorkspaceStore>> openStore, IDesktopActions actions, string dataDirectory, Func<IWorkspaceStore, DesktopChatSession>? chatFactory = null)
    {
        this.openStore = openStore; this.chatFactory = chatFactory;
        startup = new(InitializeAsync, actions, dataDirectory); content = startup;
        navigation = new RootNavigationService(destination => Content = destination switch
        { RootDestination.Workbench => main ?? throw new InvalidOperationException("工作台尚未就绪。"), _ => startup });
        CancelCloseCommand = new RelayCommand(() => { IsCloseConfirmationVisible = false; confirmed = false; layoutPermit = null; main?.CancelLayoutLeave(); });
        ReturnToWorkbenchCommand = new RelayCommand(() =>
        {
            if (IsClosing || IsClosed) return;
            closing = confirmed = false; layoutPermit = null; main?.CancelLayoutLeave(); IsCloseConfirmationVisible = false; CloseError = null; main?.ReturnToWorkbench();
        });
        ConfirmCloseCommand = new AsyncRelayCommand(async () => { confirmed = true; IsCloseConfirmationVisible = false; await CloseAsync(); });
        RetryCloseCommand = new AsyncRelayCommand(async () => { confirmed = true; await CloseAsync(); });
        RetrySaveCommand = new AsyncRelayCommand(async () =>
        {
            try { if (main is not null) await main.RetryPersistenceAsync(); CloseError = closing ? "保存已完成。可重试退出，或返回工作台。" : null; }
            catch (SummaryInputResolutionRequiredException error) { CloseError = error.Message; }
            catch { CloseError = "保存仍未完成。可重试保存、重试退出，或返回工作台阅读和复制内容。"; }
        });
    }
    partial void OnCloseErrorChanged(string? value) => OnPropertyChanged(nameof(HasCloseError));
    public async Task InitializeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (closing || main is not null) return;
            startup.IsBusy = true; startup.ErrorMessage = null; navigation.Navigate(RootDestination.StartupRecovery);
            store = await openStore();
            chat = chatFactory?.Invoke(store);
            if (chat is not null) await chat.InitializeAsync();
            var workbench = chat is null ? CompositionRoot.CreateMain(store) : CompositionRoot.CreateMain(store, chat);
            await workbench.InitializeAsync();
            if (workbench.ErrorMessage is not null) { workbench.DetachObservers(); throw new InvalidDataException(workbench.ErrorMessage); }
            main = workbench; navigation.Navigate(RootDestination.Workbench);
        }
        catch (Exception error)
        {
            if (chat is not null) { try { await chat.DisposeAsync(); } catch { } chat = null; }
            if (store is not null) { await store.DisposeAsync(); store = null; }
            startup.ErrorMessage = error.Message;
        }
        finally { startup.IsBusy = false; lifecycle.Release(); }
    }
    public Task CloseAsync()
    {
        // Repeated native close requests share an in-flight layout prompt/drain.
        if (closeAttempt is { IsCompleted: false }) return closeAttempt;
        return closeAttempt = CloseCoreAsync();
    }
    private async Task CloseCoreAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (IsClosed) return;
            while (main is not null)
            {
                if (layoutPermit is null || !main.IsLayoutLeavePermitCurrent(layoutPermit))
                { IsCloseConfirmationVisible = false; layoutPermit = await main.ConfirmLayoutLeaveAsync(); }
                if (layoutPermit is null) { confirmed = false; IsCloseConfirmationVisible = false; return; }
                // Every cancellable prompt precedes freezing, stopping work, or discarding.
                if (!confirmed && (chat?.Runtime.HasActiveOrPendingWork == true || main.HasPendingChatAcceptance))
                { IsCloseConfirmationVisible = true; return; }
                IsClosing = true;
                await main.QuiesceNetworkForCloseAsync();
                if (main.TryCommitLayoutLeave(layoutPermit)) { layoutPermit = null; break; }
                layoutPermit = null; main.ReturnToWorkbench(); IsClosing = false;
            }
            IsClosing = closing = true; CloseError = null;
            if (main is not null) await main.PrepareCloseAsync();
            if (chat is not null) await chat.Runtime.ShutdownAsync(main is null ? null : main.SaveDraftForCloseAsync);
            else if (main is not null) await main.SaveDraftForCloseAsync();
            if (chat is not null) { await chat.DisposeAsync(); chat = null; }
            if (store is not null) { await store.DisposeAsync(); store = null; }
            main?.DetachObservers(); IsClosed = true;
        }
        catch (SummaryInputResolutionRequiredException error) { CloseError = error.Message; }
        catch { CloseError = "退出时保存失败。当前内容仍可阅读和复制。可重试保存、重试退出，或返回工作台。"; }
        finally { IsClosing = false; lifecycle.Release(); }
        if (IsClosed) CloseCompleted?.Invoke();
    }
    public void ReportCloseError(Exception error) => CloseError = "退出未完成，请保留窗口并重试。";
}
