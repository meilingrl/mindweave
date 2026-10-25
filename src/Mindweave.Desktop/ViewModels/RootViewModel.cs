using Mindweave.Application.Storage;
using Mindweave.Desktop.Services;
using Mindweave.Application.Navigation;
using Mindweave.Desktop.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mindweave.Desktop.ViewModels;

public sealed partial class RootViewModel : ViewModelBase
{
    private readonly Func<Task<IWorkspaceStore>> openStore;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly IRootNavigationService navigation;
    private readonly StartupViewModel startup;
    private IWorkspaceStore? store;
    private MainViewModel? main;
    private bool closing;
    [ObservableProperty] private ViewModelBase content;
    [ObservableProperty] private string? closeError;

    public RootViewModel(Func<Task<IWorkspaceStore>> openStore, IDesktopActions actions, string dataDirectory)
    {
        this.openStore = openStore;
        startup = new(InitializeAsync, actions, dataDirectory);
        content = startup;
        navigation = new RootNavigationService(destination => Content = destination switch
        {
            RootDestination.Workbench => main ?? throw new InvalidOperationException("工作台尚未就绪。"),
            _ => startup
        });
    }

    public async Task InitializeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (closing || main is not null) return;
            startup.IsBusy = true;
            startup.ErrorMessage = null;
            navigation.Navigate(RootDestination.StartupRecovery);
            store = await openStore();
            var workbench = CompositionRoot.CreateMain(store);
            await workbench.InitializeAsync();
            if (workbench.ErrorMessage is not null) throw new InvalidDataException(workbench.ErrorMessage);
            main = workbench;
            navigation.Navigate(RootDestination.Workbench);
        }
        catch (Exception error)
        {
            if (store is not null) { await store.DisposeAsync(); store = null; }
            startup.ErrorMessage = error.Message;
        }
        finally { startup.IsBusy = false; lifecycle.Release(); }
    }

    public async Task CloseAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            closing = true;
            if (main is not null) await main.PrepareCloseAsync();
            if (store is not null) { await store.DisposeAsync(); store = null; }
        }
        finally { lifecycle.Release(); }
    }

    public void ReportCloseError(Exception error) => CloseError = $"退出未完成，请保留窗口并重试：{error.Message}";
}
