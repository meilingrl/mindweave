using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Preferences;
using ConversationId = Mindweave.Core.Conversations.ConversationId;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The pop-up hint, the picker's count badge and the optional sound for a new answer in a workspace that is not open
/// (the user's feedback 2026-11-10: the dot alone was too easy to miss).
/// </summary>
public class WorkspaceNewsHintTests
{
    private static readonly TimeSpan Duration = WorkspaceNewsOptions.DefaultToastDuration;

    [AvaloniaFact]
    public async Task An_answer_in_another_workspace_pops_up_a_hint_without_taking_the_focus_and_with_in_workspace_hints_off_one_in_the_open_workspace_does_not()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window, news) = (scene.App, scene.Window, scene.News);
        Find<Button>(window, "NewConversationButton").Focus();
        var focused = window.FocusManager!.GetFocusedElement();

        var answered = await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        var toast = Assert.Single(news.Toasts);
        Assert.Equal(scene.Trains.Id, toast.Workspace);
        Assert.Equal("火车 有新回答", toast.Text);
        Assert.Equal([answered], toast.Conversations);

        // On screen at the content area's bottom right; read out politely, and the focus stays where it was.
        window.UpdateLayout();
        var body = Find<Button>(window, "OpenNewsToast");
        Assert.True(body.IsEffectivelyVisible);
        Assert.Equal("火车 有新回答。" + UiText.WorkspaceNewsToastAction, AutomationProperties.GetName(body));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(body));
        Assert.Equal(UiText.CloseWorkspaceNewsToast, AutomationProperties.GetName(Find<Button>(window, "CloseNewsToast")));
        Assert.Same(focused, window.FocusManager!.GetFocusedElement());
        var content = Find<DockPanel>(window, "ContentArea");
        var card = body.FindAncestorOfType<Border>()!;
        Assert.Contains("news-toast", card.Classes);
        var corner = card.TranslatePoint(new Point(card.Bounds.Width, card.Bounds.Height), content)!.Value;
        Assert.InRange(content.Bounds.Width - corner.X, 0, 40);
        Assert.InRange(content.Bounds.Height - corner.Y, 0, 120);

        // With the open workspace's hints turned off, an answer there: its sidebar marks it, and no hint pops up for it.
        await app.Services.GetRequiredService<INewsAlerts>().SetInWorkspaceAsync(false, CancellationToken.None);
        var open = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？", expectHint: false);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.SingleOrDefault(i => i.Id == open)?.Mark == ConversationMark.Unread);
        await scene.SettledAsync();
        Assert.Equal([scene.Trains.Id], news.Toasts.Select(t => t.Workspace));
        Assert.Equal(0, app.Sound.Played);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_answer_that_ended_while_its_workspace_was_open_pops_up_no_workspace_hint_after_the_user_leaves_it()
    {
        await using var scene = await Scene.StartAsync();
        var (app, news) = (scene.App, scene.News);
        app.ShellPage.SelectedWorkspace = scene.Trains;
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Id == scene.Trains.Id);

        // It ends in 火车 while 火车 is open, and the user moves to 高铁 before the hints are counted again.
        var answered = await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？", expectHint: false);
        await UiWait.UntilAsync(() => news.MarksNoted == 1);
        app.ShellPage.SelectedWorkspace = scene.HighSpeed;
        await UiWait.UntilAsync(() => news.Count == 1);
        await scene.SettledAsync();

        // The badge counts it, as for any mark elsewhere; but the user saw that workspace when it ended, so its hint is
        // the conversation's (it ended in the open workspace), never the workspace's.
        Assert.Contains(scene.Trains.Id, news.Workspaces);
        Assert.Equal([answered], news.Toasts.Select(t => t.Conversation));
        Assert.True(app.Services.GetRequiredService<IConversationMarks>().IsUnread(answered));
        scene.Window.Close();
    }

    [AvaloniaFact]
    public async Task A_further_answer_in_the_same_workspace_updates_its_hint_and_restarts_its_time_and_other_workspaces_stack_up_to_three()
    {
        await using var scene = await Scene.StartAsync();
        var news = scene.News;
        await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        var toast = Assert.Single(news.Toasts);

        // Most of its time later, a second conversation there gets an answer: the same hint, counting two, with its full time again.
        scene.Clock.Advance(Duration - TimeSpan.FromSeconds(2));
        await scene.AnswerAsync(scene.Trains, "蒸汽机车有多重？");
        Assert.Same(toast, Assert.Single(news.Toasts));
        Assert.Equal(2, toast.Count);
        Assert.Equal("火车：2 个会话有新回答", toast.Text);
        scene.Clock.Advance(Duration - TimeSpan.FromSeconds(1));
        await scene.SettledAsync();
        Assert.Single(news.Toasts);
        scene.Clock.Advance(TimeSpan.FromSeconds(2));
        await UiWait.UntilAsync(() => news.Toasts.Count == 0);

        // Different workspaces stack, newest last; a fourth pushes out the oldest.
        var others = new List<Workspace>();
        foreach (var name in new[] { "地铁", "飞机", "轮船" })
        {
            others.Add(await scene.AddWorkspaceAsync(name));
        }

        await scene.AnswerAsync(scene.Trains, "第一条铁路在哪里？");
        foreach (var other in others)
        {
            await scene.AnswerAsync(other, other.Name.Value + "是什么时候出现的？");
        }

        Assert.Equal(others.Select(w => w.Id), news.Toasts.Select(t => t.Workspace));
        scene.Window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_the_hint_switches_to_its_workspace_and_opens_the_one_new_conversation()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window, news) = (scene.App, scene.Window, scene.News);
        var answered = await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        window.UpdateLayout();

        UiInput.Click(window, Find<Button>(window, "OpenNewsToast"));
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Id == scene.Trains.Id);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(answered, page.Id);
        Assert.Empty(news.Toasts);
        Assert.Same(scene.Trains, app.ShellPage.SelectedWorkspace);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_the_hint_for_several_new_conversations_only_switches_and_the_sidebar_shows_them()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window, news) = (scene.App, scene.Window, scene.News);
        var first = await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        var second = await scene.AnswerAsync(scene.Trains, "蒸汽机车有多重？");
        Assert.Equal(2, Assert.Single(news.Toasts).Count);
        window.UpdateLayout();

        UiInput.Click(window, Find<Button>(window, "OpenNewsToast"));
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Id == scene.Trains.Id);
        Assert.IsNotType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Empty(news.Toasts);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count(i => (i.Id == first || i.Id == second) && i.Mark == ConversationMark.Unread) == 2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_close_button_takes_the_hint_away_and_the_badge_stays()
    {
        await using var scene = await Scene.StartAsync();
        var (window, news) = (scene.Window, scene.News);
        await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        window.UpdateLayout();

        UiInput.Click(window, Find<Button>(window, "CloseNewsToast"));
        Assert.Empty(news.Toasts);
        Assert.Equal(scene.HighSpeed.Id, scene.App.Workbench.CurrentWorkspace?.Id);
        window.UpdateLayout();
        Assert.True(Find<Border>(window, "OtherWorkspacesNews").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_hint_closes_itself_after_its_time_but_stays_while_the_pointer_is_on_it()
    {
        await using var scene = await Scene.StartAsync();
        var (window, news) = (scene.Window, scene.News);
        await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        window.UpdateLayout();
        var card = Find<Button>(window, "OpenNewsToast");
        var over = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), window)!.Value;

        // The pointer rests on it: it stays well past its time.
        window.MouseMove(over);
        scene.Clock.Advance(Duration * 3);
        await scene.SettledAsync();
        Assert.Single(news.Toasts);

        // Once the pointer leaves, it has its full time again, then goes.
        window.MouseMove(new Point(5, 5));
        scene.Clock.Advance(Duration - TimeSpan.FromSeconds(1));
        await scene.SettledAsync();
        Assert.Single(news.Toasts);
        scene.Clock.Advance(TimeSpan.FromSeconds(2));
        await UiWait.UntilAsync(() => news.Toasts.Count == 0);

        // The badge and the dots remain: only the pop-up is temporary.
        Assert.True(news.HasNews);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Opening_the_workspace_another_way_takes_its_hint_away()
    {
        await using var scene = await Scene.StartAsync();
        var (app, news) = (scene.App, scene.News);
        await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");

        // Gone as the workspace opens, not only once the counts are read again.
        app.ShellPage.SelectedWorkspace = scene.Trains;
        Assert.Equal(scene.Trains.Id, app.Workbench.CurrentWorkspace?.Id);
        Assert.Empty(news.Toasts);
        scene.Window.Close();
    }

    [AvaloniaFact]
    public async Task The_badge_counts_new_conversations_in_other_workspaces_and_pulses_once_when_the_count_grows()
    {
        await using var scene = await Scene.StartAsync();
        var (window, news) = (scene.Window, scene.News);
        var shell = window.GetVisualDescendants().OfType<ShellView>().Single();
        var count = Find<TextBlock>(window, "OtherWorkspacesNewsCount");
        Assert.False(shell.IsPulsing);

        // Pulses are counted, not caught in the act: one lasts 600 ms, and a slow CI runner can take longer than that
        // to finish the answer and look (it failed there once, 2026-11-13).
        await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        Assert.Equal(1, news.Count);
        Assert.Equal("1", count.Text);
        Assert.Equal(1, shell.PulsesStarted);
        await UiWait.UntilAsync(() => !shell.IsPulsing, timeoutMs: 2000);

        var other = await scene.AddWorkspaceAsync("地铁");
        await scene.AnswerAsync(other, "地铁是什么时候出现的？");
        Assert.Equal("2", count.Text);
        Assert.Equal(2, shell.PulsesStarted);
        await UiWait.UntilAsync(() => !shell.IsPulsing, timeoutMs: 2000);

        // Fewer (a workspace opened, its conversations counted there instead): no pulse.
        scene.App.ShellPage.SelectedWorkspace = other;
        await UiWait.UntilAsync(() => news.Count == 1);
        Assert.Equal(2, shell.PulsesStarted);
        Assert.False(shell.IsPulsing);
        Assert.Equal("9+", UiText.NewsBadge(10));
        Assert.Equal("9", UiText.NewsBadge(9));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_badge_does_not_pulse_when_it_grows_only_because_the_user_left_a_workspace_with_new_answers()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window, news) = (scene.App, scene.Window, scene.News);
        var shell = window.GetVisualDescendants().OfType<ShellView>().Single();

        // A new answer in the open workspace: its sidebar marks it, the badge does not count it.
        var open = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？", expectHint: false);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.SingleOrDefault(i => i.Id == open)?.Mark == ConversationMark.Unread);
        await scene.SettledAsync();
        Assert.Equal(0, news.Count);

        // Leaving it makes it another workspace: the badge shows 1, but nothing new arrived, so it does not pulse, and no
        // workspace hint pops up (the conversation's own hint, from when it ended, may still be up).
        app.ShellPage.SelectedWorkspace = scene.Trains;
        await UiWait.UntilAsync(() => news.Count == 1);
        Assert.False(shell.IsPulsing);
        Assert.DoesNotContain(news.Toasts, t => t.Conversation is null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_sound_is_off_by_default_and_once_chosen_plays_once_per_hint_and_never_at_start()
    {
        await using var scene = await Scene.StartAsync();
        var app = scene.App;
        await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        await scene.News.Alerting;
        Assert.Equal(0, app.Sound.Played);

        // Chosen on the settings page: the next hint sounds once, and so does the hint coming up again for a further answer.
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        Assert.False(settings.PlaysNewsSound);
        scene.Window.UpdateLayout();
        var toggle = Find<CheckBox>(scene.Window, "NewsSoundToggle");
        UiInput.Click(scene.Window, toggle);
        Assert.True(settings.PlaysNewsSound);
        await settings.Saving;

        var other = await scene.AddWorkspaceAsync("地铁");
        await scene.AnswerAsync(other, "地铁是什么时候出现的？");
        await scene.News.Alerting;
        Assert.Equal(1, app.Sound.Played);
        await scene.AnswerAsync(scene.Trains, "蒸汽机车有多重？");
        await scene.News.Alerting;
        Assert.Equal(2, app.Sound.Played);

        // After a restart the marks are back on the badge, but nothing pops up and nothing sounds.
        scene.Window.Close();
        await app.RestartAsync();
        var news = app.ShellPage.WorkspaceNews!;
        await UiWait.UntilAsync(() => news.Count == 3);
        await Task.Delay(300);
        await news.Loaded;
        Assert.Empty(news.Toasts);
        Assert.Equal(2, app.Sound.Played);
    }

    [AvaloniaFact]
    public async Task The_sound_choice_is_kept_across_a_restart()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        settings.PlaysNewsSound = true;
        await settings.Saving;

        await app.RestartAsync();
        Assert.True(await app.Services.GetRequiredService<INewsAlerts>().GetSoundAsync(CancellationToken.None));
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var again = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await again.Loaded;
        Assert.True(again.PlaysNewsSound);

        // And off again, the same way.
        again.PlaysNewsSound = false;
        await again.Saving;
        await app.RestartAsync();
        Assert.False(await app.Services.GetRequiredService<INewsAlerts>().GetSoundAsync(CancellationToken.None));
    }

    /// <summary>Two workspaces, 火车 and 高铁 (open), a standing clock for the hints' time, and a window.</summary>
    internal sealed class Scene : IAsyncDisposable
    {
        private Scene(DesktopHarness app, ManualTime clock)
        {
            App = app;
            Clock = clock;
        }

        public DesktopHarness App { get; }

        public ManualTime Clock { get; }

        public Window Window { get; private set; } = null!;

        public WorkspaceNewsViewModel News => App.ShellPage.WorkspaceNews!;

        public Workspace Trains { get; private set; } = null!;

        public Workspace HighSpeed { get; private set; } = null!;

        public static async Task<Scene> StartAsync(Action<IServiceCollection>? overrides = null)
        {
            var clock = new ManualTime();
            var app = DesktopHarness.Create(s =>
            {
                s.AddSingleton(new WorkspaceNewsOptions { Time = clock });
                overrides?.Invoke(s);
            });
            var scene = new Scene(app, clock) { Window = app.ShowWindow() };
            scene.Trains = await app.StartWithWorkspaceAsync("火车");
            await app.ConfigureModelAsync();
            scene.HighSpeed = await scene.AddWorkspaceAsync("高铁");
            return scene;
        }

        /// <summary>Creates a workspace through the shell (which opens it), then goes back to 高铁 when that exists.</summary>
        public async Task<Workspace> AddWorkspaceAsync(string name)
        {
            App.ShellPage.NewWorkspace.Name = name;
            await App.ShellPage.NewWorkspace.SubmitCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => App.Workbench.CurrentWorkspace?.Name.Value == name);
            var created = App.Workbench.CurrentWorkspace!;
            if (HighSpeed is not null)
            {
                App.ShellPage.SelectedWorkspace = HighSpeed;
                await UiWait.UntilAsync(() => App.Workbench.CurrentWorkspace?.Id == HighSpeed.Id);
            }

            return created;
        }

        /// <summary>Starts a conversation in <paramref name="workspace"/> and waits until its answer has ended and been counted.</summary>
        public async Task<ConversationId> AnswerAsync(Workspace workspace, string question, bool expectHint = true)
        {
            var marks = App.Services.GetRequiredService<IConversationMarks>();
            var sent = await App.Services.GetRequiredService<ConversationService>().StartAsync(workspace.Id, question, CancellationToken.None);
            Assert.Null(sent.Rejection);
            await UiWait.UntilAsync(() => marks.IsUnread(sent.Conversation));
            if (expectHint)
            {
                await UiWait.UntilAsync(() => News.Toasts.Any(t => t.Workspace == workspace.Id && t.Conversations.Contains(sent.Conversation)));
            }

            return sent.Conversation;
        }

        /// <summary>Asks again in <paramref name="conversation"/> (off screen) and waits until that answer has ended and been noted.</summary>
        public async Task AskAsync(ConversationId conversation, string question)
        {
            var conversations = App.Services.GetRequiredService<ConversationService>();
            await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(conversation));
            var noted = News.MarksNoted;
            var asked = await conversations.AskAsync(conversation, question, CancellationToken.None);
            Assert.Null(asked.Rejection);
            await UiWait.UntilAsync(() => News.MarksNoted > noted);
        }

        /// <summary>Past the hints' 100 ms refresh delay and any refresh it started, so a hint that would come has come.</summary>
        public async Task SettledAsync()
        {
            await Task.Delay(300);
            await News.Loaded;
            Window.UpdateLayout();
        }

        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }
}
