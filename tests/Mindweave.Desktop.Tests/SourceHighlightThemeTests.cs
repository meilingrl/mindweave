using Avalonia;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// on it stays readable. 2026-11-10 (dark highlight still too dark): a clearer tint with its own text colour (7:1) and a
/// second cue, a line along the bottom of each highlighted line, recoloured with the theme like the tint.
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
        AssertPaint(block, ThemeVariant.Dark);
        Assert.True(Contrast(TextColour(block), HighlightColour(block)) >= 7, $"Text {TextColour(block)} on {HighlightColour(block)}");
        Screenshots.Capture(window, "r4-source-highlight-dark");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text()
    {
        await using var app = DesktopHarness.Create();
        var window = await ReturnToSourceAsync(app, ThemeVariant.Dark);
        var block = Highlighted(window);
        var line = Assert.Single(Mark(block).LineRects());
        var start = block.LayoutText.IndexOf("铁路随后", StringComparison.Ordinal);
        var text = Assert.Single(block.GetTextRangeBoundsInControl(start, "铁路随后".Length));
        // The highlighted text's line box, give or take the half pixel the line is snapped by.
        Assert.InRange(line.Left - text.Left, -0.5, 0.5);
        Assert.InRange(line.Right - text.Right, -0.5, 0.5);
        Assert.InRange(line.Bottom - text.Bottom, -0.5, 0.5);
        Assert.Equal(SourceHighlightMark.Thickness, line.Height, 3);

        // On screen: the line's middle is the line colour; just above it is the tint, not more line. The row above
        // runs through the bottom of the glyphs, and where their strokes reach depends on the platform's CJK font
        // (Windows 2026-11-12: one point 2 px above the line landed on a glyph), so the whole row is checked: no
        // pixel of it is the line colour, and the tint shows between the strokes.
        using (var frame = window.CaptureRenderedFrame()!)
        {
            var markColour = ThemeColour("SourceHighlightMark", ThemeVariant.Dark);
            var tint = Expected(ThemeVariant.Dark);
            var onLine = Pixel(frame, block, line.Center);
            Assert.True(Near(onLine, markColour), $"{onLine} is not the line colour {markColour}");
            var above = Enumerable.Range(0, (int)Math.Floor(line.Width) - 2)
                .Select(dx => Pixel(frame, block, new Point(line.Left + 1 + dx, line.Top - 2)))
                .ToList();
            Assert.DoesNotContain(above, p => Near(p, markColour));

            // How much tint shows between the strokes depends on the CJK font. Linux CI's fallback font fills that row
            // almost entirely (4 of 54 pixels tinted, every run since 2026-11-10); Linux is not a supported platform, so
            // there only the geometry above and the absence of line colour are checked (health check 2026-11-12, item 7).
            if (!OperatingSystem.IsLinux())
            {
                var tinted = above.Count(p => Near(p, tint));
                Assert.True(tinted * 4 >= above.Count, $"only {tinted} of {above.Count} pixels above the line are the tint {tint}");
            }
        }

        // Clearing the highlight takes the line away with it.
        SourceAnchors.ClearHighlight(CurrentView(window));
        Assert.Null(AdornerLayer.GetAdorner(block));
        Assert.DoesNotContain(window.GetVisualDescendants(), v => v is SourceHighlightMark);
        window.Close();
    }

    /// <summary>
    /// When the highlighted text rewraps (a narrower column), the line follows it onto every line it now spans: the
    /// line is measured again and redrawn, not left where the text used to end.
    /// </summary>
    [AvaloniaFact]
    public async Task The_line_under_the_highlight_follows_the_text_when_it_rewraps()
    {
        await using var app = DesktopHarness.Create();
        var window = await ReturnToSourceAsync(app, ThemeVariant.Dark);
        var block = Highlighted(window);
        var markColour = ThemeColour("SourceHighlightMark", ThemeVariant.Dark);
        using (var frame = window.CaptureRenderedFrame()!)
        {
            Assert.True(Near(Pixel(frame, block, Assert.Single(Mark(block).LineRects()).Center), markColour));
        }

        // Narrow enough that "铁路随后" is split over two lines, after three characters, then after one: the second
        // rewrap keeps the block's height, so only the text inside it moves.
        var start = block.LayoutText.IndexOf("铁路随后", StringComparison.Ordinal);
        block.Width = block.GetTextRangeBoundsInControl(start, 3)[0].Right + 2;
        window.UpdateLayout();
        using (window.CaptureRenderedFrame())
        {
        }

        var height = block.Bounds.Height;
        block.Width = block.GetTextRangeBoundsInControl(start, 1)[0].Right + 2;
        window.UpdateLayout();
        Assert.Equal(height, block.Bounds.Height);
        Assert.Single(block.GetTextRangeBoundsInControl(start, 1));
        Assert.Equal(2, block.GetTextRangeBoundsInControl(start, "铁路随后".Length).Count);
        var lines = Mark(block).LineRects();
        Assert.Equal(2, lines.Count);
        using (var frame = window.CaptureRenderedFrame()!)
        {
            foreach (var line in lines)
            {
                var onLine = Pixel(frame, block, line.Center);
                Assert.True(Near(onLine, markColour), $"{onLine} at {line} is not the line colour {markColour}");
            }
        }

        window.Close();
    }

    /// <summary>
    /// The line is drawn over the page, not inside the answer list, so it must still be cut off where the list is:
    /// scrolled just above the list's top edge it does not show over the toolbar, scrolled back it shows again.
    /// </summary>
    [AvaloniaFact]
    public async Task The_line_under_the_highlight_is_cut_off_with_the_text_when_scrolled_out_of_view()
    {
        await using var app = DesktopHarness.Create();
        var window = await ReturnToSourceAsync(app, ThemeVariant.Dark);
        var scroller = CurrentView(window).GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "Scroller");
        window.Height = 380;
        await UiWait.UntilAsync(() =>
        {
            window.UpdateLayout();
            return scroller.Extent.Height - scroller.Viewport.Height > 300;
        });
        var block = Highlighted(window);
        var line = Assert.Single(Mark(block).LineRects());
        var markColour = ThemeColour("SourceHighlightMark", ThemeVariant.Dark);
        var visible = scroller.Offset;

        // The line's top 6 px above the list's top edge: outside the list, over the page behind it.
        scroller.Offset = new Vector(0, scroller.Offset.Y + block.TranslatePoint(line.TopLeft, scroller)!.Value.Y + 6);
        window.UpdateLayout();
        Assert.True(block.TranslatePoint(line.Center, scroller)!.Value.Y < 0);
        using (var frame = window.CaptureRenderedFrame()!)
        {
            var outside = Pixel(frame, block, line.Center);
            Assert.False(Near(outside, markColour), $"{outside} outside the list is the line colour");
        }

        scroller.Offset = visible;
        window.UpdateLayout();
        using (var frame = window.CaptureRenderedFrame()!)
        {
            Assert.True(Near(Pixel(frame, block, line.Center), markColour));
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_theme_switch_while_the_source_is_highlighted_recolours_the_highlight()
    {
        await using var app = DesktopHarness.Create();
        var window = await ReturnToSourceAsync(app, ThemeVariant.Light);
        var block = Highlighted(window);
        AssertPaint(block, ThemeVariant.Light);
        Assert.True(Contrast(TextColour(block), HighlightColour(block)) >= 7);
        Screenshots.Capture(window, "r4-source-highlight-light");

        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.UpdateLayout();
        AssertPaint(Highlighted(window), ThemeVariant.Dark);
        Assert.True(Contrast(TextColour(Highlighted(window)), HighlightColour(Highlighted(window))) >= 7);

        window.RequestedThemeVariant = ThemeVariant.Light;
        window.UpdateLayout();
        AssertPaint(Highlighted(window), ThemeVariant.Light);
        window.Close();
    }

    /// <summary>
    /// The numbers behind the colours (WCAG contrast, recorded in s7-ui-polish.md): the highlight's text on its tint at
    /// least 7:1, ordinary body text on it still 4.5:1; the line at least 3:1 (non-text contrast) against both the tint
    /// and the page, so it shows even where the tint is faint; in dark the tint itself clearly apart from the page.
    /// </summary>
    [AvaloniaFact]
    public void The_highlight_colours_meet_their_contrast_targets()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var tint = ThemeColour("SourceHighlight", theme);
            var page = ThemeColour("WindowBackground", theme);
            var mark = ThemeColour("SourceHighlightMark", theme);
            AssertAtLeast(ThemeColour("SourceHighlightText", theme), tint, 7, $"{theme} highlight text");
            AssertAtLeast(ThemeColour("PrimaryText", theme), tint, 4.5, $"{theme} body text");
            AssertAtLeast(mark, tint, 3, $"{theme} line on the tint");
            AssertAtLeast(mark, page, 3, $"{theme} line on the page");
        }

        AssertAtLeast(ThemeColour("SourceHighlight", ThemeVariant.Dark), ThemeColour("WindowBackground", ThemeVariant.Dark), 2.3, "Dark tint on the page");
    }

    private static void AssertAtLeast(Color a, Color b, double ratio, string what) =>
        Assert.True(Contrast(a, b) >= ratio, $"{what}: {a} on {b} is {Contrast(a, b):0.00}:1, below {ratio}:1");

    /// <summary>The highlight on <paramref name="block"/> uses all three colours of <paramref name="theme"/>.</summary>
    private static void AssertPaint(MarkdownTextBlock block, ThemeVariant theme)
    {
        Assert.Equal(Expected(theme), HighlightColour(block));
        Assert.Equal(ThemeColour("SourceHighlightText", theme), TextColour(block));
        Assert.Equal(ThemeColour("SourceHighlightMark", theme), Assert.IsAssignableFrom<ISolidColorBrush>(Mark(block).Brush).Color);
    }

    private static SourceHighlightMark Mark(MarkdownTextBlock block) => Assert.IsType<SourceHighlightMark>(AdornerLayer.GetAdorner(block));

    private static Color Pixel(Avalonia.Media.Imaging.WriteableBitmap frame, Visual visual, Point point)
    {
        var at = visual.TranslatePoint(point, TopLevel.GetTopLevel(visual)!)!.Value;
        using var buffer = frame.Lock();
        var x = (int)Math.Round(at.X * frame.Dpi.X / 96);
        var y = (int)Math.Round(at.Y * frame.Dpi.Y / 96);
        var value = Marshal.ReadInt32(buffer.Address, (y * buffer.RowBytes) + (x * 4));
        var b = (byte)(value & 0xFF);
        var g = (byte)((value >> 8) & 0xFF);
        var r = (byte)((value >> 16) & 0xFF);
        return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromRgb(b, g, r) : Color.FromRgb(r, g, b);
    }

    private static bool Near(Color a, Color b, int tolerance = 12) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static async Task<MainWindow> ReturnToSourceAsync(DesktopHarness app, ThemeVariant theme)
    {
        var window = app.ShowWindow(theme: theme);
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

    /// <summary>The colour the highlighted text is drawn in: the highlight's own text colour, else the block's.</summary>
    private static Color TextColour(MarkdownTextBlock block)
    {
        Assert.True(block.HighlightStyles!.TryGetValue("branch-source", out var style));
        return Assert.IsAssignableFrom<ISolidColorBrush>(style!.Foreground ?? block.Foreground).Color;
    }

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
