using Avalonia;
using Avalonia.Headless;
using Mindweave.Desktop;

[assembly: AvaloniaTestApplication(typeof(Mindweave.Desktop.Tests.TestAppBuilder))]

// One application and dispatcher for the whole assembly. Recreating them per test (the default) occasionally
// failed in Avalonia's own setup: the new compositor joined a render loop still owned by the previous test's
// thread ("The calling thread cannot access this object", 2 of about 40 full runs). Every test builds its own
// window and services, so nothing of ours is shared.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Mindweave.Desktop.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering so tests can capture frames of the actual UI.
    // Spinners draw one fixed frame and motion is off, so screenshots and layout never depend on when a frame was taken.
    public static AppBuilder BuildAvaloniaApp()
    {
        Views.ActivitySpinner.Animates = false;
        Views.WeavingIndicator.Animates = false;
        // Nothing fades or slides: every frame and every layout read shows a settled state.
        Views.Motion.Enabled = false;
        return AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
