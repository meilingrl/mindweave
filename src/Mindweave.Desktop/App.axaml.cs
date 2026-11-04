using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
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
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (_host is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var root = _host.Services.GetRequiredService<RootViewModel>();
            var window = new MainWindow { DataContext = root };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, e) => OnShutdownRequested(root, e);
            _host.Instance.StartListening(() => Dispatcher.UIThread.Post(window.BringToFront));
            Dispatcher.UIThread.Post(async () => await _host.Services.GetRequiredService<AppStartup>().RunAsync());
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
