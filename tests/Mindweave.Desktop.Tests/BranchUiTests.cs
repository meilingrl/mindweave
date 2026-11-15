using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Requirements §11, first four rows, through the real window: native mouse selection in rendered Markdown,
/// real layout (Skia), real navigation and storage. Only the AI service is faked.
/// </summary>
public class BranchUiTests
{
    private const string Pad = "这一段只是为了让回答足够长，需要滚动才能读完，以便检验阅读位置与来源定位是否真的生效。";

    /// <summary>A long answer: the passage to branch from sits in the middle, far from both ends.</summary>
    private static readonly string LongAnswer = string.Join("\n\n",
        Enumerable.Range(1, 12).Select(i => $"第{i}段：{Pad}")
            .Append("蒸汽机车由**斯蒂芬森**改良，铁路随后在英国普及。")
            .Concat(Enumerable.Range(13, 12).Select(i => $"第{i}段：{Pad}")));

    [AvaloniaFact]
    public async Task Selecting_a_passage_creates_a_branch_that_continues_with_its_background()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var root = await StartConversationAsync(app, window, "火车是怎么发明的？", LongAnswer);

        var child = await BranchAsync(app, window, root, "铁路随后");

        Assert.True(child.IsBranch);
        Assert.Equal("铁路随后", child.SourceQuote);
        Assert.Equal(root.Title, Assert.Single(child.Lineage).Title);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2);
        Assert.Equal([0, 1], app.ShellPage.Conversations.Items.Select(i => i.Depth));
        Assert.Equal("「铁路随后」", app.ShellPage.Conversations.Items[1].Title);

        child.Composer.Text = "它最先通往哪里？";
        await child.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => child.Messages.Count == 2 && child.Messages[1].State == MessageState.Complete);
        var sent = app.Client.Requests.Last(r => r.Turns[0].Role != ChatRole.System);
        Assert.Equal(["火车是怎么发明的？", LongAnswer, "关于上文中的这段内容：\n> 铁路随后\n\n它最先通往哪里？"], sent.Turns.Select(t => t.Content));

        window.UpdateLayout();
        Screenshots.Capture(window, "04-branch");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Returning_from_a_second_level_branch_reaches_each_source_in_turn()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var root = await StartConversationAsync(app, window, "火车是怎么发明的？", LongAnswer);
        var child = await BranchAsync(app, window, root, "斯蒂芬森");
        app.Client.Reply("斯蒂芬森是英国工程师。\n\n他的**火箭号**赢得了雨山试验。");

        // The branch's first answer earns it an automatic title. Wait for it while the branch is shown: a title landing
        // after the branch is left updates the grandchild's lineage but not the page left behind (CI 2026-11-15).
        app.Client.Titles.Replies.Enqueue(ChildTitle);
        child.Composer.Text = "他是谁？";
        await child.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => child.Messages.Count == 2 && child.Messages[1].CanBranch && child.Title == ChildTitle);
        var grandchild = await BranchAsync(app, window, child, "雨山试验");
        Assert.Equal([FakeChatClient_DefaultTitle, ChildTitle], grandchild.Lineage.Select(l => l.Title));

        // Grandchild → child: the source passage is selected, highlighted and on screen.
        grandchild.ReturnToSourceCommand.Execute(null);
        var backInChild = await PageAsync(app, window, child.Id);
        Assert.NotSame(child, backInChild);
        AssertSourceShown(window, "雨山试验");
        Assert.Null(backInChild.SourceNotice);

        // Child → root, from far down a long answer.
        backInChild.ReturnToSourceCommand.Execute(null);
        await PageAsync(app, window, root.Id);
        AssertSourceShown(window, "斯蒂芬森");

        // Back follows history: root ← child ← grandchild, independent of the source relation.
        app.Workbench.GoBack();
        Assert.Equal(child.Id, Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Id);
        app.Workbench.GoBack();
        Assert.Equal(grandchild.Id, Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Re_entering_a_branch_and_switching_conversations_each_restore_their_own_place()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var root = await StartConversationAsync(app, window, "火车是怎么发明的？", LongAnswer);
        var child = await BranchAsync(app, window, root, "铁路随后");
        app.Client.Reply(LongAnswer.Replace("火车", "铁路", StringComparison.Ordinal));
        child.Composer.Text = "讲讲铁路";
        await child.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => child.Messages.Count == 2 && child.Messages[1].CanBranch && TryAnswer(window) is { IsLayoutCurrent: true });
        window.UpdateLayout();

        // Read the child somewhere in the middle, then leave through the sidebar (ordinary switch).
        var childOffset = await ScrollToAsync(window, 0.4);
        OpenFromSidebar(app, root.Id);
        var rootAgain = await PageAsync(app, window, root.Id);
        Assert.Null(rootAgain.SourceNotice);
        Assert.Empty(HighlightedBlocks(window));

        // Re-enter through the entry under the source answer: navigation, not a new branch.
        var entry = Assert.Single(rootAgain.Messages[1].Branches);
        rootAgain.OpenConversationCommand.Execute(entry.Child);
        await PageAsync(app, window, child.Id);
        Assert.InRange(Scroller(window).Offset.Y, childOffset - 4, childOffset + 4);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        Assert.Single(await conversations.ListBranchesAsync(root.Id, CancellationToken.None));
        Assert.Equal(2, (await conversations.ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None)).Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_page_left_right_after_a_scroll_reopens_where_it_was_left()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var root = await StartConversationAsync(app, window, "火车是怎么发明的？", LongAnswer);
        var child = await BranchAsync(app, window, root, "铁路随后");
        // The sidebar lists new conversations after its own reload; in a full desktop run that can come later than
        // the branch page, and opening from the sidebar before it would find no row.
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Any(i => i.Id == root.Id)
            && app.ShellPage.Conversations.Items.Any(i => i.Id == child.Id));
        OpenFromSidebar(app, root.Id);
        await PageAsync(app, window, root.Id);
        await UiWait.UntilAsync(() => !CurrentView(window).IsPositionPending);

        // Scroll and leave at once, before the scroll has been still long enough to be recorded on its own (the
        // likely cause of a CI failure on 2026-11-15, where a page reopened after a restart at its earlier place).
        var scroller = Scroller(window);
        scroller.Offset = new Vector(0, Math.Round((scroller.Extent.Height - scroller.Viewport.Height) * 0.3));
        window.UpdateLayout();
        var left = scroller.Offset.Y;
        Assert.True(CurrentView(window).IsPositionPending);
        OpenFromSidebar(app, child.Id);
        await PageAsync(app, window, child.Id);

        OpenFromSidebar(app, root.Id);
        await PageAsync(app, window, root.Id);
        Assert.InRange(Scroller(window).Offset.Y, left - 4, left + 4);
        window.Close();
    }

    [AvaloniaFact]
    public async Task After_a_restart_messages_sources_reading_positions_and_drafts_are_back()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var root = await StartConversationAsync(app, window, "火车是怎么发明的？", LongAnswer);
        var rootOffset = await ScrollToAsync(window, 0.3);
        var child = await BranchAsync(app, window, root, "铁路随后");
        child.Composer.Text = "还没发出的追问";
        window.Close();

        await app.RestartAsync();
        window = await OpenAsync(app, started: true);

        // The last page was the branch: its draft, source and ancestors are restored.
        var restored = await PageAsync(app, window, child.Id);
        Assert.Equal("还没发出的追问", restored.Composer.Text);
        Assert.Equal("铁路随后", restored.SourceQuote);
        Assert.Equal(root.Title, Assert.Single(restored.Lineage).Title);
        Assert.Equal([0, 1], app.ShellPage.Conversations.Items.Select(i => i.Depth));

        // The root reopens at its own reading position, with the branch entry under its answer.
        OpenFromSidebar(app, root.Id);
        var rootPage = await PageAsync(app, window, root.Id);
        Assert.InRange(Scroller(window).Offset.Y, rootOffset - 4, rootOffset + 4);
        Assert.Single(rootPage.Messages[1].Branches);

        // And the source can still be located exactly after the restart.
        restored = await PageAsync(app, window, child.Id, open: () => rootPage.OpenConversationCommand.Execute(child.Id));
        restored.ReturnToSourceCommand.Execute(null);
        await PageAsync(app, window, root.Id);
        AssertSourceShown(window, "铁路随后");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_source_that_no_longer_matches_falls_back_to_its_answer_with_a_notice()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var root = await StartConversationAsync(app, window, "火车是怎么发明的？", LongAnswer);
        var child = await BranchAsync(app, window, root, "铁路随后");

        // As if the renderer had changed since the branch was made.
        using (var connection = new SqliteConnection($"Data Source={app.Paths.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE branch_source SET render_profile = 'livemarkdown-9/other';";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        child.ReturnToSourceCommand.Execute(null);
        var parent = await PageAsync(app, window, root.Id);

        Assert.Equal(UiText.SourceNotLocated("铁路随后"), parent.SourceNotice);
        Assert.Empty(HighlightedBlocks(window));
        var answer = Answer(window);
        var top = answer.TranslatePoint(default, Scroller(window))!.Value.Y;
        Assert.InRange(top, -answer.Bounds.Height, Scroller(window).Viewport.Height);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_growing_answer_offers_no_branch_until_it_is_finished_and_saved()
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenAsync(app);
        var held = app.Client.Hold("正在增长的回答文字");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "问";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].Body.Length > 0);
        await CurrentView(window).Restored;
        await UiWait.UntilAsync(() => Answer(window).IsLayoutCurrent && SourceAnchors.Blocks(Answer(window).Renderer).Count > 0);

        // While it grows, even a selection made with the mouse cannot become a branch.
        Select(window, Answer(window), "增长");
        Assert.False(Answer(window).BranchButton.IsEffectivelyVisible);
        Assert.Null(Answer(window).CaptureSelection());

        held.Release.TrySetResult();
        await UiWait.UntilAsync(() => page.Messages[1].State == MessageState.Complete && page.Messages[1].CanBranch);
        window.UpdateLayout();
        Answer(window).ClearSelection();

        // The same drag again: a second press at the same point within the double-click time is a double click, which
        // selects the whole run of CJK text and anchors the drag at its start (the [0,4) of 2026-10-27).
        await UiInput.PastDoubleClickAsync();
        Select(window, Answer(window), "增长");
        Assert.True(Answer(window).BranchButton.IsEffectivelyVisible);
        window.Close();
    }


    private static async Task<MainWindow> OpenAsync(DesktopHarness app, bool started = false)
    {
        var window = app.ShowWindow();
        if (!started)
        {
            await app.StartWithWorkspaceAsync("火车历史");
            await app.ConfigureModelAsync();
        }

        window.UpdateLayout();
        return window;
    }

    private static async Task<ConversationViewModel> StartConversationAsync(DesktopHarness app, Window window, string question, string answer)
    {
        app.Client.Reply(answer);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        await UiWait.UntilAsync(() => page.Title == FakeChatClient_DefaultTitle);
        await CurrentView(window).Restored;
        window.UpdateLayout();
        return page;
    }

    private const string FakeChatClient_DefaultTitle = Mindweave.Testing.FakeChatClient.DefaultTitle;

    private const string ChildTitle = "斯蒂芬森其人";

    /// <summary>Drags across <paramref name="quote"/> in the page's last answer and clicks "create branch".</summary>
    private static async Task<ConversationViewModel> BranchAsync(DesktopHarness app, Window window, ConversationViewModel parent, string quote)
    {
        await CurrentView(window).Restored;

        // A finished answer is swapped from its live view to the stored text; wait until that view is laid out.
        await UiWait.UntilAsync(() => TryAnswer(window) is { IsLayoutCurrent: true, DataContext: MessageItemViewModel { CanBranch: true } });
        window.UpdateLayout();
        var answer = Answer(window);
        Select(window, answer, quote);
        var button = answer.BranchButton;
        Assert.True(button.IsEffectivelyVisible);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel page && page.Id != parent.Id);
        var child = (ConversationViewModel)app.Workbench.CurrentPage!;
        await child.Loaded;
        await CurrentView(window).Restored;
        return child;
    }

    private static void Select(Window window, AnswerView answer, string quote)
    {
        var block = SourceAnchors.Blocks(answer.Renderer).First(b => b.LayoutText.Contains(quote, StringComparison.Ordinal));
        block.BringIntoView();
        window.UpdateLayout();
        var start = block.LayoutText.IndexOf(quote, StringComparison.Ordinal);
        var end = start + quote.Length;
        Assert.True(end < block.LayoutText.Length, "Pick a quote that does not end its block.");
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

    private static async Task<ConversationViewModel> PageAsync(DesktopHarness app, Window window, ConversationId id, Action? open = null)
    {
        open?.Invoke();
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel page && page.Id == id);
        var current = (ConversationViewModel)app.Workbench.CurrentPage!;
        await current.Loaded;
        window.UpdateLayout();
        await UiWait.UntilAsync(() => CurrentView(window).DataContext == current && CurrentView(window).Restored.IsCompleted);
        window.UpdateLayout();
        return current;
    }

    private static void OpenFromSidebar(DesktopHarness app, ConversationId id) =>
        app.ShellPage.Conversations.OpenCommand.Execute(app.ShellPage.Conversations.Items.Single(i => i.Id == id));

    /// <summary>
    /// Scrolls the current page to a fraction of its height and waits until that position is recorded: the view records
    /// it once scrolling has been still for a moment. A newly recorded position is not enough: a recording still pending
    /// from an earlier scroll (the page following the answer to its end) could land after a "before" value was read and
    /// look new (the likely cause of a CI failure on 2026-11-15, where a restart reopened the page at its end), so this
    /// waits until nothing is pending at a stable offset.
    /// </summary>
    private static async Task<double> ScrollToAsync(Window window, double fraction)
    {
        var view = CurrentView(window);
        var scroller = Scroller(window);
        scroller.Offset = new Vector(0, Math.Round((scroller.Extent.Height - scroller.Viewport.Height) * fraction));
        window.UpdateLayout();
        var last = double.NaN;
        await UiWait.UntilAsync(() =>
        {
            var stable = scroller.Offset.Y == last;
            last = scroller.Offset.Y;
            return stable && !view.IsPositionPending;
        });
        return scroller.Offset.Y;
    }

    private static void AssertSourceShown(Window window, string quote)
    {
        var highlighted = Assert.Single(HighlightedBlocks(window));
        Assert.True(highlighted.Highlights.TryGetValue("branch-source", out var highlight));
        var range = Assert.Single(highlight!.Ranges);
        Assert.Equal(quote, highlighted.LayoutText.Substring(range.Start, range.Length));
        var top = highlighted.TranslatePoint(default, Scroller(window))!.Value.Y;
        Assert.InRange(top, -highlighted.Bounds.Height, Scroller(window).Viewport.Height);
    }

    private static List<MarkdownTextBlock> HighlightedBlocks(Window window) =>
        [.. CurrentView(window).GetVisualDescendants().OfType<MarkdownTextBlock>().Where(b => b.Highlights.TryGetValue("branch-source", out _))];

    /// <summary>The page's last answer view (the message template also hosts a hidden one for each question).</summary>
    private static AnswerView Answer(Window window) => TryAnswer(window) ?? throw new InvalidOperationException("No answer is shown.");

    private static AnswerView? TryAnswer(Window window) =>
        CurrentView(window).GetVisualDescendants().OfType<AnswerView>().LastOrDefault(a => a.DataContext is MessageItemViewModel { IsAnswer: true });
}
