using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace Mindweave.Desktop.Tests;

internal static class UiInput
{
    /// <summary>
    /// Clicks the centre of <paramref name="target"/>. Pending dispatcher work (a page reload, a row being replaced)
    /// runs before the point is measured: the headless press runs it anyway, and a point measured earlier can then land
    /// on something else. A press that still misses an enabled button fails here, by name, instead of as a later wait
    /// that never ends.
    /// </summary>
    public static void Click(Window window, Control target)
    {
        Settle();
        target.BringIntoView();
        window.UpdateLayout();
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"{Describe(target)} is no longer in the window.");
        var enabled = target.IsEffectivelyEnabled;
        window.MouseDown(centre, MouseButton.Left);
        if (enabled && target is Button { IsPressed: false })
        {
            window.MouseUp(centre, MouseButton.Left);
            throw new InvalidOperationException($"The press did not reach {Describe(target)} (it was replaced or moved).");
        }

        window.MouseUp(centre, MouseButton.Left);
        window.UpdateLayout();
    }

    /// <summary>What the headless press does before it delivers the input: run queued jobs and render, a few rounds.</summary>
    private static void Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (!Dispatcher.UIThread.HasJobsWithPriority(DispatcherPriority.Background))
            {
                return;
            }
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static string Describe(Visual visual) => visual is Control { Name: { Length: > 0 } name } ? $"{visual.GetType().Name} '{name}'" : visual.GetType().Name;
}
