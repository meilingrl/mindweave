using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Reading;
using Mindweave.Application.Startup;
using Mindweave.Application.Summaries;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Navigation;

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
    SummaryNetwork.SummaryNetworkSessions summaryNetworks,
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
    Application.Statistics.IUsagePrices prices,
    Application.Recommendations.IRecommendations recommendations,
    Application.Preferences.IRecommendationSettings recommendationSettings,
    Application.SummaryLinks.ISummaryLinks links,
    Application.SummaryLinks.ILinkSuggestions suggestions) : IPageFactory
{
    public PageViewModel Create(ContentRoute route, PageContext context) => route switch
    {
        ExploreHomeRoute => new ExploreHomeViewModel(context.Workspace, context.Navigator),
        SummaryNetworkHomeRoute => new SummaryNetworkHomeViewModel(context.Workspace, summaryNetworks.Get(context.Workspace.Id), summaries, conversations, context.Navigator, dispatcher, recommendations, recommendationSettings, links, suggestions),
        SummaryRoute summary => new SummaryViewModel(summary, context, summaries, dispatcher, time, exports, shell, links),
        ConversationRoute conversation => new ConversationViewModel(conversation, context, conversations, reading, dispatcher, storage, summaries, time, conversationDeletion, conversationDeletionPrompt),
        NewConversationRoute fresh => new NewConversationViewModel(context.Workspace.Id, conversations, context.Navigator, context.Navigator, recommendations, dispatcher, fresh, recommendationSettings, reading),
        ArchivedRoute => new ArchivedViewModel(context.Workspace, conversations, context.Navigator, dispatcher, conversationDeletion, conversationDeletionPrompt),
        SettingsHomeRoute => new SettingsHomeViewModel(environment, new ModelSettingsViewModel(models), context.Navigator, appearance, theme, context.Workspaces, newsAlerts, recommendationSettings),
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
