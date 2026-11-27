using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Export;
using Mindweave.Application.Import;
using Mindweave.Application.Languages;
using Mindweave.Application.Resources;
using Mindweave.Application.Share;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Infrastructure.Ai;
using Mindweave.Infrastructure.Credentials;
using Mindweave.Infrastructure.Export;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Import;
using Mindweave.Infrastructure.Languages;
using Mindweave.Infrastructure.Resources;
using Mindweave.Infrastructure.Share;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;
using Mindweave.Infrastructure.Tools;

namespace Mindweave.Infrastructure;

public static class InfrastructureServices
{
    public static IServiceCollection AddMindweaveInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.TryAddSingleton<ILanguageChoice>(new FileLanguageChoice(paths.DataDirectory));
        services.TryAddSingleton(MigrationCatalog.Default);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton(new SqliteDatabaseOptions()); // Tests shorten the busy wait.
        services.AddSingleton<SqliteDatabase>();
        services.TryAddSingleton<IDatabaseLifecycle>(sp => sp.GetRequiredService<SqliteDatabase>());
        services.AddSingleton<IStorageProbe>(sp => sp.GetRequiredService<SqliteDatabase>());
        services.AddSingleton<IDatabaseBackup>(sp => sp.GetRequiredService<SqliteDatabase>());
        services.AddSingleton<IBackupFiles>(sp => sp.GetRequiredService<SqliteDatabase>());
        services.AddSingleton<IWorkspaceStore, SqliteWorkspaceStore>();
        services.AddSingleton<ILocationStore, SqliteLocationStore>();
        services.TryAddSingleton<IPreferenceStore, SqlitePreferenceStore>();
        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.TryAddSingleton<IModelSettingsStore, SqliteModelSettingsStore>();
        services.AddSingleton<IAiRequestStore, SqliteAiRequestStore>();
        services.AddSingleton<IStatisticsStore, SqliteStatisticsStore>();
        services.TryAddSingleton<IRecommendationStore, SqliteRecommendationStore>();
        services.AddSingleton<ISummaryLinkStore, SqliteSummaryLinkStore>();
        services.AddSingleton<ILinkSuggestionStore, SqliteLinkSuggestionStore>();
        services.AddSingleton<IBranchStore, SqliteBranchStore>();
        services.AddSingleton<IResourceStore, SqliteResourceStore>();
        services.AddSingleton<IRunStore, SqliteRunStore>();
        services.AddSingleton<FileResourceBlobStore>();
        services.AddSingleton<IResourceBlobStore>(sp => sp.GetRequiredService<FileResourceBlobStore>());
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxTextExtractor>();
        services.AddSingleton<IImageProcessor, SkiaImageProcessor>();
        services.TryAddSingleton<IConversationArchiveStore, SqliteArchiveStore>();
        services.TryAddSingleton<IReadingStateStore, SqliteReadingStateStore>();
        services.AddSingleton<IContextSummaryStore, SqliteContextSummaryStore>();
        services.TryAddSingleton<ISummaryStore, SqliteSummaryStore>();
        services.TryAddSingleton<ISummaryNetworkStore, SqliteSummaryNetworkStore>();
        services.TryAddSingleton<IExportFileWriter, AtomicFileWriter>();
        services.AddSingleton<ISharePageWriter, SharePageWriter>();
        services.AddSingleton<IImportStore, SqliteImportStore>();
        services.AddSingleton<ISharedFileReader, SharedFileReader>();
        services.AddSingleton<Application.ManualBackup.IManualBackupStore, SqliteManualBackupStore>();
        services.AddSingleton<Application.ManualBackup.IManualBackupWriter, ManualBackup.ManualBackupPackage>();
        services.AddSingleton<Application.ManualBackup.IManualBackupReader, ManualBackup.ManualBackupReader>();
        services.AddSingleton<Application.ManualBackup.IManualBackupImportStore, SqliteManualBackupImportStore>();
        services.TryAddSingleton(_ => CredentialStores.ForPlatform(paths.Channel));
        services.TryAddSingleton(new ChatClientOptions());
        services.TryAddSingleton<IChatClient>(sp => new ChatCompletionsClient(
            new HttpClient(ChatCompletionsClient.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<ChatClientOptions>()));

        // Web search and page reading (phase 3, architecture §15.13): each with its own client, per-request limits inside.
        services.TryAddSingleton<ISearchProvider>(_ => new TavilySearchProvider(
            new HttpClient(TavilySearchProvider.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan }));
        services.TryAddSingleton<IWebPageFetcher>(sp => GuardedPageFetcher.Create(
            sp.GetServices<ITextExtractor>(),
            sp.GetRequiredService<TimeProvider>()));

        // Third-party tools (phase 4, architecture §15.14): the MCP library is used only behind this.
        services.TryAddSingleton<Application.Tools.Mcp.IMcpConnector>(_ => new Tools.Mcp.SdkMcpConnector(paths));
        return services;
    }
}
