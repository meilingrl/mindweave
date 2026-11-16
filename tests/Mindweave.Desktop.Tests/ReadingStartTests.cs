using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;
using Scene = Mindweave.Desktop.Tests.WorkspaceNewsHintTests.Scene;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// New content is read from its first sentence (direction 1, item 1, approved 2026-11-17): an answer the reader has not
/// seen opens at its start, not its end, and an answer being written is followed only until its start reaches the top
/// of the page. Saved reading positions, branch return and return to source are unchanged (BranchUiTests,
/// ProgressiveOpenTests).
/// </summary>
public class ReadingStartTests
{
    private const string Pad = "这一段只是为了让回答足够长，需要滚动才能读完，以便检验新内容是否从第一句开始读。";

    private static string Paragraph(int i) => $"第{i}段：{Pad}";

    private static readonly string LongAnswer = string.Join("\n\n", Enumerable.Range(1, 40).Select(Paragraph));

    [AvaloniaFact]
    public async Task Opening_an_unread_answer_from_its_hint_shows_the_start_of_that_answer()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window) = (scene.App, scene.Window);
        app.Client.Reply(LongAnswer);
        var answered = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        window.UpdateLayout();

        UiInput.Click(window, Find<Button>(window, "OpenNewsToast"));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(answered, page.Id);
        await CurrentView(window).Restored;
        window.UpdateLayout();
        Screenshots.Capture(window, "reading-start-from-hint");

        AssertStartsAtTop(window, page, answer: 1);
        Assert.False(IsAtBottom(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_long_answer_being_written_is_followed_until_its_start_reaches_the_top()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await app.StartWithConversationAsync(waitForTitle: true);
        await CurrentView(window).Restored;

        var held = app.Client.Hold(Paragraph(1));
        page.Composer.Text = "再讲长一点？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task;
        await StreamedAsync(window, page, 1);

        // At first the answer is short: the page follows it, so the reader sees it grow.
        await PushAsync(window, page, held, 2, 3);
        Screenshots.Capture(window, "reading-start-stream-1-start");
        Assert.True(IsAtBottom(window));

        // Longer than the page: it stops once the answer's start reaches the top, and the first sentence stays there.
        await PushAsync(window, page, held, 4, 22);
        Screenshots.Capture(window, "reading-start-stream-2-middle");
        AssertStartsAtTop(window, page, answer: 3);
        Assert.False(IsAtBottom(window));

        await PushAsync(window, page, held, 23, 40);
        AssertStartsAtTop(window, page, answer: 3);

        held.Finish();
        await UiWait.UntilAsync(() => page.Messages[3].State == MessageState.Complete);
        await UiWait.AnswersRenderedAsync(window);
        window.UpdateLayout();
        Screenshots.Capture(window, "reading-start-stream-3-end");
        AssertStartsAtTop(window, page, answer: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_reader_who_scrolls_to_the_end_while_an_answer_is_written_keeps_following_it()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await app.StartWithConversationAsync(waitForTitle: true);
        await CurrentView(window).Restored;

        var held = app.Client.Hold(Paragraph(1));
        page.Composer.Text = "再讲长一点？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task;
        await PushAsync(window, page, held, 2, 22);
        AssertStartsAtTop(window, page, answer: 3);

        // The reader goes to the end themselves: from then on the page follows the new text there, as before.
        var scroller = Scroller(window);
        scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
        window.UpdateLayout();
        await PushAsync(window, page, held, 23, 28);
        Assert.True(IsAtBottom(window));

        // And scrolled up to read something, they stay there.
        scroller.Offset = new Vector(0, scroller.Offset.Y - 200);
        window.UpdateLayout();
        var reading = scroller.Offset.Y;
        await PushAsync(window, page, held, 29, 32);
        Assert.Equal(reading, scroller.Offset.Y, 1);

        held.Finish();
        await UiWait.UntilAsync(() => page.Messages[3].State == MessageState.Complete);
        window.Close();
    }

    /// <summary>Streams paragraphs <paramref name="from"/> to <paramref name="to"/>, one at a time, each laid out before the next.</summary>
    private static async Task PushAsync(Window window, ConversationViewModel page, FakeChatClient.HeldReply held, int from, int to)
    {
        for (var i = from; i <= to; i++)
        {
            held.Push("\n\n" + Paragraph(i));
            await StreamedAsync(window, page, i);
        }
    }

    private static async Task StreamedAsync(Window window, ConversationViewModel page, int paragraph)
    {
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].Body.Contains($"第{paragraph}段", StringComparison.Ordinal));
        for (var settle = 0; settle < 3; settle++)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }

    /// <summary>
    /// The answer in row <paramref name="answer"/> is read from its start: its (short) question at the top of the page and
    /// the answer's first sentence right below it.
    /// </summary>
    private static void AssertStartsAtTop(Window window, ConversationViewModel page, int answer)
    {
        Assert.True(page.Messages[answer].IsAnswer);
        Assert.True(page.Messages[answer - 1].IsUser);
        Assert.InRange(RowTop(window, answer - 1), 0, 24);
        Assert.InRange(RowTop(window, answer), 0, Scroller(window).Viewport.Height / 3);
    }

    private static double RowTop(Window window, int row) =>
        Find<ItemsControl>(CurrentView(window), "MessageList").ContainerFromIndex(row)!.TranslatePoint(default, Scroller(window))!.Value.Y;

    private static bool IsAtBottom(Window window)
    {
        var scroller = Scroller(window);
        return scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 24;
    }
}
