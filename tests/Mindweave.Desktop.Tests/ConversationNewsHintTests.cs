using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using static Mindweave.Desktop.Tests.UiTree;
using ConversationId = Mindweave.Core.Conversations.ConversationId;
using Scene = Mindweave.Desktop.Tests.WorkspaceNewsHintTests.Scene;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The pop-up hint for a conversation of the open workspace that is not on screen (accepted 2026-11-10): the same card as
/// for another workspace, naming the conversation, and opening it when clicked. 高铁 is the open workspace throughout.
/// </summary>
public class ConversationNewsHintTests
{
    private static readonly TimeSpan Duration = WorkspaceNewsOptions.DefaultToastDuration;

    [AvaloniaFact]
    public async Task An_answer_in_a_conversation_of_the_open_workspace_pops_up_its_hint_with_the_sidebar_title_and_the_badge_ignores_it()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window, news) = (scene.App, scene.Window, scene.News);
        Find<Button>(window, "NewConversationButton").Focus();
        var focused = window.FocusManager!.GetFocusedElement();

        var answered = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        var toast = Assert.Single(news.Toasts);
        Assert.Equal(answered, toast.Conversation);
        Assert.Equal(scene.HighSpeed.Id, toast.Workspace);

        // Named as the sidebar names it, following it when the conversation is then titled automatically.
        var item = await SidebarItemAsync(app, answered);
        await UiWait.UntilAsync(() => item.Title == Testing.FakeChatClient.DefaultTitle && toast.Name == item.Title);
        Assert.Equal("「" + item.Title + "」有新回答", toast.Text);

        // The same card in the same place: read out politely, the focus stays, and it says what clicking does.
        window.UpdateLayout();
        Assert.Equal(toast.Text, Find<TextBlock>(window, "NewsToastText").Text);
        Assert.Equal(UiText.ConversationNewsToastAction, Find<TextBlock>(window, "NewsToastAction").Text);
        var body = Find<Button>(window, "OpenNewsToast");
        Assert.True(body.IsEffectivelyVisible);
        Assert.Equal(toast.Text + "。" + UiText.ConversationNewsToastAction, AutomationProperties.GetName(body));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(body));
        Assert.Same(focused, window.FocusManager!.GetFocusedElement());

        // The closed picker's badge counts other workspaces only.
        await scene.SettledAsync();
        Assert.Equal(0, news.Count);
        Assert.False(Find<Border>(window, "OtherWorkspacesNews").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_long_title_is_cut_with_an_ellipsis_and_read_out_whole()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window, news) = (scene.App, scene.Window, scene.News);
        var answered = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        var toast = Assert.Single(news.Toasts);

        const string Long = "京沪高铁为什么能跑出每小时三百五十公里的速度";
        await app.Services.GetRequiredService<ConversationService>().RenameAsync(answered, Long, CancellationToken.None);
        await UiWait.UntilAsync(() => toast.Name == Long);
        Assert.Equal("「京沪高铁为什么能跑出每小时三百…」有新回答", toast.Text);
        window.UpdateLayout();
        Assert.Equal(toast.Text, Find<TextBlock>(window, "NewsToastText").Text);
        Assert.Equal("「" + Long + "」有新回答。" + UiText.ConversationNewsToastAction, AutomationProperties.GetName(Find<Button>(window, "OpenNewsToast")));

        // Counted as the reader sees characters: at the limit nothing is cut, and an emoji is never split.
        Assert.Equal("十六个字的标题刚好放得下不会截断", UiText.Shortened("十六个字的标题刚好放得下不会截断", 16));
        Assert.Equal("ab👍🏽…", UiText.Shortened("ab👍🏽cdef", 4));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_the_hint_opens_its_conversation()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window, news) = (scene.App, scene.Window, scene.News);
        var answered = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        window.UpdateLayout();

        UiInput.Click(window, Find<Button>(window, "OpenNewsToast"));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(answered, page.Id);
        Assert.Equal(scene.HighSpeed.Id, app.Workbench.CurrentWorkspace?.Id);
        Assert.Empty(news.Toasts);
        Assert.False(app.Services.GetRequiredService<IConversationMarks>().IsUnread(answered));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Opening_the_conversation_from_the_sidebar_takes_its_hint_away_at_once_and_leaves_the_others()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window, news) = (scene.App, scene.Window, scene.News);
        var first = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        var second = await scene.AnswerAsync(scene.HighSpeed, "复兴号有几节车厢？");
        Assert.Equal([first, second], news.Toasts.Select(t => t.Conversation));
        await SidebarItemAsync(app, first);

        // Gone as the conversation shows, not only once the marks are counted again.
        UiInput.Click(window, Row(window, first));
        Assert.Equal(new ConversationRoute(first), app.Workbench.CurrentPage?.Route);
        Assert.Equal([second], news.Toasts.Select(t => t.Conversation));
        await scene.SettledAsync();
        Assert.Equal([second], news.Toasts.Select(t => t.Conversation));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_hint_goes_when_its_conversation_is_seen_another_way_or_archived()
    {
        await using var scene = await Scene.StartAsync();
        var (app, news) = (scene.App, scene.News);
        var seen = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        var archived = await scene.AnswerAsync(scene.HighSpeed, "复兴号有几节车厢？");
        Assert.Equal(2, news.Toasts.Count);

        // Its mark cleared without the page changing (as when it is shown elsewhere): the hint follows the mark.
        var marks = app.Services.GetRequiredService<IConversationMarks>();
        marks.SetViewing(seen);
        marks.SetViewing(null);
        await UiWait.UntilAsync(() => news.Toasts.All(t => t.Conversation != seen));

        // Archived, it leaves the sidebar, and its hint goes with it.
        await UiWait.UntilAsync(() => !app.Services.GetRequiredService<ConversationService>().HasUnfinishedAnswer(archived));
        var outcome = await app.Services.GetRequiredService<ConversationService>().ArchiveAsync(archived, CancellationToken.None);
        Assert.True(outcome.Archived, outcome.ToString());
        await UiWait.UntilAsync(() => news.Toasts.Count == 0);
        scene.Window.Close();
    }

    [AvaloniaFact]
    public async Task Nothing_pops_up_for_the_conversation_on_screen()
    {
        await using var scene = await Scene.StartAsync();
        var (app, news) = (scene.App, scene.News);
        var answered = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(answered)));
        Assert.Empty(news.Toasts);

        // A further answer while it is on screen: no mark, no hint.
        var noted = news.MarksNoted;
        var conversations = app.Services.GetRequiredService<ConversationService>();
        await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(answered));
        var asked = await conversations.AskAsync(answered, "比飞机快吗？", CancellationToken.None);
        Assert.Null(asked.Rejection);
        await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(answered));
        await scene.SettledAsync();
        Assert.Equal(noted, news.MarksNoted);
        Assert.Empty(news.Toasts);
        scene.Window.Close();
    }

    [AvaloniaFact]
    public async Task One_hint_per_conversation_a_further_answer_restarts_it_and_it_closes_itself_after_its_time()
    {
        await using var scene = await Scene.StartAsync();
        var (app, news) = (scene.App, scene.News);
        var answered = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        var toast = Assert.Single(news.Toasts);

        // Most of its time later, a further answer in the same conversation: the same hint, with its full time again.
        scene.Clock.Advance(Duration - TimeSpan.FromSeconds(2));
        await scene.AskAsync(answered, "比飞机快吗？");
        await scene.SettledAsync();
        Assert.Same(toast, Assert.Single(news.Toasts));
        scene.Clock.Advance(Duration - TimeSpan.FromSeconds(1));
        await scene.SettledAsync();
        Assert.Single(news.Toasts);
        scene.Clock.Advance(TimeSpan.FromSeconds(2));
        await UiWait.UntilAsync(() => news.Toasts.Count == 0);

        // Only the pop-up is temporary: the sidebar still marks the conversation.
        Assert.True((await SidebarItemAsync(app, answered)).IsUnread);
        scene.Window.Close();
    }

    [AvaloniaFact]
    public async Task The_hint_stays_under_the_pointer_and_its_close_button_takes_it_away()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window, news) = (scene.App, scene.Window, scene.News);
        var answered = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        window.UpdateLayout();
        var card = Find<Button>(window, "OpenNewsToast");
        window.MouseMove(card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), window)!.Value);
        scene.Clock.Advance(Duration * 3);
        await scene.SettledAsync();
        Assert.Single(news.Toasts);

        UiInput.Click(window, Find<Button>(window, "CloseNewsToast"));
        Assert.Empty(news.Toasts);
        Assert.IsNotType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.True((await SidebarItemAsync(app, answered)).IsUnread);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hints_of_both_kinds_stack_up_to_three_and_the_oldest_goes()
    {
        await using var scene = await Scene.StartAsync();
        var news = scene.News;
        var subway = await scene.AddWorkspaceAsync("地铁");
        await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        var first = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        await scene.AnswerAsync(subway, "地铁是什么时候出现的？");
        var last = await scene.AnswerAsync(scene.HighSpeed, "复兴号有几节车厢？");

        Assert.Equal(3, news.Toasts.Count);
        Assert.Equal(
            [(first, scene.HighSpeed.Id), (null, subway.Id), (last, scene.HighSpeed.Id)],
            news.Toasts.Select(t => ((ConversationId?)t.Conversation, t.Workspace)));
        scene.Window.Close();
    }

    [AvaloniaFact]
    public async Task A_conversations_hint_outlasts_a_workspace_switch_and_stays_apart_from_its_workspaces_hint()
    {
        await using var scene = await Scene.StartAsync();
        var (app, news) = (scene.App, scene.News);
        var first = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");

        // The user moves to 火车: the conversation still has news, so its hint stays (clicking it would switch back).
        app.ShellPage.SelectedWorkspace = scene.Trains;
        await UiWait.UntilAsync(() => news.Count == 1);
        await scene.SettledAsync();
        Assert.Equal([first], news.Toasts.Select(t => t.Conversation));

        // A further answer in 高铁, now another workspace: its own workspace hint, beside the conversation's.
        var second = await scene.AnswerAsync(scene.HighSpeed, "复兴号有几节车厢？");
        Assert.Equal([(first, 1), (null, 2)], news.Toasts.Select(t => ((ConversationId?)t.Conversation, t.Count)));
        Assert.Equal("「" + news.Toasts[0].Name + "」有新回答", news.Toasts[0].Text);
        Assert.Equal("高铁：2 个会话有新回答", news.Toasts[1].Text);

        // Back in 高铁: the workspace's hint has done its job; the conversation's stays until the conversation shows.
        app.ShellPage.SelectedWorkspace = scene.HighSpeed;
        Assert.Equal([(ConversationId?)first], news.Toasts.Select(t => t.Conversation));
        Assert.True(app.Services.GetRequiredService<IConversationMarks>().IsUnread(second));
        scene.Window.Close();
    }

    [AvaloniaFact]
    public async Task With_the_setting_off_only_other_workspaces_pop_up_and_the_sidebar_still_marks_the_conversation()
    {
        await using var scene = await Scene.StartAsync();
        var (app, window, news) = (scene.App, scene.Window, scene.News);

        // On by default; turned off on the settings page.
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        Assert.True(settings.ShowsInWorkspaceNews);
        window.UpdateLayout();
        var toggle = Find<CheckBox>(window, "NewsInWorkspaceToggle");
        Assert.Equal(UiText.NewsInWorkspace, toggle.Content);
        Assert.True(toggle.IsChecked);
        UiInput.Click(window, toggle);
        Assert.False(settings.ShowsInWorkspaceNews);
        await settings.Saving;

        var open = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？", expectHint: false);
        await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        await scene.SettledAsync();
        Assert.Equal([(ConversationId?)null], news.Toasts.Select(t => t.Conversation));
        Assert.True((await SidebarItemAsync(app, open)).IsUnread);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_sound_plays_for_a_conversations_hint_when_chosen()
    {
        await using var scene = await Scene.StartAsync();
        var app = scene.App;
        await app.Services.GetRequiredService<INewsAlerts>().SetSoundAsync(true, CancellationToken.None);

        await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        await scene.News.Alerting;
        Assert.Equal(1, app.Sound.Played);
        scene.Window.Close();
    }

    [AvaloniaFact]
    public async Task The_setting_is_kept_across_a_restart_and_marks_restored_at_start_pop_up_nothing()
    {
        await using var scene = await Scene.StartAsync();
        var app = scene.App;
        var answered = await scene.AnswerAsync(scene.HighSpeed, "高铁有多快？");
        scene.Window.Close();

        // After a restart, with the hints still on: the conversation is marked again, but nothing pops up for it.
        await app.RestartAsync();
        Assert.Equal(scene.HighSpeed.Id, app.Workbench.CurrentWorkspace?.Id);
        Assert.True(await app.Services.GetRequiredService<INewsAlerts>().GetInWorkspaceAsync(CancellationToken.None));
        await SidebarItemAsync(app, answered);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Single(i => i.Id == answered).IsUnread);
        var news = app.ShellPage.WorkspaceNews!;
        await Task.Delay(300);
        await news.Loaded;
        Assert.Empty(news.Toasts);

        // Turned off, it stays off after a restart.
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        settings.ShowsInWorkspaceNews = false;
        await settings.Saving;
        await app.RestartAsync();
        Assert.False(await app.Services.GetRequiredService<INewsAlerts>().GetInWorkspaceAsync(CancellationToken.None));
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var again = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await again.Loaded;
        Assert.False(again.ShowsInWorkspaceNews);

        // And on again, the same way.
        again.ShowsInWorkspaceNews = true;
        await again.Saving;
        await app.RestartAsync();
        Assert.True(await app.Services.GetRequiredService<INewsAlerts>().GetInWorkspaceAsync(CancellationToken.None));
    }

    private static async Task<ConversationItemViewModel> SidebarItemAsync(DesktopHarness app, ConversationId id)
    {
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Any(i => i.Id == id));
        return app.ShellPage.Conversations.Items.Single(i => i.Id == id);
    }

    private static Button Row(Window window, ConversationId id)
    {
        window.UpdateLayout();
        var list = window.GetVisualDescendants().OfType<ShellView>().Single().GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ConversationList");
        return list.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is ConversationItemViewModel item && item.Id == id);
    }
}
