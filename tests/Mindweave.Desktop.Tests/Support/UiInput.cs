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
    /// <param name="window">The window that receives the input.</param>
    /// <param name="target">What is clicked.</param>
    /// <param name="modifiers">Keys held during the click (for example ⇧ or ⌘ to extend a selection).</param>
    /// <param name="at">Where in <paramref name="target"/> to click, in its own coordinates; its centre when null.</param>
    /// <param name="hover">Moves the pointer there first, so the target is hovered when it is pressed, as with a real mouse.</param>
    public static void Click(Window window, Control target, RawInputModifiers modifiers = RawInputModifiers.None, Point? at = null, bool hover = false)
    {
        Settle();
        target.BringIntoView();
        window.UpdateLayout();
        var point = target.TranslatePoint(at ?? new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"{Describe(target)} is no longer in the window.");
        if (hover)
        {
            window.MouseMove(point, modifiers);
        }

        var enabled = target.IsEffectivelyEnabled;
        window.MouseDown(point, MouseButton.Left, modifiers);
        if (enabled && target is Button { IsPressed: false })
        {
            window.MouseUp(point, MouseButton.Left, modifiers);
            throw new InvalidOperationException($"The press did not reach {Describe(target)} (it was replaced or moved).");
        }

        window.MouseUp(point, MouseButton.Left, modifiers);
        window.UpdateLayout();
    }

    /// <summary>
    /// Waits out the platform's double-click time, so the next press is a single click even at the point of the last
    /// one. Two presses there within that time count as a double click, which selects a whole word (in CJK text, the
    /// whole run) and anchors a drag at its start.
    /// </summary>
    public static Task PastDoubleClickAsync()
    {
        var time = Avalonia.Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500);
        return Task.Delay(time + TimeSpan.FromMilliseconds(50));
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
