using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Reading;
using Mindweave.Application.Startup;
using Mindweave.Application.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Infrastructure;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Logging;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Desktop;

/// <summary>The only place where Desktop references Infrastructure: wires contracts to implementations.</summary>
public static class CompositionRoot
{
    /// <param name="overrides">Registrations applied first; infrastructure defaults are only added where nothing is registered (tests).</param>
    public static ServiceProvider Build(AppPaths paths, ISystemShell? shell = null, ILoggerProvider? logging = null, Action<IServiceCollection>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(logging ?? new FileLoggerProvider(paths.LogDirectory, TimeProvider.System));
        });
        overrides?.Invoke(services);
        services.AddMindweaveInfrastructure(paths);
        services.AddSingleton(sp => new AppEnvironment(
            paths.Channel,
            typeof(CompositionRoot).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0",
            paths.DataDirectory,
            paths.DatabasePath,
            paths.BackupDirectory,
            paths.LogDirectory,
            sp.GetRequiredService<MigrationCatalog>().LatestVersion));

        services.AddSingleton<StartupCoordinator>();
        services.TryAddSingleton(new Application.Storage.StorageHealthOptions());
        services.AddSingleton<Application.Storage.StorageHealth>();
        services.AddSingleton<Application.Storage.IStorageStatus>(sp => sp.GetRequiredService<Application.Storage.StorageHealth>());
        services.AddSingleton<WorkspaceService>();
        services.TryAddSingleton(new GenerationOptions());
        services.AddSingleton<ContextCompressor>();
        services.AddSingleton<GenerationCoordinator>();
        services.AddSingleton<ModelSettingsService>();
        services.AddSingleton<ConversationService>();
        services.AddSingleton<ConversationMarksService>();
        services.AddSingleton<Application.Summaries.SummaryDrafts>();
        services.AddSingleton<Application.Summaries.SummaryService>();
        services.AddSingleton<Application.Network.NetworkService>();
        services.AddSingleton<Application.Export.SummaryExportService>();
        services.AddSingleton<ReadingStateService>();
        services.AddSingleton<Application.Preferences.AppearanceService>();
        services.AddSingleton<Application.Preferences.SidebarLayoutService>();

        // View models depend on these interfaces, not the concrete services (course requirement C3); each resolves to the
        // one singleton above, so a page and the rest of the application share state.
        services.AddSingleton<IConversations>(sp => sp.GetRequiredService<ConversationService>());
        services.AddSingleton<IConversationMarks>(sp => sp.GetRequiredService<ConversationMarksService>());
        services.AddSingleton<IGenerationExit>(sp => sp.GetRequiredService<GenerationCoordinator>());
        services.AddSingleton<Application.Summaries.ISummaries>(sp => sp.GetRequiredService<Application.Summaries.SummaryService>());
        services.AddSingleton<IReadingState>(sp => sp.GetRequiredService<ReadingStateService>());
        services.AddSingleton<Application.Export.ISummaryExport>(sp => sp.GetRequiredService<Application.Export.SummaryExportService>());
        services.AddSingleton<IModelSettings>(sp => sp.GetRequiredService<ModelSettingsService>());
        services.AddSingleton<IWorkspaces>(sp => sp.GetRequiredService<WorkspaceService>());
        services.AddSingleton<Application.Preferences.IAppearance>(sp => sp.GetRequiredService<Application.Preferences.AppearanceService>());
        services.AddSingleton<Application.Preferences.ISidebarLayout>(sp => sp.GetRequiredService<Application.Preferences.SidebarLayoutService>());
        services.AddSingleton<Application.Network.INetworkLayout>(sp => sp.GetRequiredService<Application.Network.NetworkService>());
        services.TryAddSingleton<IThemeApplier, AvaloniaThemeApplier>();
        services.TryAddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();

        services.AddSingleton(shell ?? new SystemShell());
        services.AddSingleton<IPageFactory, PageFactory>();
        services.AddSingleton<Workbench>();
        services.AddSingleton<IMenuNavigator>(sp => sp.GetRequiredService<Workbench>());
        services.AddSingleton<IContentNavigator>(sp => sp.GetRequiredService<Workbench>());
        services.TryAddSingleton(new ExitOptions());
        services.AddSingleton<ExitCoordinator>();
        services.AddSingleton<LayoutPromptViewModel>();
        services.AddSingleton<Network.NetworkSessions>();
        services.AddSingleton<ConversationListViewModel>();
        services.AddSingleton<RootViewModel>();
        services.AddSingleton<IRootNavigator>(sp => sp.GetRequiredService<RootViewModel>());
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<AppStartup>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
