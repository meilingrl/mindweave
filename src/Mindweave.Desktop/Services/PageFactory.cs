using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Startup;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Services;

internal sealed class PageFactory(
    AppEnvironment environment,
    IDatabaseLifecycle database,
    ISystemShell shell,
    ConversationService conversations,
    ModelSettingsService models,
    IUiDispatcher dispatcher) : IPageFactory
{
    public PageViewModel Create(ContentRoute route, Workspace workspace, IWorkbenchNavigator navigator) => route switch
    {
        ExploreHomeRoute => new ExploreHomeViewModel(workspace, navigator),
        NetworkHomeRoute => new NetworkHomeViewModel(workspace),
        ConversationRoute conversation => new ConversationViewModel(conversation.Id, conversations, navigator, dispatcher),
        NewConversationRoute => new NewConversationViewModel(workspace.Id, conversations, navigator, navigator),
        SettingsHomeRoute => new SettingsHomeViewModel(environment, new ModelSettingsViewModel(models), navigator),
        StorageDetailsRoute => new StorageDetailsViewModel(environment, database.ListBackups(), shell),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "No page is registered for this route."),
    };
}
