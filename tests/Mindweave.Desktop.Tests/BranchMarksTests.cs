using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// S7 round 4 (your Windows test, finding 5): in a long conversation, marks in the margin beside the scroll bar lead to
/// each answer's branch list, so the way into a branch is never buried.
/// </summary>
public class BranchMarksTests
{
    private const string Pad = "这一段只是为了让回答足够长，长到整段对话需要滚动好几屏才能读完。";

    private static string LongAnswer(int n) => string.Join("\n\n", Enumerable.Range(1, 10).Select(i => $"回答{n}第{i}段：{Pad}"));

    [AvaloniaFact]
    public async Task Each_answer_with_branches_has_one_mark_at_its_place_named_with_the_count_and_first_quote()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var page = await LongConversationAsync(app, window, answers: 4);
        await BranchAsync(app, page, page.Messages[1], "第一个分支");
        await BranchAsync(app, page, page.Messages[1], "第二个分支");
        await BranchAsync(app, page, page.Messages[5], "第三个分支");
        await UiWait.UntilAsync(() => Marks(window).Marks.Count == 2);

        var marks = Marks(window).Marks;
        Assert.Same(page.Messages[1], marks[0].Answer);
        Assert.Same(page.Messages[5], marks[1].Answer);
        Assert.Equal(UiText.BranchMark(2, "第一个分支"), ToolTip.GetTip(marks[0].Mark));
        Assert.Equal("2 个分支 · 「第一个分支」", AutomationProperties.GetName(marks[0].Mark));
        Assert.Equal("1 个分支 · 「第三个分支」", AutomationProperties.GetName(marks[1].Mark));
        AssertPlaced(window);

        // The hit target is at least 8 px tall, and a real hit test at its centre finds the mark.
        foreach (var (_, mark) in marks)
        {
            Assert.True(mark.Bounds.Height >= 8);
            Rendered(window);
            var centre = mark.TranslatePoint(new Point(mark.Bounds.Width / 2, mark.Bounds.Height / 2), window)!.Value;
            Assert.Same(mark, (window.InputHitTest(centre) as Visual)?.FindAncestorOfType<Button>(includeSelf: true));
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_a_mark_scrolls_its_answers_branch_list_into_view()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var page = await LongConversationAsync(app, window, answers: 4);
        await BranchAsync(app, page, page.Messages[1], "最早的分支");
        await UiWait.UntilAsync(() => Marks(window).Marks.Count == 1);
        var scroller = Scroller(window);
        scroller.ScrollToEnd();
        window.UpdateLayout();
        var section = Section(window, page.Messages[1]);
        Assert.False(InView(scroller, section));

        Click(window, Marks(window).Marks[0].Mark);
        Assert.True(InView(scroller, section), $"Branch list at {section.TranslatePoint(default, scroller)?.Y} in a {scroller.Viewport.Height} viewport");

        // The entries themselves are there to open.
        Assert.Contains(section.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("entry") && b.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Marks_stay_off_the_scroll_bar_and_dragging_the_thumb_still_scrolls()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var page = await LongConversationAsync(app, window, answers: 4);
        await BranchAsync(app, page, page.Messages[1], "分支");
        await BranchAsync(app, page, page.Messages[3], "分支二");
        await BranchAsync(app, page, page.Messages[5], "分支三");
        await BranchAsync(app, page, page.Messages[7], "分支四");
        await UiWait.UntilAsync(() => Marks(window).Marks.Count == 4);
        var scroller = Scroller(window);
        scroller.Offset = default;
        window.UpdateLayout();

        var bar = VerticalBar(scroller);
        var barRect = Rect(bar, window);
        var content = Rect(CurrentView(window).GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "MessageList"), window);
        foreach (var (_, mark) in Marks(window).Marks)
        {
            Assert.True(Rect(mark, window).Right <= barRect.Left, $"Mark {Rect(mark, window)} overlaps the scroll bar {barRect}");
            Assert.True(Rect(mark, window).Left >= content.Right, $"Mark {Rect(mark, window)} covers the messages {content}");
        }

        // Drag the thumb down: a real pointer drag on it scrolls, whatever marks are beside it.
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
        window.MouseMove(Centre(thumb, window));
        Rendered(window);
        var from = Centre(thumb, window);
        Assert.Same(thumb, (window.InputHitTest(from) as Visual)?.FindAncestorOfType<Thumb>(includeSelf: true));
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(0, 120), RawInputModifiers.LeftMouseButton);
        window.MouseUp(from + new Point(0, 120), MouseButton.Left);
        window.UpdateLayout();
        Assert.True(scroller.Offset.Y > 200, $"Offset after the drag: {scroller.Offset.Y}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Marks_follow_streaming_text_a_new_branch_a_resize_and_the_theme()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var page = await LongConversationAsync(app, window, answers: 3);
        await BranchAsync(app, page, page.Messages[3], "中间的分支");
        await UiWait.UntilAsync(() => Marks(window).Marks.Count == 1);
        AssertPlaced(window);
        var before = Canvas.GetTop(Marks(window).Marks[0].Mark);

        // A new answer streams in below: the conversation grows, the mark moves up along the track.
        var held = app.Client.Hold(LongAnswer(9));
        page.Composer.Text = "再问一个";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => page.Messages.Count == 8 && page.Messages[7].Body.Length > 100);
        await UiWait.UntilAsync(() => Canvas.GetTop(Marks(window).Marks[0].Mark) < before - 10);
        AssertPlaced(window);
        held.Release.TrySetResult();
        await UiWait.UntilAsync(() => page.Messages[7] is { State: MessageState.Complete, IsLive: false });

