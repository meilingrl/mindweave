using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mindweave.Application;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Infrastructure;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Logging;
using Mindweave.Infrastructure.Platform;
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

        services.AddMindweaveApplication();
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
        services.AddSingleton<WorkspaceDeletionRequests>();
        services.AddSingleton<IWorkspaceDeletionPrompt>(sp => sp.GetRequiredService<WorkspaceDeletionRequests>());
        services.AddSingleton<WorkspaceDeletionViewModel>();
        services.AddSingleton<WorkspaceSwitchRequests>();
        services.AddSingleton<IWorkspaceSwitcher>(sp => sp.GetRequiredService<WorkspaceSwitchRequests>());
        services.AddSingleton<ConversationDeletionRequests>();
        services.AddSingleton<IConversationDeletionPrompt>(sp => sp.GetRequiredService<ConversationDeletionRequests>());
        services.TryAddSingleton<ConversationDeletionViewModel>(); // Tests give it their own clock.
        services.AddSingleton<BackupRequests>();
        services.AddSingleton<IBackupPrompts>(sp => sp.GetRequiredService<BackupRequests>());
        services.TryAddSingleton<IInAppRestore, UnavailableInAppRestore>(); // The application session registers itself first.
        services.AddSingleton<BackupRestoreViewModel>();
        services.AddSingleton<BackupDeletionViewModel>();
        services.AddSingleton<Network.NetworkSessions>();
        services.AddSingleton<ConversationListViewModel>();
        services.AddSingleton<RootViewModel>();
        services.AddSingleton<IRootNavigator>(sp => sp.GetRequiredService<RootViewModel>());
        services.TryAddSingleton(SystemAlertSound.ForPlatform()); // Tests count calls with their own.
        services.TryAddSingleton(new WorkspaceNewsOptions());
        services.AddSingleton<WorkspaceNewsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<AppStartup>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
