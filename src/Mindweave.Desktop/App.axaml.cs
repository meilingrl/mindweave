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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (_host is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = _host.Services.GetRequiredService<RootViewModel>() };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _host.Instance.StartListening(() => Dispatcher.UIThread.Post(window.BringToFront));
            Dispatcher.UIThread.Post(async () => await _host.Services.GetRequiredService<AppStartup>().RunAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
