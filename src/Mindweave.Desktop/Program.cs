using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mindweave.Infrastructure.Hosting;

namespace Mindweave.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Resolve(AppPaths.ChannelOf(typeof(Program).Assembly));

        // One writer per data directory: take the lock before touching the database at all.
        var instance = SingleInstance.TryAcquire(paths);
        if (instance is null)
        {
            var result = SingleInstance.SignalPrimaryAsync(paths, TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            if (result == SecondaryLaunchResult.Activated)
            {
                return 0;
            }

            // The holder may have just died (e.g. a crash): Windows releases its lock a moment after exit.
            instance = SingleInstance.TryAcquire(paths);
            if (instance is null)
            {
                Console.Error.WriteLine($"Mindweave is already running for {paths.DataDirectory} but did not respond. Close it and try again.");
                return 2;
            }
        }

        var services = CompositionRoot.Build(paths);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Mindweave");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(logger, e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(logger, e.Exception);

        var host = new AppHost(instance, services);
        return BuildAvaloniaApp(host).StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Used by the Avalonia previewer; the real entry point passes a host.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();

    private static AppBuilder BuildAvaloniaApp(AppHost host) =>
        AppBuilder.Configure(() => new App(host)).UsePlatformDetect().WithInterFont();

#pragma warning disable CA1848 // Crash path: a plain call is clearer than a source-generated delegate.
    private static void LogCrash(ILogger logger, Exception? exception) =>
        logger.LogCritical(exception, "Unhandled exception");
#pragma warning restore CA1848
}
