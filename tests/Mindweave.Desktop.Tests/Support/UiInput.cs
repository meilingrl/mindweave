using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
        // A tooltip that opens while the pointer rests here (its delay runs out on a slow runner) lies in the headless overlay
        // right over the point and takes the press; in the app it is its own window beside the pointer. Closing it is not
        // enough: the press runs due timers first, and the tooltip's own can open it again. So tooltips are off during the
        // click and come back after it.
        var tipped = new List<(Control Control, bool Enabled)>();
        for (Visual? v = target; v is not null; v = v.GetVisualParent())
        {
            if (v is Control c && ToolTip.GetTip(c) is not null)
            {
                tipped.Add((c, ToolTip.GetServiceEnabled(c)));
                ToolTip.SetServiceEnabled(c, false);
                ToolTip.SetIsOpen(c, false);
            }
        }

        try
        {
            ClickCore(window, target, modifiers, at, hover);
        }
        finally
        {
            foreach (var (control, enabled) in tipped)
            {
                ToolTip.SetServiceEnabled(control, enabled);
            }
        }
    }

    private static void ClickCore(Window window, Control target, RawInputModifiers modifiers, Point? at, bool hover)
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
        var clicked = false;
        void OnClick(object? sender, RoutedEventArgs e) => clicked = true;
        var button = target as Button;
        if (button is not null)
        {
            button.Click += OnClick;
        }

        try
        {
            window.MouseDown(point, MouseButton.Left, modifiers);
            if (enabled && button is { IsPressed: false } && !clicked)
            {
                window.MouseUp(point, MouseButton.Left, modifiers);
                throw new InvalidOperationException($"The press did not reach {Describe(target)} (it was replaced or moved).");
            }

            window.MouseUp(point, MouseButton.Left, modifiers);

            // A button clicks on release only if the release lands on it too: content that moved in between (an answer
            // above it rendered, say) takes the click away without a word, and the test would wait for its effect in vain.
            if (enabled && button is not null && !clicked)
            {
                throw new InvalidOperationException($"The release did not reach {Describe(target)} (it moved while pressed).");
            }
        }
        finally
        {
            if (button is not null)
            {
                button.Click -= OnClick;
            }
        }

        window.UpdateLayout();
    }

    /// <summary>
    /// Waits out the platform's double-click time, so the next press is a single click even at the point of the last
    /// one. Two presses there within that time count as a double click, which selects a whole word (in CJK text, the
    /// whole run) and anchors a drag at its start.
    /// </summary>
    public static Task PastDoubleClickAsync() => Task.Delay(DoubleClickTime + TimeSpan.FromMilliseconds(50));

    /// <summary>The platform's double-click time: two presses at one point within it make a double click.</summary>
    public static TimeSpan DoubleClickTime =>
        Avalonia.Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500);

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