        // A branch from another answer adds its own mark.
        await BranchAsync(app, page, page.Messages[1], "开头的分支");
        await UiWait.UntilAsync(() => Marks(window).Marks.Count == 2);
        Assert.Same(page.Messages[1], Marks(window).Marks[0].Answer);
        AssertPlaced(window);

        // A taller window: same places along a longer track.
        window.Height = 900;
        window.UpdateLayout();
        AssertPlaced(window);

        // The bar takes the theme's colour.
        var bar = Marks(window).Marks[0].Mark.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("branch-mark-bar"));
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.UpdateLayout();
        Assert.Equal(Colour("BranchMark", ThemeVariant.Dark), ((ISolidColorBrush)bar.Background!).Color);
        window.RequestedThemeVariant = ThemeVariant.Light;
        window.UpdateLayout();
        Assert.Equal(Colour("BranchMark", ThemeVariant.Light), ((ISolidColorBrush)bar.Background!).Color);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_conversation_that_fits_on_screen_shows_no_marks()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Client.Reply("很短的回答，可以从中分出探索。");
        var page = await AskFirstAsync(app, window, "短问题");
        await BranchAsync(app, page, page.Messages[1], "很短的回答");
        await UiWait.UntilAsync(() => page.Messages[1].Branches.Count == 1);
        window.UpdateLayout();
        Assert.True(Section(window, page.Messages[1]).IsEffectivelyVisible);
        Assert.Empty(Marks(window).Marks);
        window.Close();
    }

    [AvaloniaFact]
    public void The_marks_stand_out_from_the_page_in_both_themes()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var ratio = SourceHighlightThemeTests.Contrast(Colour("BranchMark", theme), Colour("WindowBackground", theme));
            Assert.True(ratio >= 3, $"{theme}: mark on the page is {ratio:0.0}:1");
        }
    }

    /// <summary>Each mark sits where the scroll thumb's top would be with its answer's branch list at the top of the viewport.</summary>
    private static void AssertPlaced(Window window)
    {
        window.UpdateLayout();
        var marks = Marks(window);
        var scroller = Scroller(window);
        var track = VerticalBar(scroller).GetVisualDescendants().OfType<Track>().Single();
        var trackTop = track.TranslatePoint(default, marks)!.Value.Y;
        var previous = double.NegativeInfinity;
        foreach (var (answer, mark) in marks.Marks)
        {
            var sectionTop = Section(window, answer).TranslatePoint(default, scroller)!.Value.Y + scroller.Offset.Y;
            var expected = trackTop + (sectionTop / scroller.Extent.Height * track.Bounds.Height) - (BranchMarks.MarkHeight / 2);
            expected = Math.Max(Math.Clamp(expected, trackTop, trackTop + track.Bounds.Height - BranchMarks.MarkHeight), previous + 2);
            Assert.InRange(Canvas.GetTop(mark), expected - 0.5, expected + 0.5);
            previous = Canvas.GetTop(mark) + BranchMarks.MarkHeight;
        }
    }

    private static async Task<ConversationViewModel> LongConversationAsync(DesktopHarness app, Window window, int answers)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Client.Reply(LongAnswer(1));
        var page = await AskFirstAsync(app, window, "第1问");
        for (var i = 2; i <= answers; i++)
        {
            app.Client.Reply(LongAnswer(i));
            page.Composer.Text = $"第{i}问";
            await page.Composer.SendCommand.ExecuteAsync(null);
            var count = i * 2;
            await UiWait.UntilAsync(() => page.Messages.Count == count && page.Messages[^1] is { State: MessageState.Complete, IsLive: false });
        }

        window.UpdateLayout();
        return page;
    }

    private static async Task<ConversationViewModel> AskFirstAsync(DesktopHarness app, Window window, string question)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1] is { State: MessageState.Complete, IsLive: false });
        await UiWait.UntilAsync(() => CurrentView(window).Restored.IsCompleted);
        return page;
    }

    /// <summary>A branch from an answer through the service (the page stays); its anchor is not used here.</summary>
    private static async Task BranchAsync(DesktopHarness app, ConversationViewModel page, MessageItemViewModel answer, string quote)
    {
        var count = answer.Branches.Count;
        var outcome = await app.Services.GetRequiredService<IConversations>().CreateBranchAsync(
            page.Id, new BranchSelection(answer.Id, 0, quote, "{}", SourceAnchors.FormatVersion, SourceAnchors.RenderProfile), CancellationToken.None);
        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        await UiWait.UntilAsync(() => answer.Branches.Count == count + 1);
    }

    private static MainWindow Open(DesktopHarness app)
    {
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        return window;
    }

    private static ConversationView CurrentView(Window window) => window.GetVisualDescendants().OfType<ConversationView>().Single();

    private static BranchMarks Marks(Window window)
    {
        window.UpdateLayout();
        return CurrentView(window).GetVisualDescendants().OfType<BranchMarks>().Single();
    }

    private static ScrollViewer Scroller(Window window) => CurrentView(window).GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "Scroller");

    private static ScrollBar VerticalBar(ScrollViewer scroller) =>
        scroller.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);

    private static Border Section(Window window, MessageItemViewModel answer) =>
        CurrentView(window).GetVisualDescendants().OfType<Border>().Single(b => b.Name == "BranchSection" && b.DataContext == answer);

    private static bool InView(ScrollViewer scroller, Visual section)
    {
        var top = section.TranslatePoint(default, scroller)!.Value.Y;
        return top >= 0 && top + section.Bounds.Height <= scroller.Viewport.Height;
    }

    private static Rect Rect(Visual visual, Visual root) => new(visual.TranslatePoint(default, root)!.Value, visual.Bounds.Size);

    private static Point Centre(Visual visual, Visual root) => visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), root)!.Value;

    private static void Rendered(Window window)
    {
        window.UpdateLayout();
        window.CaptureRenderedFrame()?.Dispose();
    }

    private static void Click(Window window, Button button)
    {
        Rendered(window);
        var centre = Centre(button, window);
        window.MouseMove(centre);
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        window.UpdateLayout();
    }

    private static Color Colour(string key, ThemeVariant theme)
    {
        Assert.True(Avalonia.Application.Current!.Resources.TryGetResource(key, theme, out var value));
        return ((ISolidColorBrush)value!).Color;
    }
}
