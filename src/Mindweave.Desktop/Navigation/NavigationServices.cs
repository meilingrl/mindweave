using Mindweave.Application.Navigation;

namespace Mindweave.Desktop.Navigation;

public sealed class RootNavigationService(Action<RootDestination> navigate) : IRootNavigationService
{
    public void Navigate(RootDestination destination) => navigate(destination);
}

public sealed class MenuNavigationService(Func<MainArea, Task> navigate) : IMenuNavigationService
{
    public Task NavigateAsync(MainArea area) => navigate(area);
}

public sealed class ContentNavigationService(Func<NavigationRequest, Task> navigate, Func<Task> goBack) : IContentNavigationService
{
    public Task NavigateAsync(NavigationRequest request) => navigate(request);
    public Task GoBackAsync() => goBack();
}
