using Avalonia.Controls;
using Avalonia.Headless;

namespace Mindweave.Desktop.Tests;

internal static class Screenshots
{
    /// <summary>Writes a rendered frame when MINDWEAVE_SCREENSHOT_DIR is set, for visual review.</summary>
    public static void Capture(Window window, string name)
    {
        var target = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        Directory.CreateDirectory(target);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(target, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
