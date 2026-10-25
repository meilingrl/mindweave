using Mindweave.Application.Storage;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop;

public static class CompositionRoot
{
    public static MainViewModel CreateMain(IWorkspaceStore store)
    {
        MainViewModel main = null!;
        var menu = new MenuNavigationService(area => main.NavigateAsync(new(area)));
        var content = new ContentNavigationService(request => main.NavigateAsync(request), () => main.GoBackAsync());
        main = new MainViewModel(store, menu, content);
        return main;
    }
}
