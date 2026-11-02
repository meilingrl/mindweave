using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using TextBlock = Avalonia.Controls.TextBlock;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// S7 round 4 (your Windows test, finding 2): a quiet live counter while an answer is pending or streaming, and the
/// total time it took beside its time once it finished. Driven by the app's clock, which the test moves by hand.
/// </summary>
public class AnswerTimerTests
{
    [Fact]
    public void Elapsed_times_read_as_seconds_minutes_and_hours()
    {
        Assert.Equal("0 秒", UiText.Elapsed(TimeSpan.FromMilliseconds(400)));
        Assert.Equal("12 秒", UiText.Elapsed(TimeSpan.FromSeconds(12.9)));
        Assert.Equal("1 分 05 秒", UiText.Elapsed(TimeSpan.FromSeconds(65)));
        Assert.Equal("59 分 59 秒", UiText.Elapsed(TimeSpan.FromSeconds(3599)));
        Assert.Equal("1 小时 02 分", UiText.Elapsed(TimeSpan.FromMinutes(62)));
        Assert.Equal("0 秒", UiText.Elapsed(TimeSpan.FromSeconds(-3)));
        Assert.Equal("用时 14 秒", UiText.AnswerDuration(TimeSpan.FromSeconds(14.2)));
        Assert.Equal("用时不到 1 秒", UiText.AnswerDuration(TimeSpan.FromMilliseconds(300)));
    }

