using Mindweave.Application.Chat;
using Mindweave.Application.Storage;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
namespace Mindweave.Desktop;

public static class CompositionRoot
{
    // S0 navigation-only compatibility. Production Root supplies its single application session.
    public static MainViewModel CreateMain(IWorkspaceStore store) => Create(store, null, null);
    public static MainViewModel CreateMain(IWorkspaceStore store, GenerationCoordinator coordinator, ChatConfigurationService configuration) => Create(store, coordinator, configuration);
    private static MainViewModel Create(IWorkspaceStore store, GenerationCoordinator? coordinator, ChatConfigurationService? configuration)
    {
        MainViewModel main = null!;
        var menu = new MenuNavigationService(area => main.NavigateAsync(new(area)));
        var content = new ContentNavigationService(request => main.NavigateAsync(request), () => main.GoBackAsync());
        main = new MainViewModel(store, menu, content, coordinator, configuration);
        return main;
    }
}
