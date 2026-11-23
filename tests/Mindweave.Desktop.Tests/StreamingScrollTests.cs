using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// An answer being written never moves the page (the owner, 2026-11-23: "Let the model's generation just make itself
/// longer; don't let it affect the page"). Sending puts the question at the top of the page once; from then on only the
/// reader scrolls, whatever comes and goes around the text: the reasoning fold, tool steps, the 出处 list, the row's
/// replacement when the answer ends.
/// </summary>
public class StreamingScrollTests
{
    private const string Pad = "这一段只是为了让回答足够长，需要滚动才能读完，以便检验生成时页面不跟着走。";

    private static string Paragraph(int i) => $"第{i}段：{Pad}";

    private static string Paragraphs(int from, int to) => string.Join("\n\n", Enumerable.Range(from, to - from + 1).Select(Paragraph));

    [AvaloniaTheory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public async Task A_follow_up_goes_to_the_top_and_stays_there_wherever_the_reader_was(double readerAt)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await LongConversationAsync(app, window);
        var scroller = Scroller(window);
        scroller.Offset = new Vector(0, (scroller.Extent.Height - scroller.Viewport.Height) * readerAt);
        await SettleAsync(window);

        var held = app.Client.Hold(Paragraph(1));
        await SendAsync(page, "再讲长一点？");
        await held.Started.Task;
        await StreamedAsync(window, page, 1);
        AssertQuestionAtTop(window, page);
        var offset = scroller.Offset.Y;
        for (var i = 2; i <= 40; i++)
        {
            held.Push("\n\n" + Paragraph(i));
            await StreamedAsync(window, page, i);
            Assert.Equal(offset, scroller.Offset.Y, 1);
        }

        held.Finish();
        await EndedAsync(window, page);
        AssertQuestionAtTop(window, page);
        Assert.Equal(offset, scroller.Offset.Y, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_short_answer_stays_put_when_it_ends_and_its_row_is_replaced()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await LongConversationAsync(app, window);
        var held = app.Client.Hold(Paragraph(1));
        await SendAsync(page, "一句话？");
        await held.Started.Task;
        await StreamedAsync(window, page, 1);
        AssertQuestionAtTop(window, page);
        var offset = Scroller(window).Offset.Y;
        held.Finish();
        await EndedAsync(window, page);
        Assert.Equal(offset, Scroller(window).Offset.Y, 1);
        AssertQuestionAtTop(window, page);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_reasoning_fold_opened_or_gone_does_not_move_the_question()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await LongConversationAsync(app, window);
        var held = app.Client.HoldThinking();
        await SendAsync(page, "再想想？");
        await held.Started.Task;
        await UiWait.UntilAsync(() => page.Messages[^1].HasReasoning);
        page.Messages[^1].ToggleReasoningCommand.Execute(null);
        await SettleAsync(window);
        AssertQuestionAtTop(window, page);
        var offset = Scroller(window).Offset.Y;
        for (var i = 0; i < 20; i++)
        {
            held.Think(Pad + "\n");
            await SettleAsync(window);
            Assert.Equal(offset, Scroller(window).Offset.Y, 1);
        }

        held.Push(Paragraph(1));
        await StreamedAsync(window, page, 1);
        Assert.Equal(offset, Scroller(window).Offset.Y, 1);
        held.Finish();
        await EndedAsync(window, page);
        Assert.Equal(offset, Scroller(window).Offset.Y, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_reader_at_the_end_of_a_growing_answer_is_not_carried_along()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await LongConversationAsync(app, window);
        var held = app.Client.Hold(Paragraphs(1, 30));
        await SendAsync(page, "再讲长一点？");
        await held.Started.Task;
        await StreamedAsync(window, page, 30);
        var scroller = Scroller(window);
        scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
        await SettleAsync(window);
        var offset = scroller.Offset.Y;
        for (var i = 31; i <= 40; i++)
        {
            held.Push("\n\n" + Paragraph(i));
            await StreamedAsync(window, page, i);
            Assert.Equal(offset, scroller.Offset.Y, 1);
        }

        held.Finish();
        await EndedAsync(window, page);
        Assert.Equal(offset, scroller.Offset.Y, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_text_being_read_stays_when_the_opened_reasoning_above_it_goes()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await LongConversationAsync(app, window);
        var held = app.Client.HoldThinking();
        await SendAsync(page, "再想想？");
        await held.Started.Task;
        await UiWait.UntilAsync(() => page.Messages[^1].HasReasoning);
        page.Messages[^1].ToggleReasoningCommand.Execute(null);
        held.Think(string.Concat(Enumerable.Repeat(Pad + "\n", 20)));
        held.Push(Paragraphs(1, 40));
        await StreamedAsync(window, page, 40);

        // The reader is in the middle of the text, the fold above the page (once the streamed text is laid out: it follows its arrival).
        var scroller = Scroller(window);
        await UiWait.UntilAsync(() =>
        {
            window.UpdateLayout();
            return scroller.Extent.Height - scroller.Viewport.Height > scroller.Offset.Y + 600;
        });
        scroller.Offset = new Vector(0, scroller.Offset.Y + 500);
        await SettleAsync(window);
        var before = TextTop(window);
        Assert.True(before < 0);
        held.Finish();
        await EndedAsync(window, page);
        Assert.Equal(before, TextTop(window), 1);
        window.Close();
    }

    private static async Task<ConversationViewModel> LongConversationAsync(DesktopHarness app, Window window)
    {
        app.Client.Reply(Paragraphs(1, 40));
        var page = await app.StartWithConversationAsync(waitForTitle: true);
        await CurrentView(window).Restored;
        await UiWait.UntilAsync(() => page.Messages[^1] is { State: MessageState.Complete, IsLive: false });
        await UiWait.AnswersRenderedAsync(window);
        await SettleAsync(window);
        Assert.True(Scroller(window).Extent.Height > Scroller(window).Viewport.Height * 2);
        return page;
    }

    private static async Task SendAsync(ConversationViewModel page, string question)
    {
        page.Composer.Text = question;
        await page.Composer.SendCommand.ExecuteAsync(null);
    }

    private static async Task StreamedAsync(Window window, ConversationViewModel page, int paragraph)
    {
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].Body.Contains($"第{paragraph}段", StringComparison.Ordinal));
        await SettleAsync(window);
    }

    private static async Task EndedAsync(Window window, ConversationViewModel page)
    {
        await UiWait.UntilAsync(() => page.Messages[^1] is { State: MessageState.Complete, IsLive: false });
        await UiWait.AnswersRenderedAsync(window);
        await SettleAsync(window);
    }

    private static async Task SettleAsync(Window window)
    {
        for (var settle = 0; settle < 3; settle++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }

    private static void AssertQuestionAtTop(Window window, ConversationViewModel page) =>
        Assert.InRange(RowTop(window, page.Messages.Count - 2), 0, 24);

    private static double RowTop(Window window, int row) =>
        Find<ItemsControl>(CurrentView(window), "MessageList").ContainerFromIndex(row)!.TranslatePoint(default, Scroller(window))!.Value.Y;

    private static double TextTop(Window window)
    {
        var list = Find<ItemsControl>(CurrentView(window), "MessageList");
        return list.ContainerFromIndex(list.ItemCount - 1)!.GetVisualDescendants().OfType<AnswerView>().First().TranslatePoint(default, Scroller(window))!.Value.Y;
    }
}
