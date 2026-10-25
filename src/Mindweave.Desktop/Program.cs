using Avalonia;
using Mindweave.Infrastructure.Runtime;

namespace Mindweave.Desktop;
internal static class Program
{
    internal static AppPaths Paths { get; private set; } = null!;
    internal static SingleInstance Instance { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 0 && (args.Length != 2 || args[0] != "--data-root"))
                throw new ArgumentException("用法：Mindweave.Desktop [--data-root 测试数据根目录]");
            Paths = AppPaths.ForTrack("codex-steady", args.Length == 2 ? args[1] : null);
            using var instance = SingleInstance.TryAcquire(Paths);
            if (instance is null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                SingleInstance.NotifyExistingAsync(Paths, timeout.Token).GetAwaiter().GetResult();
                return 0;
            }
            Instance = instance;
            return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnMainWindowClose);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Mindweave 无法启动：{error.Message}");
            return 1;
        }
    }
}
