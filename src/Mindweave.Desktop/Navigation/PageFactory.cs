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
    Application.SummaryLinks.ILinkSuggestions suggestions,
    Application.Resources.IResources? resources = null,
    Application.Preferences.IToolSettings? toolSettings = null,
    Application.Preferences.IWebSearchSettings? webSearch = null,
    Application.Tools.Mcp.IMcpServers? mcpServers = null,
    Application.Tools.Mcp.IMcpConnections? mcpConnections = null) : IPageFactory
{
    public PageViewModel Create(ContentRoute route, PageContext context) => route switch
    {
        ExploreHomeRoute => new ExploreHomeViewModel(context.Workspace, context.Navigator),
        SummaryNetworkHomeRoute => new SummaryNetworkHomeViewModel(context.Workspace, summaryNetworks.Get(context.Workspace.Id), summaries, conversations, context.Navigator, dispatcher, recommendations, recommendationSettings, links, suggestions),
        SummaryRoute summary => new SummaryViewModel(summary, context, summaries, dispatcher, time, exports, shell, links),
        ConversationRoute conversation => new ConversationViewModel(conversation, context, conversations, reading, dispatcher, storage, summaries, time, conversationDeletion, conversationDeletionPrompt, resources, models, Switch()),
        NewConversationRoute fresh => new NewConversationViewModel(context.Workspace.Id, conversations, context.Navigator, context.Navigator, recommendations, dispatcher, fresh, recommendationSettings, reading, models, resources, Switch()),
        ArchivedRoute => new ArchivedViewModel(context.Workspace, conversations, context.Navigator, dispatcher, conversationDeletion, conversationDeletionPrompt),
        SettingsHomeRoute => new SettingsHomeViewModel(environment, new ModelSettingsViewModel(models), context.Navigator, appearance, theme, context.Workspaces, newsAlerts, recommendationSettings,
            toolSettings is null ? null : new ToolSettingsPanel(
                toolSettings, models, webSearch is null ? null : new WebSearchSettingsPanel(webSearch),
                mcpServers is null || mcpConnections is null ? null : new McpServersPanel(mcpServers, mcpConnections, dispatcher))),
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

    /// <summary>A composer page's web search switch (requirements §10.4.5); none without the settings.</summary>
    private WebSearchSwitch? Switch() => webSearch is null ? null : new WebSearchSwitch(webSearch, toolSettings, models, dispatcher);
}
