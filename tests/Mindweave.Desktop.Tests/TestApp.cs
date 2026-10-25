using Avalonia;
using Avalonia.Headless;
using Mindweave.Desktop;

[assembly: AvaloniaTestApplication(typeof(Mindweave.Desktop.Tests.TestAppBuilder))]

namespace Mindweave.Desktop.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering so tests can capture frames of the actual UI.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
