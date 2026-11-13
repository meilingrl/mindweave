using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Mindweave.Desktop.Views;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>Health check 2026-11-12, errors-6: a failure before the main window exists is shown in a window of its own.</summary>
public class StartupFailureWindowTests
{
    [AvaloniaFact]
    public void The_failure_window_says_why_and_closes()
    {
        var message = UiText.StartupFailed("Access to the path '/data/instance.lock' is denied.");
        var window = new StartupFailureWindow(message);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        window.UpdateLayout();

        Assert.Equal(UiText.StartupFailedTitle, window.Title);
        Assert.Equal(message, Find<SelectableTextBlock>(window, "StartupFailureMessage").Text);
        UiInput.Click(window, Find<Button>(window, "StartupFailureClose"));
        Assert.True(closed);
    }
}
