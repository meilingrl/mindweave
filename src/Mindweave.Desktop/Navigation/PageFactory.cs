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
    BackupRequests backupPrompts,
    ISystemShell shell,
    IConversations conversations,
    IReadingState reading,
    IModelSettings models,
    ISummaries summaries,
    Application.Export.ISummaryExport exports,
    SummaryNetwork.SummaryNetworkSessions summaryNetworks,
    SummaryNetwork.SummaryNetworkDetails summaryNetworkDetails,
    TimeProvider time,
    Application.Preferences.IAppearance appearance,
    IThemeApplier theme,
    Application.Storage.IStorageStatus storage,
    IUiDispatcher dispatcher,
    WorkspaceDeletionRequests workspaceDeletion,
    IConversationDeletion conversationDeletion,
    ConversationDeletionRequests conversationDeletionPrompt,
    Application.Preferences.INewsAlerts newsAlerts,
    Application.Workspaces.IWorkspaces workspaces,
    Application.Workspaces.IWorkspaceDeletion workspaceContents,
    WorkspaceSwitchRequests workspaceSwitcher,
    Application.Workspaces.IWorkspaceNameSuggestions nameSuggestions,
    Application.Workspaces.IWorkspaceNameCache nameCache,
    Application.Statistics.IUsageStatistics statistics,
    Application.Statistics.IUsagePrices prices,
    Application.Recommendations.IRecommendations recommendations,
    Application.Preferences.IRecommendationSettings recommendationSettings,
    Application.SummaryLinks.ISummaryLinks links,
    Application.SummaryLinks.ILinkSuggestions suggestions,
    Application.Resources.IResources resources,
    Application.Preferences.IToolSettings toolSettings,
    Application.Preferences.IWebSearchSettings webSearch,
    Application.Tools.Mcp.IMcpServers mcpServers,
    Application.Tools.Mcp.IMcpConnections mcpConnections,
    ShareRequests share,
    LanguageSwitch language,
    TextWidth textWidth,
    Application.Diagnostics.IFailureDetailsSource failures,
    Application.Preferences.IPersonalNote personalNote,
    Application.Diagnostics.IRequestList requestList,
    DiagnosticPackageViewModel diagnosticPackage) : IPageFactory
{
    public PageViewModel Create(ContentRoute route, PageContext context) => route switch
    {
        ExploreHomeRoute => new ExploreHomeViewModel(context.Workspace, context.Navigator),
        SummaryNetworkHomeRoute => new SummaryNetworkHomeViewModel(context.Workspace, summaryNetworks.Get(context.Workspace.Id), summaries, conversations, context.Navigator, dispatcher, summaryNetworkDetails, recommendations, recommendationSettings, links, suggestions, context.Replayed, share),
        SummaryRoute summary => new SummaryViewModel(summary, context, summaries, dispatcher, time, exports, shell, links, textWidth),
        ConversationRoute conversation => new ConversationViewModel(conversation, context, conversations, reading, dispatcher, storage, summaries, time, conversationDeletion, conversationDeletionPrompt, resources, models, Switch(), textWidth, failures),
        NewConversationRoute fresh => new NewConversationViewModel(context.Workspace.Id, conversations, context.Navigator, context.Navigator, recommendations, dispatcher, fresh, recommendationSettings, reading, models, resources, Switch(), context.Replayed),
        ArchivedRoute => new ArchivedViewModel(context.Workspace, conversations, context.Navigator, dispatcher, conversationDeletion, conversationDeletionPrompt),
        SettingsHomeRoute => new SettingsHomeViewModel(environment, new ModelSettingsViewModel(models), context.Navigator, appearance, theme, context.Workspaces, newsAlerts, recommendationSettings,
            new ToolSettingsPanel(toolSettings, models, new WebSearchSettingsPanel(webSearch), new McpServersPanel(mcpServers, mcpConnections, dispatcher)),
            language, textWidth, new PersonalNotePanel(personalNote)),
        StorageDetailsRoute => new StorageDetailsViewModel(environment, backupFiles, shell, backupPrompts),
        DiagnosticsRoute => new DiagnosticsViewModel(environment, requestList, shell, () => diagnosticPackage.Open(dataAvailable: true), time),
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

    /// <summary>A composer page's web search switch (requirements §10.4.5).</summary>
    private WebSearchSwitch Switch() => new(webSearch, toolSettings, models, dispatcher);
}
