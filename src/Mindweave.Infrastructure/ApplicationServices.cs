using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Models;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Application.Preferences;
using Mindweave.Application.Reading;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Startup;
using Mindweave.Application.Statistics;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Application.SummaryLinks;
using Mindweave.Application.Workspaces;

namespace Mindweave.Infrastructure;

/// <summary>
/// The application services and the interfaces they are reached through. Lives here rather than in Application, which
/// may depend only on Core and the logging abstractions; the app's composition root and the test harnesses share it.
/// </summary>
public static class ApplicationServices
{
    /// <summary>
    /// Registers the application layer on top of <see cref="InfrastructureServices.AddMindweaveInfrastructure"/>. Options
    /// and the conversations interface are only added where nothing is registered yet, so a caller can supply its own first.
    /// </summary>
    public static IServiceCollection AddMindweaveApplication(this IServiceCollection services)
    {
        services.AddSingleton<StartupCoordinator>();
        services.TryAddSingleton(new StorageHealthOptions());
        services.AddSingleton<StorageHealth>();
        services.AddSingleton<IStorageStatus>(sp => sp.GetRequiredService<StorageHealth>());
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<WorkspaceDeletionService>();
        services.AddSingleton<ConversationDeletionService>();
        services.TryAddSingleton(new GenerationOptions());
        services.AddSingleton<ContextBudgets>();
        services.AddSingleton<ContextCompressor>();
        services.AddSingleton<GenerationRequests>();
        services.AddSingleton<GenerationCoordinator>();
        services.AddSingleton<ModelSettingsService>();
        services.AddSingleton<ConversationService>();
        services.AddSingleton<ConversationMarksService>();
        services.AddSingleton<SummaryDrafts>();
        services.AddSingleton<SummaryService>();
        services.AddSingleton<SummaryNetworkService>();
        services.AddSingleton<SummaryExportService>();
        services.AddSingleton<ReadingStateService>();
        services.AddSingleton<AppearanceService>();
        services.AddSingleton<SidebarLayoutService>();
        services.AddSingleton<NewsAlertsService>();
        services.AddSingleton<OnboardingProgressService>();
        services.AddSingleton<WorkspaceNameSuggester>();
        services.AddSingleton<WorkspaceNameCache>();
        services.TryAddSingleton<IWorkspaceNameCache>(sp => sp.GetRequiredService<WorkspaceNameCache>());
        services.TryAddSingleton(new WorkspaceNamePreparationOptions());
        services.AddSingleton<WorkspaceNamePreparer>();
        services.TryAddSingleton(new UsageStatisticsOptions()); // Tests give their own clock and zone.
        services.AddSingleton<UsageStatisticsService>();
        services.AddSingleton<UsagePricesService>();
        services.AddSingleton<RecommendationSettingsService>();
        services.TryAddSingleton(new RecommendationOptions()); // Tests stand its clock still.
        services.AddSingleton<RecommendationService>();
        services.AddSingleton<SummaryLinkService>();
        services.AddSingleton<LinkSuggestionService>();

        // View models depend on these interfaces, not the concrete services (course requirement C3); each resolves to the
        // one singleton above, so a page and the rest of the application share state.
        // A test may wrap the conversations interface (to hold a page reload); the application registers nothing else.
        services.TryAddSingleton<IConversations>(sp => sp.GetRequiredService<ConversationService>());
        services.AddSingleton<IConversationMarks>(sp => sp.GetRequiredService<ConversationMarksService>());
        services.AddSingleton<IGenerationExit>(sp => sp.GetRequiredService<GenerationCoordinator>());
        services.AddSingleton<ISummaries>(sp => sp.GetRequiredService<SummaryService>());
        services.AddSingleton<IReadingState>(sp => sp.GetRequiredService<ReadingStateService>());
        services.AddSingleton<ISummaryExport>(sp => sp.GetRequiredService<SummaryExportService>());
        services.AddSingleton<IModelSettings>(sp => sp.GetRequiredService<ModelSettingsService>());
        services.AddSingleton<IWorkspaces>(sp => sp.GetRequiredService<WorkspaceService>());
        // A test may stand in for the model's suggestions (to hold or reorder them), as for the conversations interface.
        services.TryAddSingleton<IWorkspaceNameSuggestions>(sp => sp.GetRequiredService<WorkspaceNameSuggester>());
        services.AddSingleton<IWorkspaceDeletion>(sp => sp.GetRequiredService<WorkspaceDeletionService>());
        services.AddSingleton<IConversationDeletion>(sp => sp.GetRequiredService<ConversationDeletionService>());
        services.AddSingleton<IAppearance>(sp => sp.GetRequiredService<AppearanceService>());
        services.AddSingleton<ISidebarLayout>(sp => sp.GetRequiredService<SidebarLayoutService>());
        services.AddSingleton<INewsAlerts>(sp => sp.GetRequiredService<NewsAlertsService>());
        services.AddSingleton<IOnboardingProgress>(sp => sp.GetRequiredService<OnboardingProgressService>());
        services.AddSingleton<ISummaryNetworkLayout>(sp => sp.GetRequiredService<SummaryNetworkService>());
        // A test may hold the statistics (to see the page while they load), as for the conversations interface.
        services.TryAddSingleton<IUsageStatistics>(sp => sp.GetRequiredService<UsageStatisticsService>());
        services.AddSingleton<IUsagePrices>(sp => sp.GetRequiredService<UsagePricesService>());
        services.AddSingleton<IRecommendationSettings>(sp => sp.GetRequiredService<RecommendationSettingsService>());
        // A test may hold the recommendations (to see the page while they load), as for the conversations interface.
        services.TryAddSingleton<IRecommendations>(sp => sp.GetRequiredService<RecommendationService>());
        services.AddSingleton<ISummaryLinks>(sp => sp.GetRequiredService<SummaryLinkService>());
        services.AddSingleton<ILinkSuggestions>(sp => sp.GetRequiredService<LinkSuggestionService>());
        return services;
    }
}
