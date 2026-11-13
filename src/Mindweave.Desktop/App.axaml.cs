using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;

namespace Mindweave.Desktop;

public sealed partial class App : Avalonia.Application
{
    private readonly AppHost? _host;

    /// <summary>Used by the XAML loader and by headless tests, which create their own windows.</summary>
    public App()
    {
    }

    internal App(AppHost host) => _host = host;

    /// <summary>An application that only says why Mindweave could not start (see Program.ShowStartupFailure).</summary>
    internal App(string startupFailure) => _startupFailure = startupFailure;

    private readonly string? _startupFailure;

    public override void Initialize()
    {
        Markdown.MarkdownSetup.EnsureRegistered();
        AvaloniaXamlLoader.Load(this);

        // Hover, press and selection transitions (s7-ui-polish.md, 2026-11-10); left out of headless tests and screenshots.
        if (Motion.Enabled)
        {
            Styles.Add(Motion.LoadStyles());
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (_startupFailure is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime failed)
        {
            failed.MainWindow = new StartupFailureWindow(_startupFailure);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (_host is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var session = _host.Session;
            var window = new MainWindow { DataContext = session.Root };
            window.Follow(session);
            desktop.MainWindow = window;

            // Before anything runs on the dispatcher: an exception a command does not handle is reported, not fatal.
            // The session is looked up each time, since an in-app restore replaces it.
            DesktopLog.Use(_host.Session.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mindweave.Desktop"));
            var guard = new UiExceptionGuard(
                Dispatcher.UIThread,
                () => _host.Session.Services.GetService<ShellViewModel>(),
                _host.Session.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mindweave.UI"));
            desktop.Exit += (_, _) => guard.Dispose();

            // The session's root: a restore in the app (2026-11-12) replaces it, and quitting goes through the current one.
            desktop.ShutdownRequested += (_, e) => OnShutdownRequested(session.Root, e);
            _host.Instance.StartListening(() => Dispatcher.UIThread.Post(window.BringToFront));
            Dispatcher.UIThread.Post(async () => await session.Services.GetRequiredService<AppStartup>().RunAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// ⌘Q, the dock's "quit" and the system's shutdown request arrive here rather than at the window. They take the
    /// same exit sequence as closing the window; only the coordinator's own final shutdown is let through.
    /// </summary>
    internal static void OnShutdownRequested(RootViewModel root, ShutdownRequestedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(e);
        if (IsSystemShutdown(e) && root.Exit.EndForSystem() is { } saving)
        {
            // Logout, restart or shutdown with nothing to ask (health check 2026-11-12, gap-lifecycle-2): save now and
            // let the system go ahead, instead of vetoing it and quitting a moment later on our own.
            if (!WaitWithinLimit(saving, SystemShutdownSaveLimit))
            {
                // Not saved in time: the request is held after all, and the application quits once saving ends.
                e.Cancel = true;
                root.Exit.SystemSaveTimedOut();
            }

            return;
        }

        e.Cancel |= root.Exit.InterceptClose();
    }

    /// <summary>
    /// Whether the request comes from the system (logout, restart, shutdown) rather than ⌘Q or the dock. Avalonia 12.1.4
    /// tracks this but does not make it public, so it is read by reflection; when it cannot be read the request is treated
    /// as a quit, which asks as before.
    /// </summary>
    internal static bool IsSystemShutdown(ShutdownRequestedEventArgs e) =>
        IsOsShutdownProperty?.GetValue(e) is true;

    private static readonly System.Reflection.PropertyInfo? IsOsShutdownProperty =
        typeof(ShutdownRequestedEventArgs).GetProperty("IsOSShutdown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    /// <summary>How long a system shutdown waits for saving; what is not written by then is lost, as a forced end would lose it.</summary>
    internal static TimeSpan SystemShutdownSaveLimit { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Keeps the UI loop running (the save steps continue on it) until <paramref name="task"/> ends or the limit passes.</summary>
    /// <returns>Whether <paramref name="task"/> ended within the limit.</returns>
    private static bool WaitWithinLimit(Task task, TimeSpan limit)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        var timer = DispatcherTimer.RunOnce(() => frame.Continue = false, limit);
        Dispatcher.UIThread.PushFrame(frame);
        timer.Dispose();
        return task.IsCompleted;
    }
}
