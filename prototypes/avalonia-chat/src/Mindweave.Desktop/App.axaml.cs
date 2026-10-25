using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Mindweave.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = OpenDesktopAsync(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task OpenDesktopAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var directory = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MindweavePrototype")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindweavePrototype");
        var file = Program.DataFileOverride ?? Path.Combine(directory, "state.json");
        var workspace = await WorkspaceViewModel.OpenSavedWorkspaceAsync(file);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = new MainWindow(workspace);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, e) =>
            {
                if (window.CloseApproved) return;
                e.Cancel = true;
                _ = window.RequestCloseAsync();
            };
            window.Show();
        });
    }
}
