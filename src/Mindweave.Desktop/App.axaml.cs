using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Mindweave.Infrastructure.Storage;

namespace Mindweave.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow window = null!;
            var actions = new DesktopActions(Program.Paths.DataDirectory, () => window.Close());
            var root = new RootViewModel(async () => await SqliteWorkspaceStore.OpenAsync(Program.Paths), actions, Program.Paths.DataDirectory,
                store => DesktopChatSession.Create((Mindweave.Application.Chat.IChatStore)store, Program.Paths.CredentialNamespace));
            window = new MainWindow { DataContext = root };
            desktop.MainWindow = window;
            var activation = new CancellationTokenSource();
            var listener = Program.Instance.ListenAsync(() => Dispatcher.UIThread.Post(() =>
            {
                window.Show();
                if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
                window.Activate();
            }), activation.Token);
            window.Opened += async (_, _) => await root.InitializeAsync();
            desktop.Exit += (_, _) =>
            {
                activation.Cancel();
                try { listener.GetAwaiter().GetResult(); }
                catch (Exception error) { Console.Error.WriteLine($"激活服务已停止：{error.Message}"); }
                activation.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
