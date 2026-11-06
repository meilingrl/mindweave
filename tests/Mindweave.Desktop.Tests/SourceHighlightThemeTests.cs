using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// S7 round 4 (your Windows test, finding 4): after 返回来源 the source passage is highlighted in the colours of the
/// theme the page is actually shown in — a dark window, or a theme switched while the highlight is on — and the text
/// on it stays readable (at least 4.5:1).
/// </summary>
public class SourceHighlightThemeTests
{
    private const string Pad = "这一段只是为了让回答足够长，需要滚动才能读完，以便检验来源定位与高亮。";

    private static readonly string LongAnswer = string.Join("\n\n",
        Enumerable.Range(1, 8).Select(i => $"第{i}段：{Pad}")
            .Append("蒸汽机车由**斯蒂芬森**改良，铁路随后在英国普及。")
            .Concat(Enumerable.Range(9, 8).Select(i => $"第{i}段：{Pad}")));

    [AvaloniaFact]
    public async Task In_a_dark_window_the_source_highlight_uses_the_dark_colour_and_the_text_on_it_is_readable()
    {
        await using var app = DesktopHarness.Create();
        var window = await ReturnToSourceAsync(app, ThemeVariant.Dark);

        var block = Highlighted(window);
        Assert.Equal(ThemeVariant.Dark, block.ActualThemeVariant);
        Assert.Equal(Expected(ThemeVariant.Dark), HighlightColour(block));
        Assert.True(Contrast(TextColour(block), HighlightColour(block)) >= 4.5, $"Text {TextColour(block)} on {HighlightColour(block)}");
        ViewTests.Capture(window, "r4-source-highlight-dark");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_theme_switch_while_the_source_is_highlighted_recolours_the_highlight()
    {
        await using var app = DesktopHarness.Create();
        var window = await ReturnToSourceAsync(app, ThemeVariant.Light);
        var block = Highlighted(window);
        Assert.Equal(Expected(ThemeVariant.Light), HighlightColour(block));
        Assert.True(Contrast(TextColour(block), HighlightColour(block)) >= 4.5);
        ViewTests.Capture(window, "r4-source-highlight-light");

        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.UpdateLayout();
        Assert.Equal(Expected(ThemeVariant.Dark), HighlightColour(Highlighted(window)));
        Assert.True(Contrast(TextColour(Highlighted(window)), HighlightColour(Highlighted(window))) >= 4.5);

        window.RequestedThemeVariant = ThemeVariant.Light;
        window.UpdateLayout();
        Assert.Equal(Expected(ThemeVariant.Light), HighlightColour(Highlighted(window)));
        window.Close();
    }

    [AvaloniaFact]
    public void Both_highlight_colours_keep_the_body_text_readable()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var text = ThemeColour("PrimaryText", theme);
            var highlight = ThemeColour("SourceHighlight", theme);
            Assert.True(Contrast(text, highlight) >= 4.5, $"{theme}: {text} on {highlight} is {Contrast(text, highlight):0.0}:1");
        }
    }

    private static async Task<MainWindow> ReturnToSourceAsync(DesktopHarness app, ThemeVariant theme)
    {
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700, RequestedThemeVariant = theme };
        window.Show();
        await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();

        app.Client.Reply(LongAnswer);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "火车是怎么发明的？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var root = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => root.Messages.Count == 2 && root.Messages[1].CanBranch);
        await CurrentView(window).Restored;
        await UiWait.UntilAsync(() => Answer(window) is { IsLayoutCurrent: true, DataContext: MessageItemViewModel { CanBranch: true } });
        window.UpdateLayout();

        Select(window, Answer(window)!, "铁路随后");
        Answer(window)!.BranchButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel page && page.Id != root.Id);
        var child = (ConversationViewModel)app.Workbench.CurrentPage!;
        await child.Loaded;
        await UiWait.UntilAsync(() => CurrentView(window).DataContext == child && CurrentView(window).Restored.IsCompleted);

        child.ReturnToSourceCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel page && page.Id == root.Id);
        var back = (ConversationViewModel)app.Workbench.CurrentPage!;
        await back.Loaded;
        await UiWait.UntilAsync(() => CurrentView(window).DataContext == back && CurrentView(window).Restored.IsCompleted);
        window.UpdateLayout();
        Assert.Null(back.SourceNotice);
        return window;
    }

    private static void Select(Window window, AnswerView answer, string quote)
    {
        var block = SourceAnchors.Blocks(answer.Renderer).First(b => b.LayoutText.Contains(quote, StringComparison.Ordinal));
        block.BringIntoView();
        window.UpdateLayout();
        var start = block.LayoutText.IndexOf(quote, StringComparison.Ordinal);
        var from = block.GetTextRangeBoundsInControl(start, 1)[0];
        var to = block.GetTextRangeBoundsInControl(start + quote.Length, 1)[0];
        var down = block.TranslatePoint(new Point(from.X + 1, from.Center.Y), window)!.Value;
        var up = block.TranslatePoint(new Point(to.X + 1, to.Center.Y), window)!.Value;
        window.MouseDown(down, MouseButton.Left);
        window.MouseMove(up, RawInputModifiers.LeftMouseButton);
        window.MouseUp(up, MouseButton.Left);
        window.UpdateLayout();
    }

    private static MarkdownTextBlock Highlighted(Window window) =>
        Assert.Single(CurrentView(window).GetVisualDescendants().OfType<MarkdownTextBlock>(), b => b.Highlights.TryGetValue("branch-source", out _));

    private static Color HighlightColour(MarkdownTextBlock block)
    {
        Assert.NotNull(block.HighlightStyles);
        Assert.True(block.HighlightStyles!.TryGetValue("branch-source", out var style));
        return Assert.IsAssignableFrom<ISolidColorBrush>(style!.Background).Color;
    }

    private static Color TextColour(MarkdownTextBlock block) => Assert.IsAssignableFrom<ISolidColorBrush>(block.Foreground).Color;

    private static Color Expected(ThemeVariant theme) => ThemeColour("SourceHighlight", theme);

    private static AnswerView? Answer(Window window) =>
        CurrentView(window).GetVisualDescendants().OfType<AnswerView>().LastOrDefault(a => a.DataContext is MessageItemViewModel { IsAnswer: true });

    /// <summary>WCAG contrast ratio of two opaque colours.</summary>
    internal static double Contrast(Color a, Color b)
    {
        static double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        var (high, low) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (high + 0.05) / (low + 0.05);
    }
}
