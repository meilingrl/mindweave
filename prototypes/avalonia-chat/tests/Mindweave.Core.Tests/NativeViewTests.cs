using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Core;
using Mindweave.Desktop;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Mindweave.Core.Tests.TestAppBuilder))]

namespace Mindweave.Core.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class NativeViewTests
{
    // Exercises our actual pointer-to-selection-to-popup-to-branch adapter, not a VM shortcut.
    [AvaloniaFact]
    public void Dragging_formatted_text_then_clicking_followup_creates_a_branch()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var workspace = (WorkspaceViewModel)window.DataContext!;
        var text = Assert.Single(window.GetVisualDescendants().OfType<SelectableTextBlock>(), x => string.Concat(x.Inlines!.OfType<Run>().Select(r => r.Text)).StartsWith("19 世纪初"));
        var displayText = string.Concat(text.Inlines!.OfType<Run>().Select(r => r.Text));
        var start = displayText.IndexOf("蒸汽机", StringComparison.Ordinal);
        var left = text.TextLayout.HitTestTextPosition(start);
        var right = text.TextLayout.HitTestTextPosition(start + 3);
        var from = text.TranslatePoint(new Point(left.X + 1, left.Y + left.Height / 2), window)!.Value;
        var to = text.TranslatePoint(new Point(right.X + 1, right.Y + right.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Assert.Equal("蒸汽机", text.SelectedText);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var popup = Assert.Single(window.GetLogicalDescendants().OfType<Popup>(), x => x.IsOpen);
        var button = (Button)((Border)popup.Child!).Child!;
        var popupTop = TopLevel.GetTopLevel(button)!;
        var clickPoint = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), popupTop)!.Value;
        popupTop.MouseDown(clickPoint, MouseButton.Left);
        popupTop.MouseUp(clickPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("蒸汽机", workspace.Current.Source!.Quote);
        Assert.Equal("rail-history", workspace.Current.Source.ConversationId);
        Assert.Equal(5, workspace.State.Conversations.Count);
        window.Close();
    }

    // Catches assigning list/code text to two visual parents during initial rendering.
    [AvaloniaFact]
    public void Demo_window_renders_lists_quotes_and_code_inside_the_narrow_viewport()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), x => x.TextLayout.Height > 0);
        window.Width = 1000;
        Dispatcher.UIThread.RunJobs();
        var scroll = window.FindControl<ScrollViewer>("ChatScroll")!;
        foreach (var text in window.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            var right = text.TranslatePoint(new Point(text.Bounds.Width, 0), scroll)!.Value.X;
            Assert.InRange(right, 0, scroll.Bounds.Width);
            Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1);
        }
        window.Close();
    }

    // Catches returning to the top of a long paragraph while the chosen words are off screen.
    [AvaloniaFact]
    public void Return_to_source_scrolls_the_selected_line_into_the_viewport()
    {
        var window = new MainWindow();
        window.Show();
        var workspace = (WorkspaceViewModel)window.DataContext!;
        workspace.NewConversation(new(null, 0), "");
        var longParagraph = string.Concat(Enumerable.Repeat("This is a long line about a railway and its engine. ", 150)) + "SOURCE-WORDS";
        workspace.Current.Messages.Add(new("long-answer", false, longParagraph + "\n\n" + string.Concat(Enumerable.Repeat("More context below the chosen words.\n\n", 25))));
        workspace.SwitchTo(workspace.Current.Id, new(null, 0), "");
        Dispatcher.UIThread.RunJobs();
        workspace.Branch("long-answer", "b0", longParagraph.Length - 12, longParagraph.Length, new(null, 0), "");
        Dispatcher.UIThread.RunJobs();
        window.Width = 1000;
        Dispatcher.UIThread.RunJobs();
        workspace.ReturnToSource(new(null, 0), "");
        Dispatcher.UIThread.RunJobs();
        var selected = Assert.Single(window.GetVisualDescendants().OfType<SelectableTextBlock>(), x => x.SelectedText == "SOURCE-WORDS");
        var scroll = window.FindControl<ScrollViewer>("ChatScroll")!;
        var caret = selected.TextLayout.HitTestTextPosition(selected.SelectionStart);
        var point = selected.TranslatePoint(new Point(0, caret.Y), scroll)!.Value;
        Assert.InRange(point.Y, 0, scroll.Viewport.Height);
        window.Width = 1440;
        Dispatcher.UIThread.RunJobs();
        var widePoint = selected.TranslatePoint(new Point(0, selected.TextLayout.HitTestTextPosition(selected.SelectionStart).Y), scroll)!.Value;
        Assert.InRange(widePoint.Y, 0, scroll.Viewport.Height);
        window.Close();
    }
}