    [AvaloniaFact]
    public async Task A_pending_answer_counts_up_from_when_the_question_was_sent()
    {
        var clock = new ManualTime();
        await using var app = DesktopHarness.Create(s => Use(s, clock));
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold();
        var page = await AskAsync(app, held);
        var view = CurrentView(window);

        // Waiting for the first text: the counter sits next to the waiting mark.
        await UiWait.UntilAsync(() => Visible(view, "WaitingElapsed")?.Text == "0 秒");
        Assert.True(page.IsTicking);
        clock.Advance(TimeSpan.FromSeconds(12));
        await UiWait.UntilAsync(() => Visible(view, "WaitingElapsed")?.Text == "12 秒");
        Assert.Contains("timestamp", Visible(view, "WaitingElapsed")!.Classes);

        held.Finish("答。");
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1] is { State: MessageState.Complete, IsLive: false });
        Assert.Equal("用时 12 秒", page.Messages[1].DurationText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_streaming_answer_counts_next_to_its_typing_mark_and_shows_its_total_beside_its_time_when_done()
    {
        var clock = new ManualTime();
        await using var app = DesktopHarness.Create(s => Use(s, clock));
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("第一句。");
        var page = await AskAsync(app, held);
        var view = CurrentView(window);

        await UiWait.UntilAsync(() => page.Messages[1].IsStreaming);
        await UiWait.UntilAsync(() => Visible(view, "StreamingElapsed")?.Text == "0 秒");
        Assert.Same(Visible(view, "StreamingElapsed")!.GetVisualParent(), view.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "StreamingIndicator" && p.IsEffectivelyVisible));
        clock.Advance(TimeSpan.FromSeconds(65));
        await UiWait.UntilAsync(() => Visible(view, "StreamingElapsed")?.Text == "1 分 05 秒");
        Assert.Null(Visible(view, "AnswerDuration"));

        // Finished: no counter, no timer; the total stays in the quiet row beside the time.
        held.Release.TrySetResult();
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1] is { State: MessageState.Complete, IsLive: false });
        window.UpdateLayout();
        Assert.False(page.IsTicking);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Null(Visible(view, "StreamingElapsed"));
        var stored = (await app.Services.GetRequiredService<IConversations>().LoadAsync(page.Id, CancellationToken.None))!.Messages[1];
        Assert.Equal(UiText.AnswerDuration(stored.CompletedAt!.Value - stored.CreatedAt), page.Messages[1].DurationText);
        Assert.Equal("用时 1 分 05 秒", page.Messages[1].DurationText);
        await UiWait.UntilAsync(() => Visible(view, "AnswerDuration") is not null);
        var duration = Visible(view, "AnswerDuration")!;
        Assert.Equal("· 用时 1 分 05 秒", duration.Text);
        Assert.Contains("timestamp", duration.Classes);
        Assert.Same(Visible(view, "AnswerTime")!.GetVisualParent(), duration.GetVisualParent());
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_stopped_answer_keeps_its_duration_and_a_failed_one_has_none()
    {
        var clock = new ManualTime();
        await using var app = DesktopHarness.Create(s => Use(s, clock));
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("半句");
        var page = await AskAsync(app, held);
        await UiWait.UntilAsync(() => page.Messages[1].IsStreaming);
        clock.Advance(TimeSpan.FromSeconds(9));
        page.StopCommand.Execute(null);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1] is { State: MessageState.Stopped, IsLive: false });
        Assert.Equal("用时 9 秒", page.Messages[1].DurationText);
        Assert.False(page.IsTicking);

        app.Client.Fail(GenerationErrorKind.Network, "connection reset");
        await page.RetryCommand.ExecuteAsync(page.Messages[1]);
        await UiWait.UntilAsync(() => page.Messages.Count == 3 && page.Messages[2] is { State: MessageState.Failed, IsLive: false });
        window.UpdateLayout();
        Assert.Null(page.Messages[2].DurationText);
        Assert.Null(page.Messages[2].ElapsedText);
        var durations = CurrentView(window).GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "AnswerDuration" && t.IsEffectivelyVisible).ToList();
        Assert.Same(page.Messages[1], Assert.Single(durations).DataContext);
        window.Close();
    }

    [Fact]
    public void Old_answers_without_an_end_time_and_interrupted_answers_show_no_duration()
    {
        var start = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);
        Message Answer(MessageState state, DateTimeOffset? end) =>
            new(MessageId.New(), ConversationId.New(), 1, MessageRole.Assistant, ContentBlocks.FromText("答"), state, 0, null, null, null, start, end);

        Assert.Null(new MessageItemViewModel(Answer(MessageState.Complete, null), null, new InlineDispatcher()).DurationText);
        Assert.Null(new MessageItemViewModel(Answer(MessageState.Interrupted, start.AddSeconds(5)), null, new InlineDispatcher()).DurationText);
        Assert.Null(new MessageItemViewModel(Answer(MessageState.Failed, start.AddSeconds(5)), null, new InlineDispatcher()).DurationText);
        Assert.Equal("用时 5 秒", new MessageItemViewModel(Answer(MessageState.Complete, start.AddSeconds(5)), null, new InlineDispatcher()).DurationText);
    }

    [AvaloniaFact]
    public async Task The_counter_ticks_only_while_the_page_is_shown_and_something_on_it_is_generating()
    {
        var clock = new ManualTime();
        await using var app = DesktopHarness.Create(s => Use(s, clock));
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();

        // An idle page: no timer at all.
        app.Client.Reply("答。");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var first = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        first.Composer.Text = "先问一句";
        await first.Composer.SendCommand.ExecuteAsync(null);
        var idle = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => idle.Messages.Count == 2 && idle.Messages[1] is { State: MessageState.Complete, IsLive: false });
        Assert.True(idle.IsShown);
        Assert.False(idle.IsTicking);
        Assert.Equal(0, clock.ActiveTimers);

        // A running answer on screen ticks; leaving the page stops the timer, coming back restarts it.
        var held = app.Client.Hold();
        var running = await AskAsync(app, held);
        await UiWait.UntilAsync(() => running.IsTicking);
        Assert.Equal(1, clock.ActiveTimers);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2);
        app.ShellPage.Conversations.OpenCommand.Execute(app.ShellPage.Conversations.Items.Single(i => i.Id == idle.Id));
        await UiWait.UntilAsync(() => !running.IsShown);
        Assert.False(running.IsTicking);
        Assert.Equal(0, clock.ActiveTimers);

        app.ShellPage.Conversations.OpenCommand.Execute(app.ShellPage.Conversations.Items.Single(i => i.Id == running.Id));
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { IsTicking: true });
        Assert.Equal(1, clock.ActiveTimers);

        held.Release.TrySetResult();
        await UiWait.UntilAsync(() => clock.ActiveTimers == 0);
        window.Close();
    }

    private static void Use(IServiceCollection services, ManualTime clock)
    {
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IClock>(clock);
    }

    private static MainWindow Open(DesktopHarness app)
    {
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
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
        await UiWait.UntilAsync(() => page.Messages.Count == 2);
        return page;
    }

    private static ConversationView CurrentView(Window window) => window.GetVisualDescendants().OfType<ConversationView>().Single();

    private static TextBlock? Visible(ConversationView view, string name)
    {
        view.UpdateLayout();
        return view.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(t => t.Name == name && t.IsEffectivelyVisible);
    }

    private sealed class InlineDispatcher : Services.IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    /// <summary>A clock that stands still until the test moves it; its timers fire only then, on the test's thread.</summary>
    internal sealed class ManualTime : TimeProvider, IClock
    {
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(8));

        public int ActiveTimers
        {
            get
            {
                lock (_timers)
                {
                    return _timers.Count;
                }
            }
        }

        public DateTimeOffset UtcNow => GetUtcNow();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_timers)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan by)
        {
            Timer[] due;
            lock (_timers)
            {
                _now += by;
                due = [.. _timers.Where(t => t.Due <= _now)];
                foreach (var timer in due)
                {
                    timer.Due = _now + timer.Period;
                }
            }

            foreach (var timer in due)
            {
                timer.Callback(timer.State);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state, period);
            lock (_timers)
            {
                timer.Due = _now + dueTime;
                _timers.Add(timer);
            }

            return timer;
        }

        internal sealed class Timer(ManualTime owner, TimerCallback callback, object? state, TimeSpan period) : ITimer
        {
            public TimerCallback Callback { get; } = callback;

            public object? State { get; } = state;

            public TimeSpan Period { get; private set; } = period;

            public DateTimeOffset Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._timers)
                {
                    Due = owner._now + dueTime;
                    Period = period;
                }

                return true;
            }

            public void Dispose()
            {
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
