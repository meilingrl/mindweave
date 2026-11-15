using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mindweave.Application;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Export;
using Mindweave.Infrastructure.Ai;
using Mindweave.Infrastructure.Credentials;
using Mindweave.Infrastructure.Export;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure;

public static class InfrastructureServices
{
    public static IServiceCollection AddMindweaveInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.TryAddSingleton(MigrationCatalog.Default);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
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
        services.AddSingleton<IBranchStore, SqliteBranchStore>();
        services.TryAddSingleton<IConversationArchiveStore, SqliteArchiveStore>();
        services.TryAddSingleton<IReadingStateStore, SqliteReadingStateStore>();
        services.AddSingleton<IContextSummaryStore, SqliteContextSummaryStore>();
        services.TryAddSingleton<ISummaryStore, SqliteSummaryStore>();
        services.TryAddSingleton<ISummaryNetworkStore, SqliteSummaryNetworkStore>();
        services.TryAddSingleton<IExportFileWriter, AtomicFileWriter>();
        services.TryAddSingleton(_ => CredentialStores.ForPlatform(paths.Channel));
        services.TryAddSingleton(new ChatClientOptions());
        services.TryAddSingleton<IChatClient>(sp => new ChatCompletionsClient(
            new HttpClient(ChatCompletionsClient.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<ChatClientOptions>()));
        return services;
    }
}
