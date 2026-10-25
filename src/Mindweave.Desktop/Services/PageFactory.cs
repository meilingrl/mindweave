using Mindweave.Application;
using Mindweave.Application.Startup;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Services;

internal sealed class PageFactory(AppEnvironment environment, IDatabaseLifecycle database, ISystemShell shell) : IPageFactory
{
    public PageViewModel Create(ContentRoute route, Workspace workspace, IContentNavigator content) => route switch
    {
        ExploreHomeRoute => new ExploreHomeViewModel(workspace),
        NetworkHomeRoute => new NetworkHomeViewModel(workspace),
        SettingsHomeRoute => new SettingsHomeViewModel(environment, content),
        StorageDetailsRoute => new StorageDetailsViewModel(environment, database.ListBackups(), shell),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "No page is registered for this route."),
    };
}
