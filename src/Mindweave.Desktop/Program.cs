using Avalonia;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Languages;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Languages;

namespace Mindweave.Desktop;

internal static class Program
{
    /// <summary>The language the user chose for this channel's data; without a choice (a first start), English (§7).</summary>
    internal static void UseStoredLanguage(AppPaths paths) => CurrentLanguage.Set(new FileLanguageChoice(paths.DataDirectory).Read());

    [STAThread]
    public static int Main(string[] args)
    {
        AppPaths paths;
        SingleInstance? instance;
        AppSession session;
        try
        {
            paths = AppPaths.Resolve(AppPaths.ChannelOf(typeof(Program).Assembly));

            // The interface language comes before any text, the startup failure window included (requirements §10.5).
            UseStoredLanguage(paths);

            // One writer per data directory: take the lock before touching the database at all.
            instance = SingleInstance.TryAcquire(paths);
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
                    return ShowStartupFailure(args, UiText.AlreadyRunningNotResponding(paths.DataDirectory), 2);
                }
            }

            // One session of services per open database; restoring a backup in the app starts the next (AppSession).
            try
            {
                session = new AppSession(extra => CompositionRoot.BuildRoot(paths, overrides: extra), root => CompositionRoot.BuildView(root));
            }
            catch
            {
                // Released before the failure window opens: a relaunch meanwhile must not wait on a lock nobody serves.
                instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }
#pragma warning disable CA1031 // Before the window exists: whatever failed is shown in a window of its own, not only on a console a GUI launch lacks.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine(ex);
            return ShowStartupFailure(args, UiText.StartupFailed(ex.Message), 3);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(session, e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(session, e.Exception);

        // The host releases (and closes the database) only after the UI loop has ended; see AppHost.Run.
        var host = new AppHost(instance, session);
        return host.Run(() => BuildAvaloniaApp(host).StartWithClassicDesktopLifetime(args));
    }

    /// <summary>
    /// Mindweave could not start (health check 2026-11-12, errors-6): a small window says why, since a launch from the
    /// Finder or the Start menu has no console to read. Returns <paramref name="exitCode"/> when it is closed.
    /// </summary>
    private static int ShowStartupFailure(string[] args, string message, int exitCode)
    {
        try
        {
            AppBuilder.Configure(() => new App(startupFailure: message)).UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
        }
#pragma warning disable CA1031 // Even the window failed: the console line written before is all there is.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return exitCode;
    }

    /// <summary>Used by the Avalonia previewer; the real entry point passes a host.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();

    private static AppBuilder BuildAvaloniaApp(AppHost host) =>
        AppBuilder.Configure(() => new App(host)).UsePlatformDetect().WithInterFont();

#pragma warning disable CA1848 // Crash path: a plain call is clearer than a source-generated delegate.
    private static void LogCrash(AppSession session, Exception? exception)
    {
        // Through the current root; between two sessions, or after the last one, nothing is left to log to.
        session.Logger("Mindweave").LogCritical(exception, "Unhandled exception");
    }
#pragma warning restore CA1848
}
