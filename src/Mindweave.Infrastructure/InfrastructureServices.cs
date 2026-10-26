using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mindweave.Application;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;
using Mindweave.Infrastructure.Ai;
using Mindweave.Infrastructure.Credentials;
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
        services.AddSingleton<IDatabaseLifecycle>(sp => sp.GetRequiredService<SqliteDatabase>());
        services.AddSingleton<IWorkspaceStore, SqliteWorkspaceStore>();
        services.AddSingleton<ILocationStore, SqliteLocationStore>();
        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<IModelSettingsStore, SqliteModelSettingsStore>();
        services.AddSingleton<IAiRequestStore, SqliteAiRequestStore>();
        services.AddSingleton<IBranchStore, SqliteBranchStore>();
        services.AddSingleton<IReadingStateStore, SqliteReadingStateStore>();
        services.AddSingleton<IContextSummaryStore, SqliteContextSummaryStore>();
        services.TryAddSingleton(_ => CredentialStores.ForPlatform(paths.Channel));
        services.TryAddSingleton(new ChatClientOptions());
        services.TryAddSingleton<IChatClient>(sp => new ChatCompletionsClient(
            new HttpClient(ChatCompletionsClient.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<ChatClientOptions>()));
        return services;
    }
}
