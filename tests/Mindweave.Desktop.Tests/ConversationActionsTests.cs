using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Testing;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// S7 round 3 (conversation): the branch action floats next to the selection, every message has a quiet time and a
/// one-click copy of its Markdown source, and stop is an icon in the composer.
/// </summary>
public partial class ConversationActionsTests
{
    private const string Pad = "这一段只是为了让回答有一定长度，使选中的文字落在回答中间。";

    /// <summary>Markdown with emphasis and a list, so a copy can be checked against the source, not the rendered text.</summary>
    private static readonly string Answer = string.Join("\n\n",
        Enumerable.Range(1, 4).Select(i => $"第{i}段：{Pad}")
            .Append("蒸汽机车由**斯蒂芬森**改良，铁路随后在英国普及。")
            .Append("- 多管锅炉\n- 鼓风管")
            .Concat(Enumerable.Range(5, 3).Select(i => $"第{i}段：{Pad}")));

    [Fact]
    public void Message_times_show_only_the_clock_today_and_the_full_date_otherwise()
    {
        var now = new DateTimeOffset(new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Local));
        var morning = new DateTimeOffset(new DateTime(2026, 11, 2, 9, 5, 0, DateTimeKind.Local));
        Assert.Equal("09:05", UiText.MessageTime(morning, now));
        Assert.Equal("09:05", UiText.MessageTime(morning.ToUniversalTime(), now.ToUniversalTime()));

        // Yesterday late in the evening is not "today", even a few hours ago; older years keep their year.
        var yesterday = new DateTimeOffset(new DateTime(2026, 11, 1, 23, 40, 0, DateTimeKind.Local));
        Assert.Equal("2026年11月1日 23:40", UiText.MessageTime(yesterday, now));
        var lastYear = new DateTimeOffset(new DateTime(2025, 3, 4, 7, 8, 0, DateTimeKind.Local));
        Assert.Equal("2025年3月4日 07:08", UiText.MessageTime(lastYear, now));
        Assert.DoesNotContain("M", UiText.MessageTime(now.AddHours(5), now), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Selecting_text_floats_the_branch_action_next_to_it_and_clearing_the_selection_hides_it()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var page = await ConversationAsync(app, window);
        var answer = AnswerOf(window);

        Select(window, answer, "铁路随后");
        var actions = Find<Border>(answer, "SelectionActions");
        var branch = Find<Button>(answer, "BranchButton");
        Assert.True(actions.IsEffectivelyVisible);
        Assert.True(branch.IsEffectivelyVisible);
        AssertNextToSelection(answer, actions);

        // Nothing is left at the bottom of the answer: the action is the only branch entry and sits inside the text.
        var answerBottom = answer.TranslatePoint(new Point(0, answer.Bounds.Height), window)!.Value.Y;
        Assert.True(actions.TranslatePoint(new Point(0, actions.Bounds.Height), window)!.Value.Y < answerBottom - 40);

        // A plain click elsewhere in the text clears the selection, and the action goes with it.
        var other = SourceAnchors.Blocks(answer.Renderer).First(b => b.LayoutText.StartsWith("第1段", StringComparison.Ordinal));
        var elsewhere = other.TranslatePoint(new Point(20, other.Bounds.Height / 2), window)!.Value;
        window.MouseDown(elsewhere, MouseButton.Left);
        window.MouseUp(elsewhere, MouseButton.Left);
        window.UpdateLayout();
        Assert.False(SourceAnchors.HasSelection(answer.Renderer));
        Assert.False(actions.IsEffectivelyVisible);
        Assert.Equal(2, page.Messages.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_the_floating_action_creates_the_branch_with_the_same_anchor_as_the_selection()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app, window);
        var answer = AnswerOf(window);
        Select(window, answer, "斯蒂芬森");
        var expected = answer.CaptureSelection();
        Assert.NotNull(expected);

        // A real pointer click: pressing the action moves focus out of the text, and the branch is still made from the selection.
        Click(window, Find<Button>(answer, "BranchButton"));

        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel page && page.Id != root.Id);
        var child = (ConversationViewModel)app.Workbench.CurrentPage!;
        await child.Loaded;
        var source = await app.Services.GetRequiredService<IConversations>().GetSourceAsync(child.Id, CancellationToken.None);
        Assert.NotNull(source);
        Assert.Equal(expected.SourceMessage, source.SourceMessage);
        Assert.Equal(expected.Quote, source.Quote);
        Assert.Equal(expected.AnchorJson, source.AnchorJson);
        Assert.Equal(expected.AnchorFormatVersion, source.AnchorFormatVersion);
        Assert.Equal(expected.RenderProfile, source.RenderProfile);
        Assert.Equal("斯蒂芬森", child.SourceQuote);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Copy_puts_the_exact_markdown_source_of_a_question_and_an_answer_on_the_clipboard_and_confirms_briefly()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var page = await ConversationAsync(app, window);
        var view = CurrentView(window);
        var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;

