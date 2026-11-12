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
    IBackupFiles backupFiles,
    IBackupPrompts backupPrompts,
    ISystemShell shell,
    IConversations conversations,
    IReadingState reading,
    IModelSettings models,
    ISummaries summaries,
    Application.Export.ISummaryExport exports,
    Network.NetworkSessions networks,
    TimeProvider time,
    Application.Preferences.IAppearance appearance,
    IThemeApplier theme,
    Application.Storage.IStorageStatus storage,
    IUiDispatcher dispatcher,
    IWorkspaceDeletionPrompt workspaceDeletion,
    IConversationDeletion conversationDeletion,
    IConversationDeletionPrompt conversationDeletionPrompt,
    Application.Preferences.INewsAlerts newsAlerts,
    Application.Workspaces.IWorkspaces workspaces,
    Application.Workspaces.IWorkspaceDeletion workspaceContents,
    IWorkspaceSwitcher workspaceSwitcher,
    Application.Workspaces.IWorkspaceNameSuggestions nameSuggestions,
    Application.Workspaces.IWorkspaceNameCache nameCache,
    Application.Statistics.IUsageStatistics statistics,
    Application.Statistics.IUsagePrices prices) : IPageFactory
{
    public PageViewModel Create(ContentRoute route, PageContext context) => route switch
    {
        ExploreHomeRoute => new ExploreHomeViewModel(context.Workspace, context.Navigator),
        NetworkHomeRoute => new NetworkHomeViewModel(context.Workspace, networks.Get(context.Workspace.Id), summaries, conversations, context.Navigator, dispatcher),
        SummaryRoute summary => new SummaryViewModel(summary, context, summaries, dispatcher, time, exports, shell),
        ConversationRoute conversation => new ConversationViewModel(conversation, context, conversations, reading, dispatcher, storage, summaries, time, conversationDeletion, conversationDeletionPrompt),
        NewConversationRoute => new NewConversationViewModel(context.Workspace.Id, conversations, context.Navigator, context.Navigator),
        ArchivedRoute => new ArchivedViewModel(context.Workspace, conversations, context.Navigator, dispatcher, conversationDeletion, conversationDeletionPrompt),
        SettingsHomeRoute => new SettingsHomeViewModel(environment, new ModelSettingsViewModel(models), context.Navigator, appearance, theme, context.Workspaces, newsAlerts),
        StorageDetailsRoute => new StorageDetailsViewModel(environment, backupFiles, shell, backupPrompts),
        UsageStatisticsRoute => new UsageStatisticsViewModel(context.Workspace, statistics, prices, context.Navigator),
        WorkspacesRoute => new WorkspacesViewModel(
            context.Workspaces ?? throw new InvalidOperationException("The workspaces page needs the workbench's list."),
            workspaces,
            workspaceContents,
            workspaceDeletion,
            workspaceSwitcher,
            nameSuggestions,
            context.Navigator,
            nameCache),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "No page is registered for this route."),
    };
}
