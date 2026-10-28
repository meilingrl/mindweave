using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Reading;
using Mindweave.Application.Startup;
using Mindweave.Application.Summaries;
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
    ReadingStateService reading,
    ModelSettingsService models,
    SummaryService summaries,
    Application.Export.SummaryExportService exports,
    Network.NetworkSessions networks,
    TimeProvider time,
    IUiDispatcher dispatcher) : IPageFactory
{
    public PageViewModel Create(ContentRoute route, PageContext context) => route switch
    {
        ExploreHomeRoute => new ExploreHomeViewModel(context.Workspace, context.Navigator),
        NetworkHomeRoute => new NetworkHomeViewModel(context.Workspace, networks.Get(context.Workspace.Id), summaries, conversations, context.Navigator, dispatcher),
        SummaryRoute summary => new SummaryViewModel(summary, context, summaries, dispatcher, time, exports, shell),
        ConversationRoute conversation => new ConversationViewModel(conversation, context, conversations, reading, dispatcher, summaries),
        NewConversationRoute => new NewConversationViewModel(context.Workspace.Id, conversations, context.Navigator, context.Navigator),
        ArchivedRoute => new ArchivedViewModel(context.Workspace, conversations, context.Navigator, dispatcher),
        SettingsHomeRoute => new SettingsHomeViewModel(environment, new ModelSettingsViewModel(models), context.Navigator),
        StorageDetailsRoute => new StorageDetailsViewModel(environment, database.ListBackups(), shell),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "No page is registered for this route."),
    };
}
