using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mindweave.Application;
using Mindweave.Application.Startup;
using Mindweave.Application.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
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
        services.AddSingleton<WorkspaceService>();

        services.AddSingleton(shell ?? new SystemShell());
        services.AddSingleton<IPageFactory, PageFactory>();
        services.AddSingleton<Workbench>();
        services.AddSingleton<IMenuNavigator>(sp => sp.GetRequiredService<Workbench>());
        services.AddSingleton<IContentNavigator>(sp => sp.GetRequiredService<Workbench>());
        services.AddSingleton<RootViewModel>();
        services.AddSingleton<IRootNavigator>(sp => sp.GetRequiredService<RootViewModel>());
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<AppStartup>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
