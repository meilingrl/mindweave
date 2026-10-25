using Avalonia;

namespace Mindweave.Desktop;

internal static class Program
{
    public static string? DataFileOverride { get; private set; }
    [STAThread]
    public static void Main(string[] args)
    {
        var index = Array.IndexOf(args, "--data-file");
        DataFileOverride = index >= 0 && index + 1 < args.Length ? args[index + 1] : Environment.GetEnvironmentVariable("MINDWEAVE_PROTOTYPE_DATA_FILE");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
