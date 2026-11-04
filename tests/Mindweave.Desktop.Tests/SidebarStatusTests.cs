using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Desktop.Services;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The sidebar shows each conversation's state with one light marker (S7 round 4, the user's acceptance test 2026-11-02,
/// finding 3): a spinner while an answer or summary is being written, "!" when the last answer needs attention, and an
/// accent dot for a new answer the user has not seen. The collapsed sidebar's expand button carries the same news.
/// </summary>
public class SidebarStatusTests
{
    [AvaloniaFact]
    public async Task An_answer_that_finishes_on_another_page_marks_its_conversation_until_it_is_opened()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var (first, second) = await TwoConversationsAsync(app);
        var item = Item(app, first);
        var title = Title(window, first);
        var titleWidth = title.Bounds.Width;
        Assert.Equal(ConversationMark.None, item.Mark);

        // The marker column is there without a marker, so a title never shifts or re-trims when one appears.
        var column = Row(window, first).GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "RowMarker");
        Assert.True(column.IsEffectivelyVisible);
        Assert.Equal(16, column.Bounds.Width);

        // On the second conversation's page, an answer of the first finishes.
        await AskElsewhereAsync(app, first, "再讲讲斯蒂芬森。");
        await UiWait.UntilAsync(() => item.IsUnread);
        window.UpdateLayout();
        Assert.True(Marker<Ellipse>(window, first, "unread-dot").IsEffectivelyVisible);
        Assert.Equal(UiText.MarkerNewAnswer, item.MarkerHint);
        // The row moved to the top (newest activity), so its controls are looked up again.
        Assert.Equal(UiText.MarkerNewAnswer, ToolTip.GetTip(Row(window, first).GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "RowMarker")));
        Assert.Equal(titleWidth, Title(window, first).Bounds.Width);
        Assert.True(app.ShellPage.Conversations.HasUnread);
        Assert.False(Item(app, second).IsUnread);

        // Opening it clears the mark at once, and the stored mark with it.
        Click(window, Row(window, first));
        Assert.Equal(new ConversationRoute(first), app.Workbench.CurrentPage?.Route);
        Assert.False(item.IsUnread);
        Assert.False(app.ShellPage.Conversations.HasUnread);
        window.UpdateLayout();
        Assert.False(Marker<Ellipse>(window, first, "unread-dot").IsVisible);
        Assert.False(app.Services.GetRequiredService<IConversationMarks>().IsUnread(first));
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_answer_that_finishes_while_its_conversation_is_on_screen_leaves_no_mark()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var (first, second) = await TwoConversationsAsync(app);
        var page = await OpenAsync(app, first);
        var held = app.Client.Hold("斯蒂芬森");
        page.Composer.Text = "再讲讲斯蒂芬森。";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        held.Finish("是铁路之父。");
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].State == MessageState.Complete);
        await UiWait.UntilAsync(() => Item(app, first).Mark == ConversationMark.None);

        // Leaving afterwards does not mark it either: the answer was on screen when it finished.
        await OpenAsync(app, second);
        await app.ShellPage.Conversations.Loaded;
        Assert.False(Item(app, first).IsUnread);
        Assert.False(app.Services.GetRequiredService<IConversationMarks>().IsUnread(first));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_stopped_answer_with_text_marks_it_and_one_without_text_does_not()
    {
        await using var app = DesktopHarness.Create();
        Open(app);
        var (first, second) = await TwoConversationsAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();

        // The marks service hears of a finished answer before this later listener does.
        var finished = new System.Collections.Concurrent.ConcurrentQueue<AnswerOutcome>();
        app.Services.GetRequiredService<GenerationCoordinator>().AnswerFinished += (_, e) => finished.Enqueue(e);
        var marks = app.Services.GetRequiredService<IConversationMarks>();

        var held = app.Client.Hold();
        await conversations.AskAsync(first, "问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var empty = await RunningAnswerAsync(conversations, first);
        await conversations.StopAsync(empty);
        await UiWait.UntilAsync(() => finished.Any(f => f.MessageId == empty));
        Assert.Equal(MessageState.Stopped, finished.Single(f => f.MessageId == empty).State);
        Assert.False(marks.IsUnread(first));

        held = app.Client.Hold("写了一半");
        await conversations.AskAsync(first, "再问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var answer = await RunningAnswerAsync(conversations, first);
        await UiWait.UntilAsync(() => conversations.Observe(answer)?.Snapshot().Text.Length > 0);
        await conversations.StopAsync(answer);
        await UiWait.UntilAsync(() => Item(app, first).IsUnread);
        Assert.False(Item(app, second).IsUnread);
    }

    [AvaloniaFact]
    public async Task Unread_marks_come_back_after_a_restart_and_opening_clears_them_for_good()
    {
        await using var app = DesktopHarness.Create();
        Open(app);
        var (first, second) = await TwoConversationsAsync(app);
        await AskElsewhereAsync(app, first, "再讲讲斯蒂芬森。");
        await UiWait.UntilAsync(() => Item(app, first).IsUnread);

        await app.RestartAsync();
        var window = Open(app);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2);
        await UiWait.UntilAsync(() => Item(app, first).IsUnread);
        Assert.False(Item(app, second).IsUnread);

        await OpenAsync(app, first);
        Assert.False(Item(app, first).IsUnread);
        window.Close();
        await app.RestartAsync();
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2);
        await app.ShellPage.Conversations.Loaded;
        Assert.False(Item(app, first).IsUnread);
        Assert.False(app.Services.GetRequiredService<IConversationMarks>().IsUnread(first));
    }

    [AvaloniaFact]
    public async Task A_running_answer_shows_a_spinner_that_turns_only_while_it_is_visible()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var (first, _) = await TwoConversationsAsync(app);
        var held = app.Client.Hold("斯蒂芬森");
        await app.Services.GetRequiredService<ConversationService>().AskAsync(first, "再讲讲斯蒂芬森。", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => Item(app, first).IsGenerating);
        window.UpdateLayout();
        var spinner = Row(window, first).GetVisualDescendants().OfType<ActivitySpinner>().Single();
        Assert.True(spinner.IsEffectivelyVisible);
        Assert.True(spinner.IsTurning);
        Assert.Equal(UiText.MarkerGenerating, Item(app, first).MarkerHint);
        Assert.Equal(12, spinner.Bounds.Width);

        // Collapsing the sidebar hides it, and it stops; it turns again once the sidebar is back.
        app.ShellPage.ToggleSidebarCommand.Execute(null);
        window.UpdateLayout();
        Assert.False(spinner.IsTurning);
        app.ShellPage.ToggleSidebarCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(spinner.IsTurning);

        held.Finish("是铁路之父。");
        await UiWait.UntilAsync(() => !Item(app, first).IsGenerating);
        window.UpdateLayout();
        Assert.False(spinner.IsTurning);
        Assert.False(spinner.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_spinner_advances_with_animation_frames_while_shown()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var (first, _) = await TwoConversationsAsync(app);
        var held = app.Client.Hold("斯蒂芬森");
        await app.Services.GetRequiredService<ConversationService>().AskAsync(first, "再讲讲斯蒂芬森。", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => Item(app, first).IsGenerating);
        window.UpdateLayout();
        var spinner = Row(window, first).GetVisualDescendants().OfType<ActivitySpinner>().Single();
        ActivitySpinner.Animates = true;
        try
        {
            // Re-show it so it asks for frames now that animation is on.
            app.ShellPage.ToggleSidebarCommand.Execute(null);
            app.ShellPage.ToggleSidebarCommand.Execute(null);
            window.UpdateLayout();
            await UiWait.UntilAsync(() =>
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                return spinner.Angle != 0;
            });
        }
        finally
        {
            ActivitySpinner.Animates = false;
        }

        held.Finish("。");
        await UiWait.UntilAsync(() => !Item(app, first).IsGenerating);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_summary_being_written_shows_the_spinner_too()
    {
        await using var app = DesktopHarness.Create();
        Open(app);
        var (first, _) = await TwoConversationsAsync(app);
        app.Client.KnowledgeGate = new TaskCompletionSource();
        Assert.True((await app.Services.GetRequiredService<SummaryService>().SummarizeAsync(first, CancellationToken.None)).Accepted);
        await UiWait.UntilAsync(() => Item(app, first).Mark == ConversationMark.Summarizing);
        Assert.True(Item(app, first).IsGenerating);
        Assert.Equal(UiText.MarkerSummarizing, Item(app, first).MarkerHint);

        app.Client.KnowledgeGate.TrySetResult();
        await UiWait.UntilAsync(() => Item(app, first).Mark == ConversationMark.None);
    }

    [AvaloniaFact]
    public async Task Running_wins_over_attention_and_attention_over_unread()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var (first, _) = await TwoConversationsAsync(app);
        var item = Item(app, first);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        await AskElsewhereAsync(app, first, "再讲讲斯蒂芬森。");
        await UiWait.UntilAsync(() => item.Mark == ConversationMark.Unread);

        // A failed answer after the unread one: the conversation needs attention, and the dot gives way to "!".
        app.Client.Fail(GenerationErrorKind.Network, "connection reset", "半句");
        await conversations.AskAsync(first, "雨山试验呢？", CancellationToken.None);
        await UiWait.UntilAsync(() => item.Mark == ConversationMark.NeedsAttention);
        window.UpdateLayout();
        Assert.True(Marker<Border>(window, first, "attention-badge").IsEffectivelyVisible);
        Assert.False(Marker<Ellipse>(window, first, "unread-dot").IsVisible);

        // Retrying it: running wins over both.
        var held = app.Client.Hold("雨山");
        var failed = (await conversations.LoadAsync(first, CancellationToken.None))!.Messages.Last(m => m.Role == MessageRole.Assistant);
        await conversations.RetryAsync(first, failed.Id, CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => item.Mark == ConversationMark.Answering);
        window.UpdateLayout();
        Assert.True(Row(window, first).GetVisualDescendants().OfType<ActivitySpinner>().Single().IsEffectivelyVisible);
        Assert.False(Marker<Border>(window, first, "attention-badge").IsVisible);
        Assert.False(Marker<Ellipse>(window, first, "unread-dot").IsVisible);

        held.Finish("试验在 1829 年。");
        await UiWait.UntilAsync(() => item.Mark == ConversationMark.Unread);
        window.Close();
    }

    [AvaloniaFact]
    public async Task While_collapsed_the_expand_button_shows_an_accent_dot_for_a_new_answer_unless_something_needs_attention()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var (first, second) = await TwoConversationsAsync(app);
        Click(window, Part<Button>(window, "CollapseSidebar"));
        var unread = Part<Ellipse>(window, "ExpandSidebarUnread");
        var attention = Part<Ellipse>(window, "ExpandSidebarAttention");
        var expand = Part<Button>(window, "ExpandSidebar");
        Assert.False(unread.IsEffectivelyVisible);

        await AskElsewhereAsync(app, first, "再讲讲斯蒂芬森。");
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.ShowsUnread);
        window.UpdateLayout();
        Assert.True(unread.IsEffectivelyVisible);
        Assert.False(attention.IsEffectivelyVisible);
        Assert.Equal(UiText.ExpandSidebarUnread, ToolTip.GetTip(expand));

        // Something needing attention takes the one dot (error colour), with its own tooltip.
        app.Client.Fail(GenerationErrorKind.Network, "connection reset", "半句");
        await app.Services.GetRequiredService<ConversationService>().AskAsync(second, "雨山试验呢？", CancellationToken.None);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.HasAttention);
        window.UpdateLayout();
        Assert.True(attention.IsEffectivelyVisible);
        Assert.False(unread.IsEffectivelyVisible);
        Assert.Equal(UiText.ExpandSidebarAttention, ToolTip.GetTip(expand));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_spinner_asks_for_no_frames_while_hidden_minimised_or_scrolled_out_of_view()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var (first, _) = await TwoConversationsAsync(app);
        var held = app.Client.Hold("斯蒂芬森");
        await app.Services.GetRequiredService<ConversationService>().AskAsync(first, "再讲讲斯蒂芬森。", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => Item(app, first).IsGenerating);
        window.UpdateLayout();
        var spinner = Row(window, first).GetVisualDescendants().OfType<ActivitySpinner>().Single();
        ActivitySpinner.Animates = true;
        try
        {
            app.ShellPage.ToggleSidebarCommand.Execute(null);
            app.ShellPage.ToggleSidebarCommand.Execute(null);
            window.UpdateLayout();
            await AssertFramesAsync(spinner, arrive: true);

            // The sidebar collapsed.
            app.ShellPage.ToggleSidebarCommand.Execute(null);
            window.UpdateLayout();
            await AssertFramesAsync(spinner, arrive: false);
            app.ShellPage.ToggleSidebarCommand.Execute(null);
            window.UpdateLayout();
            await AssertFramesAsync(spinner, arrive: true);

            // The window minimised.
            window.WindowState = WindowState.Minimized;
            await AssertFramesAsync(spinner, arrive: false);
            window.WindowState = WindowState.Normal;
            await AssertFramesAsync(spinner, arrive: true);

            // Scrolled out of the sidebar's viewport: the list's viewer is shrunk below the running row and scrolled past it.
            var list = window.GetVisualDescendants().OfType<ShellView>().Single().GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ConversationList");
            var viewer = list.FindAncestorOfType<ScrollViewer>()!;
            var row = Row(window, first);
            viewer.MaxHeight = row.Bounds.Height;
            window.UpdateLayout();
            viewer.Offset = new Vector(0, viewer.Extent.Height);
            window.UpdateLayout();
            Assert.False(spinner.IsTurning);
            await AssertFramesAsync(spinner, arrive: false);
            viewer.Offset = default;
            window.UpdateLayout();
            Assert.True(spinner.IsTurning);
            await AssertFramesAsync(spinner, arrive: true);
        }
        finally
        {
            ActivitySpinner.Animates = false;
        }

        held.Finish("。");
        await UiWait.UntilAsync(() => !Item(app, first).IsGenerating);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_mark_set_while_storage_is_paused_is_refused_then_written_once_storage_recovers()
    {
        await using var app = DesktopHarness.Create();
        Open(app);
        var (first, _) = await TwoConversationsAsync(app);
        var preferences = app.Services.GetRequiredService<IPreferenceStore>();
        var health = app.Services.GetRequiredService<StorageHealth>();

        // An answer is running when storage pauses (a kept write holds the pause until it is resolved).
        var held = app.Client.Hold("斯蒂芬森");
        await app.Services.GetRequiredService<ConversationService>().AskAsync(first, "再讲讲斯蒂芬森。", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var kept = health.Retain(new StorageWriteException(StorageFailureKind.Full, "simulated full disk", null));
        Assert.True(health.IsBlocked);
        held.Finish("是铁路之父。");
        await UiWait.UntilAsync(() => Item(app, first).IsUnread);
        await app.Services.GetRequiredService<ConversationMarksService>().FlushAsync();
        Assert.DoesNotContain(first.ToString(), await preferences.GetAsync(ConversationMarksService.UnreadKey, CancellationToken.None) ?? string.Empty);

        // Storage recovers: the mark is written without waiting for the next change or the exit.
        kept.Resolve();
        await UiWait.UntilAsync(() => !health.IsBlocked);
        await UiWait.UntilAsync(() => preferences.GetAsync(ConversationMarksService.UnreadKey, CancellationToken.None).GetAwaiter().GetResult()?.Contains(first.ToString(), StringComparison.Ordinal) == true);
    }

    [AvaloniaFact]
    public async Task A_mark_whose_save_failed_is_written_by_the_exit_and_comes_back()
    {
        var flaky = new FlakyPreferences();
        await using var app = DesktopHarness.Create(flaky.Register);
        Open(app);
        var (first, second) = await TwoConversationsAsync(app);
        flaky.FailWrites = true;
        await AskElsewhereAsync(app, first, "再讲讲斯蒂芬森。");
        await UiWait.UntilAsync(() => Item(app, first).IsUnread);
        await app.Services.GetRequiredService<ConversationMarksService>().FlushAsync();
        Assert.True(flaky.FailedWrites > 0);
        flaky.FailWrites = false;

        // Quit through the real host, which tries the pending marks once more before the database closes.
        Assert.True(app.Root.Exit.InterceptClose());
        await app.Root.Exit.Running;
        var instance = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(instance);
        await app.HandOverToHost(instance).DisposeAsync();

        await using var again = DesktopHarness.Create(directory: app.Paths.DataDirectory);
        await again.StartAsync();
        await UiWait.UntilAsync(() => again.ShellPage.Conversations.Items.Count == 2);
        await again.ShellPage.Conversations.Loaded;
        Assert.True(again.Services.GetRequiredService<IConversationMarks>().IsUnread(first));
        Assert.False(again.Services.GetRequiredService<IConversationMarks>().IsUnread(second));
    }

    [AvaloniaFact]
    public async Task A_failed_read_at_start_does_not_let_the_next_mark_wipe_the_stored_ones()
    {
        var flaky = new FlakyPreferences();
        await using var app = DesktopHarness.Create(flaky.Register);
        Open(app);
        var (first, second) = await TwoConversationsAsync(app);
        await AskElsewhereAsync(app, first, "再讲讲斯蒂芬森。");
        await UiWait.UntilAsync(() => Item(app, first).IsUnread);

        // The stored marks cannot be read at the next start; a new answer elsewhere is marked meanwhile.
        flaky.FailReads = true;
        await app.RestartAsync();
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        await AskElsewhereAsync(app, second, "高铁呢？");
        await UiWait.UntilAsync(() => Item(app, second).IsUnread);
        await app.Services.GetRequiredService<ConversationMarksService>().FlushAsync();

        // Once reads work again, both marks are stored: the earlier one was merged, not overwritten.
        flaky.FailReads = false;
        await app.Services.GetRequiredService<ConversationMarksService>().FlushAsync();
        await app.RestartAsync();
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2);
        await app.ShellPage.Conversations.Loaded;
        var marks = app.Services.GetRequiredService<IConversationMarks>();
        Assert.True(marks.IsUnread(second));
        Assert.True(marks.IsUnread(first));
    }

    [AvaloniaFact]
    public async Task Deleting_a_marked_conversation_removes_it_from_the_stored_marks()
    {
        await using var app = DesktopHarness.Create();
        Open(app);
        var (first, _) = await TwoConversationsAsync(app);
        var preferences = app.Services.GetRequiredService<IPreferenceStore>();
        await AskElsewhereAsync(app, first, "再讲讲斯蒂芬森。");
        await UiWait.UntilAsync(() => Item(app, first).IsUnread);
        await app.Services.GetRequiredService<ConversationMarksService>().FlushAsync();
        Assert.Contains(first.ToString(), await preferences.GetAsync(ConversationMarksService.UnreadKey, CancellationToken.None) ?? string.Empty);

        var deletion = app.Services.GetRequiredService<ConversationDeletionService>();
        Assert.True((await deletion.DeleteAsync(await deletion.InspectAsync([first], CancellationToken.None), CancellationToken.None)).IsDeleted);
        Assert.False(app.Services.GetRequiredService<IConversationMarks>().IsUnread(first));
        await app.Services.GetRequiredService<ConversationMarksService>().FlushAsync();
        Assert.DoesNotContain(first.ToString(), await preferences.GetAsync(ConversationMarksService.UnreadKey, CancellationToken.None) ?? string.Empty);
    }

    [AvaloniaFact]
    public async Task The_state_markers_reach_automation_names_not_only_tooltips()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var (first, _) = await TwoConversationsAsync(app);
        var expand = Part<Button>(window, "ExpandSidebar");
        Assert.Equal(UiText.ExpandSidebar, AutomationProperties.GetName(expand));

        await AskElsewhereAsync(app, first, "再讲讲斯蒂芬森。");
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.ShowsUnread);
        window.UpdateLayout();
        Assert.Equal(UiText.ExpandSidebar + " · " + UiText.MarkerNewAnswer, AutomationProperties.GetName(expand));
        Assert.Equal(UiText.MarkerNewAnswer, AutomationProperties.GetHelpText(Row(window, first)));
        window.Close();
    }

    /// <summary>Frames keep arriving while the spinner is in sight, and none arrive (after the one already asked for) while it is not.</summary>
    private static async Task AssertFramesAsync(ActivitySpinner spinner, bool arrive)
    {
        for (var settle = 0; settle < 3; settle++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var before = spinner.FramesReceived;
        for (var tick = 0; tick < 5; tick++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        if (arrive)
        {
            Assert.True(spinner.FramesReceived > before, "The spinner received no frames while in sight.");
        }
        else
        {
            Assert.Equal(before, spinner.FramesReceived);
        }
    }

    /// <summary>The real preference store, made to fail reads or writes of the unread marks on demand.</summary>
    private sealed class FlakyPreferences
    {
        public volatile bool FailReads;
        public volatile bool FailWrites;
        private int _failedWrites;

        public int FailedWrites => Volatile.Read(ref _failedWrites);

        public void Register(IServiceCollection services) =>
            services.AddSingleton<IPreferenceStore>(sp => new Store(this, new SqlitePreferenceStore(sp.GetRequiredService<SqliteDatabase>())));

        private sealed class Store(FlakyPreferences owner, IPreferenceStore inner) : IPreferenceStore
        {
            public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
                owner.FailReads && key == ConversationMarksService.UnreadKey
                    ? Task.FromException<string?>(new IOException("simulated read failure"))
                    : inner.GetAsync(key, cancellationToken);

            public Task SetAsync(string key, string value, CancellationToken cancellationToken)
            {
                if (owner.FailWrites && key == ConversationMarksService.UnreadKey)
                {
                    Interlocked.Increment(ref owner._failedWrites);
                    return Task.FromException(new IOException("simulated write failure"));
                }

                return inner.SetAsync(key, value, cancellationToken);
            }
        }
    }

    /// <summary>Two finished conversations in a fresh workspace; the second one's page is shown.</summary>
    private static async Task<(ConversationId First, ConversationId Second)> TwoConversationsAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var first = await NewConversationAsync(app, "火车是怎么发明的？");
        var second = await NewConversationAsync(app, "高铁为什么快？");
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2);
        await app.ShellPage.Conversations.Loaded;
        Assert.False(Item(app, first).IsUnread);
        return (first, second);
    }

    private static async Task<ConversationId> NewConversationAsync(DesktopHarness app, string question)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].State == MessageState.Complete);
        return page.Id;
    }

    /// <summary>Asks in a conversation that is not on screen, and waits for its answer.</summary>
    private static async Task AskElsewhereAsync(DesktopHarness app, ConversationId conversation, string question)
    {
        Assert.NotEqual(new ConversationRoute(conversation), app.Workbench.CurrentPage?.Route);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        Assert.Null((await conversations.AskAsync(conversation, question, CancellationToken.None)).Rejection);
        await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(conversation));
    }

    private static async Task<MessageId> RunningAnswerAsync(ConversationService conversations, ConversationId conversation)
    {
        var answer = (await conversations.LoadAsync(conversation, CancellationToken.None))!.Messages.Last(m => m.Role == MessageRole.Assistant);
        await UiWait.UntilAsync(() => conversations.Observe(answer.Id) is not null);
        return answer.Id;
    }

    private static async Task<ConversationViewModel> OpenAsync(DesktopHarness app, ConversationId id)
    {
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(id)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        return page;
    }

    private static ConversationItemViewModel Item(DesktopHarness app, ConversationId id) =>
        app.ShellPage.Conversations.Items.Single(i => i.Id == id);

    private static Button Row(Window window, ConversationId id)
    {
        window.UpdateLayout();
        var list = window.GetVisualDescendants().OfType<ShellView>().Single().GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ConversationList");
        return list.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is ConversationItemViewModel item && item.Id == id);
    }

    private static TextBlock Title(Window window, ConversationId id) =>
        Row(window, id).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RowTitle");

    private static T Marker<T>(Window window, ConversationId id, string className)
        where T : Control =>
        Row(window, id).GetVisualDescendants().OfType<T>().Single(c => c.Classes.Contains(className));

    private static T Part<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<ShellView>().Single().GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        window.UpdateLayout();
    }

    private static MainWindow Open(DesktopHarness app)
    {
        var window = new MainWindow { DataContext = app.Root, Width = 1100, Height = 720 };
        window.Show();
        return window;
    }
}
