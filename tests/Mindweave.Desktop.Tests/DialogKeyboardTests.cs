using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Desktop.Views;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Health check 2026-11-12, item 11: a prompt over the window is modal for the keyboard too. It takes the focus when
/// it opens (wherever the focus was), Tab never leaves it, Esc cancels, keys never reach the page behind it, and the
/// focus goes back where it was when it closes.
/// </summary>
public class DialogKeyboardTests
{
    [AvaloniaFact]
    public async Task The_quit_prompt_takes_the_keyboard_from_the_page_and_gives_it_back()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        await ExitScenario.RunningAnswerAsync(app);
        var before = Find<Button>(window, "NewConversationButton");
        before.Focus();

        Assert.True(ExitScenario.Quit(app, window, ExitEntry.Lifetime));
        Dispatcher.UIThread.RunJobs();

        var prompt = Find<Border>(window, "ExitTaskPrompt");
        Assert.True(Find<Button>(window, "ExitCancel").IsFocused);
        for (var i = 0; i < 5; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.True(IsInside(Focused(window), prompt), $"Tab {i + 1} left the prompt for {Focused(window)}");
        }

        // A key aimed at the page behind (focus put there by other means) does not reach it.
        before.Focus();
        var page = app.Workbench.CurrentPage;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(page, app.Workbench.CurrentPage);
        Assert.True(IsInside(Focused(window), prompt));

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(app.Root.Exit.IsTaskPromptOpen);
        Assert.True(before.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_unsaved_layout_prompt_takes_the_keyboard_and_esc_cancels()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await ExitScenario.DirtyLayoutAsync(app, window);
        Find<Button>(window, "NewConversationButton").Focus();

        Assert.True(ExitScenario.Quit(app, window, ExitEntry.Lifetime));
        Dispatcher.UIThread.RunJobs();

        var prompt = Find<Border>(window, "LayoutPromptDialog");
        Assert.True(IsInside(Focused(window), prompt));
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(IsInside(Focused(window), prompt));

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(app.Root.LayoutPrompt.IsOpen);
        window.Close();
    }

    private static IInputElement? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    private static bool IsInside(IInputElement? element, Visual container) =>
        element is Visual visual && (ReferenceEquals(visual, container) || visual.GetVisualAncestors().Contains(container));
}
