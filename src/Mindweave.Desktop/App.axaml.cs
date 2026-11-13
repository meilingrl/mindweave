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
        if (_host is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var session = _host.Session;
            var window = new MainWindow { DataContext = session.Root };
            window.Follow(session);
            desktop.MainWindow = window;

            // Before anything runs on the dispatcher: an exception a command does not handle is reported, not fatal.
            // The session is looked up each time, since an in-app restore replaces it.
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
        e.Cancel |= root.Exit.InterceptClose();
    }
}