        var answerCopy = VisibleNamed<CopyButton>(view, "AnswerCopy");
        Assert.Equal(UiText.Copy, ToolTip.GetTip(answerCopy));
        Assert.Empty(answerCopy.GetVisualDescendants().OfType<TextBlock>());
        Click(window, answerCopy);
        await UiWait.UntilAsync(() => answerCopy.ShowsCopied);
        var copiedAt = DateTime.UtcNow;
        Assert.Equal(Answer, await clipboard.TryGetTextAsync());
        Assert.Equal(UiText.Copied, ToolTip.GetTip(answerCopy));

        // The check goes back to the copy icon after a moment.
        await UiWait.UntilAsync(() => !answerCopy.ShowsCopied, timeoutMs: 5_000);
        Assert.True(DateTime.UtcNow - copiedAt >= TimeSpan.FromSeconds(1.2));
        Assert.Equal(UiText.Copy, ToolTip.GetTip(answerCopy));

        var questionCopy = VisibleNamed<CopyButton>(view, "QuestionCopy");
        Click(window, questionCopy);
        await UiWait.UntilAsync(() => questionCopy.ShowsCopied);
        Assert.Equal(page.Messages[0].Body, await clipboard.TryGetTextAsync());
        Assert.Equal("火车是怎么发明的？\n**第二行**", await clipboard.TryGetTextAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_stopped_partial_answer_can_be_copied_and_a_failed_answer_without_text_cannot()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("半句**粗体**");
        var page = await AskAsync(app, window, "问");
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].Body == "半句**粗体**");
        window.UpdateLayout();
        var view = CurrentView(window);

        // While it runs there is nothing final to copy or date: the row under it is hidden.
        Assert.Null(page.Messages[1].TimestampText);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<CopyButton>(), b => b.Name == "AnswerCopy" && b.IsEffectivelyVisible);

        page.StopCommand.Execute(null);
        await UiWait.UntilAsync(() => page.Messages[1].State == MessageState.Stopped);
        window.UpdateLayout();
        var copy = VisibleNamed<CopyButton>(view, "AnswerCopy");
        Assert.True(copy.IsEffectivelyEnabled);
        Click(window, copy);
        await UiWait.UntilAsync(() => copy.ShowsCopied);
        Assert.Equal("半句**粗体**", await TopLevel.GetTopLevel(window)!.Clipboard!.TryGetTextAsync());

        // A failure before any text: the row is there (with its time), copy is disabled.
        app.Client.Fail(GenerationErrorKind.Network, "connection reset");
        await page.RetryCommand.ExecuteAsync(page.Messages[1]);
        await UiWait.UntilAsync(() => page.Messages.Count == 3 && page.Messages[2].State == MessageState.Failed);
        window.UpdateLayout();
        Assert.False(page.Messages[2].CanCopy);
        var failedCopy = view.GetVisualDescendants().OfType<CopyButton>().Last(b => b.Name == "AnswerCopy" && b.IsEffectivelyVisible);
        Assert.Same(page.Messages[2], failedCopy.DataContext);
        Assert.False(failedCopy.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Questions_and_finished_answers_show_their_time_quietly()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var page = await ConversationAsync(app, window);
        var view = CurrentView(window);

        // Sent today, so only the clock; the answer's time is its end.
        var question = VisibleNamed<TextBlock>(view, "QuestionTime");
        var answer = VisibleNamed<TextBlock>(view, "AnswerTime");
        Assert.Matches(Clock(), question.Text!);
        Assert.Matches(Clock(), answer.Text!);
        Assert.Equal(UiText.MessageTime(page.Messages[0].SentAt, DateTimeOffset.Now), question.Text);
        Assert.Equal(UiText.MessageTime(page.Messages[1].SentAt, DateTimeOffset.Now), answer.Text);
        var stored = (await app.Services.GetRequiredService<IConversations>().LoadAsync(page.Id, CancellationToken.None))!.Messages;
        Assert.Equal(stored[0].CreatedAt, page.Messages[0].SentAt);
        Assert.Equal(stored[1].CompletedAt, page.Messages[1].SentAt);
        Assert.Contains("timestamp", answer.Classes);

        // Beside the answer's own actions, in the same quiet row.
        Assert.Same(VisibleNamed<StackPanel>(view, "AnswerActions"), answer.GetVisualParent());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Stop_is_an_icon_with_its_name_and_tooltip()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("半句");
        var page = await AskAsync(app, window, "问");
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => page.RunningAnswer is not null);
        window.UpdateLayout();

        var stop = VisibleNamed<Button>(CurrentView(window), "StopButton");
        Assert.DoesNotContain(stop.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));
        Assert.Single(stop.GetVisualDescendants().OfType<PathIcon>());
        Assert.Equal(UiText.Stop, ToolTip.GetTip(stop));
        Assert.Equal(UiText.Stop, Avalonia.Automation.AutomationProperties.GetName(stop));

        // Round, the same height as the input's single line.
        Assert.Equal(stop.Bounds.Width, stop.Bounds.Height, 0.5);
        held.Release.TrySetResult();
        await UiWait.UntilAsync(() => page.RunningAnswer is null);
        window.Close();
    }

    [GeneratedRegex(@"^\d{2}:\d{2}$")]
    private static partial Regex Clock();

    /// <summary>The action sits just above the selection's first line at its left edge, or just below its last line when there is no room above.</summary>
    private static void AssertNextToSelection(AnswerView answer, Border actions)
    {
        var lines = SourceAnchors.SelectionRects(answer.Renderer, answer);
        Assert.NotEmpty(lines);
        var first = lines[0];
        var last = lines[^1];
        var origin = actions.TranslatePoint(default, answer)!.Value;
        var size = actions.Bounds.Size;
        Assert.Equal(Math.Clamp(first.X, 0, answer.Bounds.Width - size.Width), origin.X, 0.5);

        var scroller = answer.FindAncestorOfType<ScrollViewer>()!;
        var roomAbove = answer.TranslatePoint(new Point(0, first.Y - size.Height - AnswerView.ActionGap), scroller)!.Value.Y >= 0;
        if (roomAbove)
        {
            Assert.Equal(first.Y - AnswerView.ActionGap, origin.Y + size.Height, 0.5);
        }
        else
        {
            Assert.Equal(last.Bottom + AnswerView.ActionGap, origin.Y, 0.5);
        }

        // On screen.
        var top = actions.TranslatePoint(default, scroller)!.Value.Y;
        Assert.InRange(top, 0, scroller.Viewport.Height - size.Height);
    }

    private static AnswerView AnswerOf(Window window) =>
        window.GetVisualDescendants().OfType<AnswerView>().Last(a => a.DataContext is MessageItemViewModel { IsAnswer: true });

    /// <summary>Drags across <paramref name="quote"/> with the mouse, keeping the block in the middle of the page.</summary>
    private static void Select(Window window, AnswerView answer, string quote)
    {
        var block = SourceAnchors.Blocks(answer.Renderer).First(b => b.LayoutText.Contains(quote, StringComparison.Ordinal));
        var scroller = answer.FindAncestorOfType<ScrollViewer>()!;
        var y = block.TranslatePoint(default, scroller)!.Value.Y;
        scroller.Offset = new Vector(0, Math.Max(0, scroller.Offset.Y + y - (scroller.Viewport.Height / 2)));
        window.UpdateLayout();
        var start = block.LayoutText.IndexOf(quote, StringComparison.Ordinal);
        var end = start + quote.Length;
        var from = block.GetTextRangeBoundsInControl(start, 1)[0];
        var to = block.GetTextRangeBoundsInControl(end, 1)[0];
        var down = block.TranslatePoint(new Point(from.X + 1, from.Center.Y), window)!.Value;
        var up = block.TranslatePoint(new Point(to.X + 1, to.Center.Y), window)!.Value;
        window.MouseDown(down, MouseButton.Left);
        window.MouseMove(up, RawInputModifiers.LeftMouseButton);
        window.MouseUp(up, MouseButton.Left);
        window.UpdateLayout();
        Assert.Equal(quote, block.LayoutText[Math.Min(block.SelectionStart, block.SelectionEnd)..Math.Max(block.SelectionStart, block.SelectionEnd)]);
    }

    /// <summary>A real pointer click at the button's centre.</summary>
    private static void Click(Window window, Button button)
    {
        button.BringIntoView();
        window.UpdateLayout();
        var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        window.UpdateLayout();
    }

    private static MainWindow Open(DesktopHarness app)
    {
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        return window;
    }

    private static async Task<ConversationViewModel> AskAsync(DesktopHarness app, Window window, string question)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        window.UpdateLayout();
        return page;
    }

    /// <summary>A finished exchange whose question has two lines of Markdown and whose answer is <see cref="Answer"/>, laid out.</summary>
    private static async Task<ConversationViewModel> ConversationAsync(DesktopHarness app, Window window)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Client.Reply(Answer);
        var page = await AskAsync(app, window, "火车是怎么发明的？\n**第二行**");
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        await UiWait.UntilAsync(() => page.Title == FakeChatClient.DefaultTitle);
        await CurrentView(window).Restored;
        await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<AnswerView>().Any(a => a is { IsLayoutCurrent: true, DataContext: MessageItemViewModel { CanBranch: true } }));
        window.UpdateLayout();
        return page;
    }

    private static ConversationView CurrentView(Window window) => window.GetVisualDescendants().OfType<ConversationView>().Single();

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static T VisibleNamed<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name && c.IsEffectivelyVisible);
}
