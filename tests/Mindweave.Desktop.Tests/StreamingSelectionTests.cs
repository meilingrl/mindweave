using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>Selecting text with the mouse in an answer that is still being written (copying is allowed then).</summary>
public class StreamingSelectionTests
{
    [AvaloniaFact]
    public async Task Dragging_across_a_streaming_answer_selects_what_the_pointer_covers()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var held = app.Client.Hold("正在增长的回答文字");
        var page = await AskAsync(app, held);
        var answer = await StreamingAnswerAsync(window, page);

        var block = SourceAnchors.Blocks(answer.Renderer).Single();
        Drag(window, block, 2, 4);

        Assert.Equal((2, 4), Selected(block));
        held.Finish();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_selection_in_a_streaming_answer_is_kept_while_more_text_arrives()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var held = app.Client.Hold("正在增长的回答文字");
        var page = await AskAsync(app, held);
        var answer = await StreamingAnswerAsync(window, page);
        var block = SourceAnchors.Blocks(answer.Renderer).Single();
        Drag(window, block, 2, 4);

        held.Push("，同一段继续写");
        await GrownAsync(window, page, answer, "继续写");
        held.Push("\n\n第二段开始了。");
        await GrownAsync(window, page, answer, "第二段开始了。");

        var first = SourceAnchors.Blocks(answer.Renderer)[0];
        Assert.Same(block, first);
        Assert.Equal((2, 4), Selected(first));
        Assert.Equal("增长", answer.Renderer.SelectedText);
        held.Finish();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Text_arriving_during_a_drag_does_not_move_its_start()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var held = app.Client.Hold("正在增长的回答文字");
        var page = await AskAsync(app, held);
        var answer = await StreamingAnswerAsync(window, page);
        var block = SourceAnchors.Blocks(answer.Renderer).Single();
        var (down, up) = Points(window, block, 2, 4);

        window.MouseMove(down);
        window.MouseDown(down, MouseButton.Left);
        held.Push("，同一段继续写");
        await GrownAsync(window, page, answer, "继续写");
        window.MouseMove(up, RawInputModifiers.LeftMouseButton);
        window.MouseUp(up, MouseButton.Left);
        window.UpdateLayout();

        Assert.Equal((2, 4), Selected(SourceAnchors.Blocks(answer.Renderer)[0]));
        held.Finish();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_selection_made_while_streaming_survives_the_answer_finishing()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var held = app.Client.Hold("正在增长的回答文字");
        var page = await AskAsync(app, held);
        var answer = await StreamingAnswerAsync(window, page);
        Drag(window, SourceAnchors.Blocks(answer.Renderer).Single(), 2, 4);

        held.Finish("，写完了。");
        await UiWait.StoredAsync(page, 1, Mindweave.Core.Conversations.MessageState.Complete);
        await UiWait.UntilAsync(() => TryAnswer(window) is { IsLayoutCurrent: true, DataContext: MessageItemViewModel { IsLive: false } });
        window.UpdateLayout();

        // The stored answer can be branched from, so the carried selection offers 创建分支 like any other.
        var stored = TryAnswer(window)!;
        Assert.Equal("增长", stored.Renderer.SelectedText);
        Assert.True(stored.BranchButton.IsEffectivelyVisible);
        Assert.Equal("增长", stored.CaptureSelection()?.Quote);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_carried_selection_whose_text_the_finished_answer_shows_differently_is_dropped()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);

        // Until the emphasis is closed its markers are shown as text; the finished answer shows them as bold instead,
        // so the selected characters sit at a different place and nothing is selected rather than the wrong text.
        var held = app.Client.Hold("正在**增长的回答");
        var page = await AskAsync(app, held);
        var answer = await StreamingAnswerAsync(window, page);
        var block = SourceAnchors.Blocks(answer.Renderer).Single();
        Assert.Equal("正在**增长的回答", block.LayoutText);
        Drag(window, block, 4, 6);
        Assert.Equal("增长", answer.Renderer.SelectedText);

        held.Finish("**文字。");
        await UiWait.StoredAsync(page, 1, Mindweave.Core.Conversations.MessageState.Complete);
        await UiWait.UntilAsync(() => TryAnswer(window) is { IsLayoutCurrent: true, DataContext: MessageItemViewModel { IsLive: false } });
        window.UpdateLayout();

        var stored = TryAnswer(window)!;
        Assert.Equal("正在增长的回答文字。", SourceAnchors.Blocks(stored.Renderer).Single().LayoutText);
        Assert.Equal(string.Empty, stored.Renderer.SelectedText);
        Assert.False(stored.BranchButton.IsEffectivelyVisible);
        window.Close();
    }

    private static async Task GrownAsync(Window window, ConversationViewModel page, AnswerView answer, string tail)
    {
        await UiWait.UntilAsync(() => page.Messages[1].Body.EndsWith(tail, StringComparison.Ordinal) && answer.Markdown.EndsWith(tail, StringComparison.Ordinal) && answer.IsLayoutCurrent);
        window.UpdateLayout();
    }

    private static async Task<MainWindow> OpenAsync(DesktopHarness app)
    {
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        window.UpdateLayout();
        return window;
    }

    private static async Task<ConversationViewModel> AskAsync(DesktopHarness app, FakeChatClient.HeldReply held)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "问";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        return page;
    }

    private static async Task<AnswerView> StreamingAnswerAsync(Window window, ConversationViewModel page)
    {
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1] is { IsStreaming: true });
        await UiWait.UntilAsync(() => TryAnswer(window) is { IsLayoutCurrent: true } a && SourceAnchors.Blocks(a.Renderer).Count > 0);
        window.UpdateLayout();
        return TryAnswer(window)!;
    }

    /// <summary>Window points just inside the characters at <paramref name="start"/> and <paramref name="end"/>.</summary>
    private static (Point Down, Point Up) Points(Window window, MarkdownTextBlock block, int start, int end)
    {
        block.BringIntoView();
        window.UpdateLayout();
        var from = block.GetTextRangeBoundsInControl(start, 1)[0];
        var to = block.GetTextRangeBoundsInControl(end, 1)[0];
        return (block.TranslatePoint(new Point(from.X + 1, from.Center.Y), window)!.Value, block.TranslatePoint(new Point(to.X + 1, to.Center.Y), window)!.Value);
    }

    private static void Drag(Window window, MarkdownTextBlock block, int start, int end)
    {
        var (down, up) = Points(window, block, start, end);
        window.MouseDown(down, MouseButton.Left);
        window.MouseMove(up, RawInputModifiers.LeftMouseButton);
        window.MouseUp(up, MouseButton.Left);
        window.UpdateLayout();
    }

    private static (int Start, int End) Selected(MarkdownTextBlock block) =>
        (Math.Min(block.SelectionStart, block.SelectionEnd), Math.Max(block.SelectionStart, block.SelectionEnd));

    private static AnswerView? TryAnswer(Window window) =>
        window.GetVisualDescendants().OfType<ConversationView>().SingleOrDefault()?.GetVisualDescendants().OfType<AnswerView>()
            .LastOrDefault(a => a.DataContext is MessageItemViewModel { IsAnswer: true });
}
