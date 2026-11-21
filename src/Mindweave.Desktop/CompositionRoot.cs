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

/// <summary>
/// The only place where Desktop references Infrastructure: wires contracts to implementations. Composition has two
/// parts (bilingual-ui.md §6.2): a root provider with every service, which lives as long as the open database, and a
/// view provider built on it, which a language switch replaces while answers, runs and MCP servers go on.
/// </summary>
public static class CompositionRoot
{
    private static readonly ServiceProviderOptions Validated = new() { ValidateOnBuild = true, ValidateScopes = true };

    /// <summary>The service types the view provider registers itself; every other type belongs to the root.</summary>
    private static readonly Lazy<HashSet<Type>> ViewTypes = new(() =>
    {
        var services = new ServiceCollection();
        AddView(services);
        return [.. services.Select(d => d.ServiceType)];
    });

    /// <summary>
    /// Builds the root provider: logging, Infrastructure, Application, the platform services, the request bridges and the
    /// summary network sessions.
    /// </summary>
    /// <param name="overrides">
    /// Registrations applied first; infrastructure defaults are only added where nothing is registered (tests). Only
    /// those of root types are kept here; <see cref="BuildView"/> keeps those of view types.
    /// </param>
    public static ServiceProvider BuildRoot(AppPaths paths, ISystemShell? shell = null, ILoggerProvider? logging = null, Action<IServiceCollection>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(logging ?? new FileLoggerProvider(paths.LogDirectory, TimeProvider.System));
        });
        AddOverrides(services, overrides, view: false);
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
        services.TryAddSingleton(SystemAlertSound.ForPlatform()); // Tests count calls with their own.
        services.TryAddSingleton<IInAppRestore, UnavailableInAppRestore>(); // The application session registers itself first.
        services.TryAddSingleton<IViewSession, UnavailableViewSession>(); // So does it here.

        // A language switch replaces the view it is called from, so it lives here, with the services (§6).
        services.AddSingleton<LanguageSwitch>();

        // The bridges between pages and the window's dialogs: a page a view made reaches the dialogs of the view shown.
        services.AddSingleton<WorkspaceDeletionRequests>();
        services.AddSingleton<IWorkspaceDeletionPrompt>(sp => sp.GetRequiredService<WorkspaceDeletionRequests>());
        services.AddSingleton<WorkspaceSwitchRequests>();
        services.AddSingleton<IWorkspaceSwitcher>(sp => sp.GetRequiredService<WorkspaceSwitchRequests>());
        services.AddSingleton<ConversationDeletionRequests>();
        services.AddSingleton<IConversationDeletionPrompt>(sp => sp.GetRequiredService<ConversationDeletionRequests>());
        services.AddSingleton<BackupRequests>();
        services.AddSingleton<IBackupPrompts>(sp => sp.GetRequiredService<BackupRequests>());

        // Unsaved layouts and the viewport outlive a view.
        services.TryAddSingleton(SummaryNetwork.SessionNetworkGate.FromEnvironment()); // Tests give their own.
        services.AddSingleton<SummaryNetwork.SummaryNetworkSessions>();
        services.TryAddSingleton(new ExitOptions());
        services.TryAddSingleton(new WorkspaceNewsOptions());

        // The view providers built on this root take every registration made here (see AddRootServices).
        var registrations = new RootRegistrations();
        services.AddSingleton(registrations);
        registrations.Descriptors = [.. services];
        return services.BuildServiceProvider(Validated);
    }

    /// <summary>
    /// Builds a view provider on <paramref name="root"/>: the window's view models, the dialogs, the workbench and its
    /// pages' factory, the exit coordinator and the startup. Each root service is in it as the root's own instance, so
    /// views share them and disposing a view disposes only what the view made.
    /// </summary>
    /// <param name="root">A provider made by <see cref="BuildRoot"/>.</param>
    /// <param name="overrides">The same registrations as given to the root; only those of view types are kept here.</param>
    public static ServiceProvider BuildView(IServiceProvider root, Action<IServiceCollection>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var services = new ServiceCollection();
        AddRootServices(services, root);
        AddOverrides(services, overrides, view: true);
        AddView(services);
        return services.BuildServiceProvider(Validated);
    }

    private static void AddView(IServiceCollection services)
    {
        services.TryAddSingleton<IPageFactory, PageFactory>(); // Tests wrap it.
        services.AddSingleton<Workbench>();
        services.AddSingleton<IMenuNavigator>(sp => sp.GetRequiredService<Workbench>());
        services.AddSingleton<IContentNavigator>(sp => sp.GetRequiredService<Workbench>());
        services.AddSingleton<ExitCoordinator>();
        services.AddSingleton<LayoutPromptViewModel>();
        services.AddSingleton<WorkspaceDeletionViewModel>();
        services.TryAddSingleton<ConversationDeletionViewModel>(); // Tests give it their own clock.
        services.AddSingleton<BackupRestoreViewModel>();
        services.AddSingleton<BackupDeletionViewModel>();
        services.AddSingleton<ConversationListViewModel>();
        services.AddSingleton<RootViewModel>();
        services.AddSingleton<IRootNavigator>(sp => sp.GetRequiredService<RootViewModel>());
        services.AddSingleton<WorkspaceNewsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<AppStartup>();
    }

    /// <summary>Applies the overrides of one side: those of view types to a view provider, all others to the root.</summary>
    private static void AddOverrides(IServiceCollection services, Action<IServiceCollection>? overrides, bool view)
    {
        if (overrides is null)
        {
            return;
        }

        var given = new ServiceCollection();
        overrides(given);
        foreach (var descriptor in given.Where(d => ViewTypes.Value.Contains(d.ServiceType) == view))
        {
            services.Add(descriptor);
        }
    }

    /// <summary>
    /// Registers every root service in a view. A singleton goes in as the root's own instance, never through a factory:
    /// the container disposes what a factory returns, and a view must never dispose a root service. Instances keep the
    /// root's order, so a service with several registrations resolves the same. Open generics (<see cref="ILogger{T}"/>)
    /// and services made per request are registered as in the root; a view makes, and disposes, its own of those.
    /// </summary>
    /// <remarks>
    /// Every root singleton is made here, when the first view is built, rather than on first use. Then no view class
    /// can miss one, and every owner of kept writes has registered with storage health before startup begins.
    /// </remarks>
    private static void AddRootServices(IServiceCollection services, IServiceProvider root)
    {
        var registrations = root.GetRequiredService<RootRegistrations>();
        foreach (var group in registrations.Descriptors.GroupBy(d => (d.ServiceType, d.ServiceKey)))
        {
            var (type, key) = group.Key;
            if (type == typeof(RootRegistrations))
            {
                continue;
            }

            if (type.IsGenericTypeDefinition || group.Any(d => d.Lifetime != ServiceLifetime.Singleton))
            {
                foreach (var descriptor in group)
                {
                    services.Add(descriptor);
                }

                continue;
            }

            var instances = key is null ? root.GetServices(type) : root.GetKeyedServices(type, key);
            foreach (var instance in instances)
            {
                services.Add(key is null
                    ? ServiceDescriptor.Singleton(type, instance!)
                    : ServiceDescriptor.KeyedSingleton(type, key, instance!));
            }
        }
    }

    /// <summary>What a root provider registers, for the view providers built on it (and for tests that look at every root service).</summary>
    internal sealed class RootRegistrations
    {
        public IReadOnlyList<ServiceDescriptor> Descriptors { get; set; } = [];
    }
}
