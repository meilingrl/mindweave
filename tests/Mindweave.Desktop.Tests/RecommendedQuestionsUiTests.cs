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
using Mindweave.Application.Recommendations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using TextBlock = Avalonia.Controls.TextBlock;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Recommended questions on 新探索 (requirements §10.3.1, 2026-11-12) in the real window: the list under the input, the
/// first as a placeholder that Tab fills once, click and Enter only fill, sending uses one up, 不感兴趣 and 换一批, the
/// background preparing them (on a standing clock), failures said quietly, nothing without a model or content, never on
/// a conversation page, and the settings.
/// </summary>
public class RecommendedQuestionsUiTests
{
    private const string First = "蒸汽机车为什么被电力机车取代？";
    private const string Second = "高铁的轨道和普通铁轨有什么不同？";
    private const string Third = "火车的刹车是怎样工作的？";

    [AvaloniaFact]
    public async Task The_first_question_is_a_placeholder_in_the_empty_input_and_tab_fills_it_once()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var (page, view) = await OpenAsync(app, window);
        var input = Input(view);

        // Three questions under the input, each a button with what it is; the first two say what they extend.
        Assert.True(Find<StackPanel>(view, "RecommendationPanel").IsEffectivelyVisible);
        var buttons = QuestionButtons(view);
        Assert.Equal(3, buttons.Count);
        var basis = page.Recommendations[0].Recommendation.BasisName;
        Assert.NotNull(basis);
        Assert.Equal(UiText.RecommendedQuestionName(First, basis), AutomationProperties.GetName(buttons[0]));
        Assert.Equal(UiText.ExtendsFrom(basis), page.Recommendations[0].BasisText);
        Assert.Equal(UiText.RecommendedQuestionName(Third, null), AutomationProperties.GetName(buttons[2]));
        Assert.Equal(UiText.IgnoreRecommendationName(First), AutomationProperties.GetName(IgnoreButtons(view)[0]));
        Assert.Equal(UiText.RefreshRecommendationsName, AutomationProperties.GetName(Find<Button>(view, "RefreshRecommendationsButton")));
        Assert.False(Find<TextBlock>(view, "RecommendationEmptyHint").IsEffectivelyVisible);

        // The empty input offers the first one in place of the placeholder text, said in its help text.
        await UiWait.UntilAsync(() => input.IsFocused);
        window.UpdateLayout();
        Assert.Equal(First, input.Offered);
        Assert.True(input.ShowsGhost);
        Assert.Null(input.PlaceholderText);
        Assert.Equal($"推荐问题：{First}，按 Tab 填入", AutomationProperties.GetHelpText(input));

        // Tab fills it, the caret at the end, not sent; the placeholder text is back for later, and Tab now moves on.
        Tab(window);
        Assert.True(input.IsFocused);
        Assert.Equal(First, page.Composer.Text);
        Assert.Equal(First.Length, input.CaretIndex);
        Assert.Null(input.Offered);
        Assert.Equal(UiText.AskPlaceholder, input.PlaceholderText);
        Tab(window);
        Assert.False(input.IsFocused);
        Assert.Equal(First, page.Composer.Text);
        Assert.DoesNotContain(app.Client.Requests, r => FakeChatClient.IsAnswer(r) && r.Turns[^1].Content == First);
        Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);

        // Emptied, it is offered again; typing hides it.
        page.Composer.Text = string.Empty;
        window.UpdateLayout();
        Assert.Equal(First, input.Offered);
        input.Focus();
        window.KeyTextInput("火");
        Assert.Null(input.Offered);
        Assert.False(input.ShowsGhost);
        Assert.Null(AutomationProperties.GetHelpText(input));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Click_and_enter_only_fill_the_input_and_sending_an_edited_one_uses_it_up()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var (page, view) = await OpenAsync(app, window);
        var input = Input(view);

        UiInput.Click(window, QuestionButtons(view)[1]);
        Assert.Equal(Second, page.Composer.Text);
        Assert.True(input.IsFocused);
        Assert.Equal(Second.Length, input.CaretIndex);

        // The list from the keyboard: Down and Up move between the questions; Enter fills.
        QuestionButtons(view)[0].Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.True(QuestionButtons(view)[1].IsFocused);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.True(QuestionButtons(view)[2].IsFocused);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.True(QuestionButtons(view)[2].IsFocused);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.True(QuestionButtons(view)[1].IsFocused);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(Third, page.Composer.Text);
        Assert.True(input.IsFocused);
        Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);

        // Edited, then sent: the conversation starts with the edited text, and the question it came from is used up.
        window.KeyTextInput("吗");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel);
        await page.Marking;
        var rows = await Store(app).ListAsync(app.Workbench.CurrentWorkspace!.Id, RecommendationKind.Question, CancellationToken.None);
        var used = rows.Single(r => r.Question == Third);
        Assert.Equal(RecommendationState.Used, used.State);
        Assert.Contains(app.Client.Requests, r => FakeChatClient.IsAnswer(r) && r.Turns[^1].Content == Third + "吗");

        // Edited or not, the conversation remembers the recommendation and its basis (requirements §10.3.5).
        var made = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Id;
        Assert.Equal((used.Id.ToString(), "question", used.Basis?.ToString()), await StartingAsync(app, made));

        // Next time: the two left, the first the placeholder.
        var (again, againView) = await OpenAsync(app, window);
        Assert.Equal([First, Second], again.Recommendations.Select(r => r.Question));
        Assert.Equal(First, Input(againView).Offered);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_question_typed_after_the_filled_one_was_cleared_starts_a_conversation_that_remembers_no_recommendation()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var (page, view) = await OpenAsync(app, window);

        UiInput.Click(window, QuestionButtons(view)[0]);
        Assert.Equal(First, page.Composer.Text);
        page.Composer.Text = string.Empty;
        window.KeyTextInput("铁路信号是怎么发明的？");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel);
        await page.Marking;

        var made = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Id;
        Assert.Equal(((string?)null, (string?)null, (string?)null), await StartingAsync(app, made));
        var rows = await Store(app).ListAsync(app.Workbench.CurrentWorkspace!.Id, RecommendationKind.Question, CancellationToken.None);
        Assert.Equal(RecommendationState.Pending, rows.Single(r => r.Question == First).State);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Not_interested_removes_and_remembers_and_change_the_batch_keeps_the_old_ones_until_new_ones_arrive()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var (page, view) = await OpenAsync(app, window);
        var input = Input(view);

        // 不感兴趣 from the keyboard: gone, remembered, and the focus moves to the question now in its place.
        var dismissed = page.Recommendations[0];
        IgnoreButtons(view)[0].Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => page.Recommendations.Count == 2 && !dismissed.IgnoreCommand.IsRunning);
        await UiWait.UntilAsync(() => QuestionButtons(view).Count == 2 && QuestionButtons(view)[0].IsFocused);
        Assert.Equal(Second, input.Offered);
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        Assert.Equal(RecommendationState.Ignored, (await Store(app).ListAsync(workspace, RecommendationKind.Question, CancellationToken.None)).Single(r => r.Id == dismissed.Recommendation.Id).State);

        // 换一批: while it runs the old ones stay, with a line saying so; the ignored one is not offered again.
        var gate = app.Client.Recommendations.Hold();
        app.Client.Recommendations.Replies.Enqueue($"[\"{First}\", \"铁路信号灯怎么工作？\", \"火车为什么要编组？\"]");
        UiInput.Click(window, Find<Button>(view, "RefreshRecommendationsButton"));
        await UiWait.UntilAsync(() => page.IsRefreshing);
        window.UpdateLayout();
        Assert.True(Find<StackPanel>(view, "RecommendationBusyLine").IsEffectivelyVisible);
        Assert.Equal(UiText.RefreshingRecommendations, page.BusyText);
        Assert.Equal(2, QuestionButtons(view).Count);
        Assert.False(Find<Button>(view, "RefreshRecommendationsButton").IsEffectivelyEnabled);
        app.Client.Recommendations.Gate = null;
        gate.TrySetResult();
        await UiWait.UntilAsync(() => !page.IsRefreshing);
        await page.Reloaded;
        window.UpdateLayout();
        Assert.Equal(["铁路信号灯怎么工作？", "火车为什么要编组？"], page.Recommendations.Select(r => r.Question));
        Assert.Equal("铁路信号灯怎么工作？", input.Offered);
        Assert.False(Find<StackPanel>(view, "RecommendationBusyLine").IsEffectivelyVisible);

        // A failure says why with 重试; what was shown stays.
        app.Client.Recommendations.FailNext(GenerationErrorKind.Network);
        UiInput.Click(window, Find<Button>(view, "RefreshRecommendationsButton"));
        await UiWait.UntilAsync(() => page.Note is not null);
        window.UpdateLayout();
        Assert.Equal(UiText.RecommendationProblemText(RecommendationProblem.Failed, GenerationErrorKind.Network), Find<TextBlock>(view, "RecommendationNote").Text);
        Assert.True(Find<Button>(view, "RetryRecommendationsButton").IsEffectivelyVisible);
        Assert.False(Find<Button>(view, "RecommendationSettingsButton").IsEffectivelyVisible);
        Assert.Equal(2, QuestionButtons(view).Count);

        // 重试 asks again and clears the note.
        app.Client.Recommendations.Replies.Enqueue("[\"轨距是多少？\"]");
        UiInput.Click(window, Find<Button>(view, "RetryRecommendationsButton"));
        await UiWait.UntilAsync(() => page.Recommendations.Count == 1 && !page.IsRefreshing);
        window.UpdateLayout();
        Assert.Null(page.Note);
        Assert.False(Find<WrapPanel>(view, "RecommendationNoteLine").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Opening_the_page_prepares_questions_in_the_background_and_a_failure_there_is_said_quietly()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var (page, view) = await OpenAsync(app, window);

        // Nothing yet, nothing said while the background is about to look (自动准备 on): the heading and 换一批 only.
        Assert.Empty(page.Recommendations);
        Assert.True(Find<StackPanel>(view, "RecommendationPanel").IsEffectivelyVisible);
        Assert.False(Find<TextBlock>(view, "RecommendationEmptyHint").IsEffectivelyVisible);
        Assert.Null(Input(view).Offered);
        Assert.Equal(UiText.AskPlaceholder, Input(view).PlaceholderText);

        var gate = app.Client.Recommendations.Hold();
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await UiWait.UntilAsync(() => page.IsPreparing);
        window.UpdateLayout();
        Assert.True(Find<StackPanel>(view, "RecommendationBusyLine").IsEffectivelyVisible);
        Assert.Equal(UiText.PreparingRecommendations, page.BusyText);
        app.Client.Recommendations.Gate = null;
        gate.TrySetResult();
        await UiWait.UntilAsync(() => page.Recommendations.Count == 3 && !page.IsPreparing);
        window.UpdateLayout();
        Assert.Equal(First, Input(view).Offered);
        Assert.False(Find<StackPanel>(view, "RecommendationBusyLine").IsEffectivelyVisible);

        // All used or ignored: prepared again; when that fails, a quiet line with 重试, nothing else.
        app.Client.Recommendations.FailNext(GenerationErrorKind.ServerError);
        // (From the keyboard: the seeded answers' new-answer hint sits over the right end of the rows at this size.)
        foreach (var left in new[] { 2, 1, 0 })
        {
            var item = page.Recommendations[0];
            IgnoreButtons(view)[0].Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiWait.UntilAsync(() => page.Recommendations.Count == left && !item.IgnoreCommand.IsRunning);
            window.UpdateLayout();

            // The focus moves on once the row is gone (posted); waited for, so it does not land after the next one is focused.
            await UiWait.UntilAsync(() => left == 0 ? Find<Button>(view, "RefreshRecommendationsButton").IsFocused : QuestionButtons(view).Count == left && QuestionButtons(view)[0].IsFocused);
        }

        await UiWait.UntilAsync(() => page.Recommendations.Count == 0);
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await app.Services.GetRequiredService<RecommendationService>().SettledAsync();
        await UiWait.UntilAsync(() => page.Note == UiText.RecommendationsQuietFailure);
        window.UpdateLayout();
        Assert.True(Find<Button>(view, "RetryRecommendationsButton").IsEffectivelyVisible);
        Assert.Equal(2, app.Client.Requests.Count(FakeChatClient.IsRecommendation));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Without_a_model_nothing_is_shown_or_sent_until_change_the_batch_explains()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync((c, t) => c.Execute(t, "DELETE FROM app_state WHERE key = 'active_model_config_id';"), CancellationToken.None);
        var (page, view) = await OpenAsync(app, window);
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await app.Services.GetRequiredService<RecommendationService>().SettledAsync();
        await page.Reloaded;
        window.UpdateLayout();
        Assert.Empty(page.Recommendations);
        Assert.Null(page.Note);
        Assert.False(Find<WrapPanel>(view, "RecommendationNoteLine").IsEffectivelyVisible);

        UiInput.Click(window, Find<Button>(view, "RefreshRecommendationsButton"));
        await UiWait.UntilAsync(() => page.Note is not null);
        window.UpdateLayout();
        Assert.Equal(UiText.RecommendationProblemText(RecommendationProblem.NotConfigured, null), page.Note);
        Assert.True(Find<Button>(view, "RecommendationSettingsButton").IsEffectivelyVisible);
        Assert.False(Find<Button>(view, "RetryRecommendationsButton").IsEffectivelyVisible);
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsRecommendation);

        UiInput.Click(window, Find<Button>(view, "RecommendationSettingsButton"));
        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Nothing_to_draw_on_shows_nothing_and_a_conversation_page_never_shows_recommendations()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("空的");
        await app.ConfigureModelAsync();
        var (page, view) = await OpenAsync(app, window);
        Assert.False(page.ShowsRecommendations);
        Assert.False(Find<StackPanel>(view, "RecommendationPanel").IsEffectivelyVisible);
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await app.Services.GetRequiredService<RecommendationService>().SettledAsync();
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsRecommendation);

        // Sending the first question: a conversation page, whose input offers nothing.
        page.Composer.Text = "火车是怎么发明的？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel);
        window.UpdateLayout();
        var conversation = (ConversationViewModel)app.Workbench.CurrentPage!;
        Assert.Null(conversation.Composer.GhostSuggestions);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<StackPanel>(), p => p.Name == "RecommendationPanel");
        Assert.Null(Find<GhostTextBox>(CurrentView(window), "Input").Offered);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_settings_turn_questions_off_and_say_what_is_sent()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 900);
        await SeedAsync(app);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        window.UpdateLayout();

        foreach (var (name, text) in new[] { ("RecommendQuestionsToggle", UiText.RecommendQuestionsSetting), ("RecommendNodesToggle", UiText.RecommendNodesSetting), ("AutoPrepareRecommendationsToggle", UiText.AutoPrepareRecommendations) })
        {
            var toggle = Find<CheckBox>(window, name);
            Assert.True(toggle.IsChecked);
            Assert.Equal(text, AutomationProperties.GetName(toggle));
            Assert.Equal(UiText.RecommendationsHint, AutomationProperties.GetHelpText(toggle));
        }

        // What is sent and that it costs a little stays in sight; the rest is behind the row's icon.
        var row = Find<CheckBox>(window, "AutoPrepareRecommendationsToggle").FindAncestorOfType<Views.Pages.SettingRow>()!;
        Assert.Equal(UiText.RecommendationsHint, row.Info);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == UiText.RecommendationsCost);
        UiInput.Click(window, Find<CheckBox>(window, "RecommendQuestionsToggle"));
        // Clear of the news hint, which lies over the page's lower right corner.
        Find<ScrollViewer>(window.GetVisualDescendants().OfType<Views.Pages.SettingsHomeView>().Single(), "Scroller").Offset = new Vector(0, 300);
        window.UpdateLayout();
        window.CaptureRenderedFrame();
        UiInput.Click(window, Find<CheckBox>(window, "AutoPrepareRecommendationsToggle"));
        await settings.Saving;
        var stored = app.Services.GetRequiredService<IRecommendationSettings>();
        Assert.False(await stored.GetQuestionsAsync(CancellationToken.None));
        Assert.False(await stored.GetAutoPrepareAsync(CancellationToken.None));
        Assert.True(await stored.GetNodesAsync(CancellationToken.None));

        // Off: 新探索 shows none, though some are kept, and the input offers nothing.
        var (page, view) = await OpenAsync(app, window);
        Assert.False(page.ShowsRecommendations);
        Assert.False(Find<StackPanel>(view, "RecommendationPanel").IsEffectivelyVisible);
        Assert.Null(Input(view).Offered);

        // Back on, with 自动准备 off: kept ones show; once they are gone, a line says how to get more.
        await stored.SetQuestionsAsync(true, CancellationToken.None);
        app.Workbench.GoBack();
        (page, view) = await OpenAsync(app, window);
        Assert.Equal(3, page.Recommendations.Count);
        foreach (var left in new[] { 2, 1, 0 })
        {
            var item = page.Recommendations[0];
            IgnoreButtons(view)[0].Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiWait.UntilAsync(() => page.Recommendations.Count == left && !item.IgnoreCommand.IsRunning);
            window.UpdateLayout();

            // The focus moves on once the row is gone (posted); waited for, so it does not land after the next one is focused.
            await UiWait.UntilAsync(() => left == 0 ? Find<Button>(view, "RefreshRecommendationsButton").IsFocused : QuestionButtons(view).Count == left && QuestionButtons(view)[0].IsFocused);
        }

        await page.Reloaded;
        window.UpdateLayout();
        Assert.True(Find<TextBlock>(view, "RecommendationEmptyHint").IsEffectivelyVisible);
        app.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await app.Services.GetRequiredService<RecommendationService>().SettledAsync();
        Assert.Single(app.Client.Requests, FakeChatClient.IsRecommendation);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_new_exploration_page_kept_open_follows_the_settings_when_the_reader_returns()
    {
        // Review 2026-11-13: each section keeps its pages, so 新探索 left open in 探索 is the page shown on return. Turning
        // recommended questions off in 设置 left that page offering them, the placeholder included.
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 900);
        await SeedAsync(app);
        var (page, view) = await OpenAsync(app, window);
        Assert.Equal(3, page.Recommendations.Count);

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        window.UpdateLayout();
        UiInput.Click(window, Find<CheckBox>(window, "RecommendQuestionsToggle"));
        await settings.Saving;

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        Assert.Same(page, app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => !page.ShowsRecommendations && page.Recommendations.Count == 0);
        window.UpdateLayout();
        view = window.GetVisualDescendants().OfType<NewConversationView>().Single();
        Assert.False(Find<StackPanel>(view, "RecommendationPanel").IsEffectivelyVisible);
        Assert.Null(page.Composer.GhostSuggestions);
        Assert.Null(Input(view).Offered);

        // On again: the kept ones are back.
        await app.Services.GetRequiredService<IRecommendationSettings>().SetQuestionsAsync(true, CancellationToken.None);
        await UiWait.UntilAsync(() => page.ShowsRecommendations && page.Recommendations.Count == 3);
        Assert.Equal(First, page.Composer.GhostSuggestions?.Single());
        window.Close();
    }

    /// <summary>
    /// A reload that read the questions before a 不感兴趣 was stored (the one the previous 不感兴趣 or a settings change set
    /// off) must not bring the question back when it lands: that rebuilt the rows and took away the focus the removal had
    /// just moved on, so a quick second 不感兴趣 lost the keyboard (and the settings test timed out waiting for the focus).
    /// </summary>
    [AvaloniaFact]
    public async Task A_reload_that_read_before_an_ignore_was_stored_does_not_bring_the_question_back()
    {
        HeldRecommendationStore? store = null;
        await using var app = DesktopHarness.Create(services =>
            services.AddSingleton<IRecommendationStore>(sp => store = new HeldRecommendationStore(new SqliteRecommendationStore(sp.GetRequiredService<SqliteDatabase>()))));
        var window = app.ShowWindow(1100, 900);
        await SeedAsync(app);
        var (page, view) = await OpenAsync(app, window);
        Assert.Equal(3, page.Recommendations.Count);

        // A reload reads the three, and is slow to land.
        var read = store!.HoldNextList();
        await app.Services.GetRequiredService<IRecommendationSettings>().SetNodesAsync(true, CancellationToken.None);
        await read.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Meanwhile 不感兴趣 on the first, its ignoring not stored yet; the focus moves to the question now in its place.
        var write = store.HoldNextWrite();
        var ignored = page.Recommendations[0];
        IgnoreButtons(view)[0].Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(2, page.Recommendations.Count);
        await UiWait.UntilAsync(() => QuestionButtons(view).Count == 2 && QuestionButtons(view)[0].IsFocused);
        var focused = QuestionButtons(view)[0];

        // The slow reload lands: the question stays gone and the focus stays where it went.
        var stale = page.Reloaded;
        read.Release.SetResult();
        await UiWait.UntilAsync(() => stale.IsCompleted);
        window.UpdateLayout();
        Assert.DoesNotContain(page.Recommendations, r => r.Recommendation.Id == ignored.Recommendation.Id);
        Assert.Equal(2, page.Recommendations.Count);
        Assert.True(focused.IsFocused);

        write.SetResult();
        await UiWait.UntilAsync(() => !ignored.IgnoreCommand.IsRunning);
        Assert.Equal(2, page.Recommendations.Count);
        Assert.True(focused.IsFocused);
        window.Close();
    }

    /// <summary>The real store; its next question list (after the read) or next state change can be held.</summary>
    private sealed class HeldRecommendationStore(IRecommendationStore inner) : IRecommendationStore
    {
        private HeldRead? _read;
        private TaskCompletionSource? _write;

        public sealed record HeldRead(TaskCompletionSource Reached, TaskCompletionSource Release);

        public HeldRead HoldNextList() =>
            _read = new HeldRead(new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));

        public TaskCompletionSource HoldNextWrite() => _write = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<Recommendation>> ListAsync(Core.Workspaces.WorkspaceId workspace, RecommendationKind kind, CancellationToken cancellationToken)
        {
            var rows = await inner.ListAsync(workspace, kind, cancellationToken).ConfigureAwait(false);
            if (kind == RecommendationKind.Question && Interlocked.Exchange(ref _read, null) is { } held)
            {
                held.Reached.SetResult();
                await held.Release.Task.ConfigureAwait(false);
            }

            return rows;
        }

        public async Task<bool> SetStateAsync(RecommendationId id, RecommendationState state, string normalized, DateTimeOffset at, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _write, null) is { } held)
            {
                await held.Task.ConfigureAwait(false);
            }

            return await inner.SetStateAsync(id, state, normalized, at, cancellationToken).ConfigureAwait(false);
        }

        public Task<int?> AddBatchAsync(Core.Workspaces.WorkspaceId workspace, RecommendationKind kind, RecommendationBatch batch, bool replacePending, CancellationToken cancellationToken) =>
            inner.AddBatchAsync(workspace, kind, batch, replacePending, cancellationToken);

        public Task<int> RemovePendingAsync(Core.Workspaces.WorkspaceId workspace, RecommendationKind kind, IReadOnlyCollection<RecommendationId> ids, CancellationToken cancellationToken) =>
            inner.RemovePendingAsync(workspace, kind, ids, cancellationToken);

        public Task<RecommendationBases> ListBasesAsync(Core.Workspaces.WorkspaceId workspace, int limit, int bodyChars, CancellationToken cancellationToken) =>
            inner.ListBasesAsync(workspace, limit, bodyChars, cancellationToken);
    }

    /// <summary>The network scenario's workspace with three questions made (by 换一批's request, as the fake answers it).</summary>
    internal static async Task SeedAsync(DesktopHarness app)
    {
        await SummaryNetworkScenario.SeedAsync(app);
        var outcome = await app.Services.GetRequiredService<IRecommendations>().RefreshQuestionsAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);
        Assert.Equal(new RecommendationOutcome(3), outcome);
    }

    internal static async Task<(NewConversationViewModel Page, NewConversationView View)> OpenAsync(DesktopHarness app, Window window)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var page = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        window.UpdateLayout();
        var view = window.GetVisualDescendants().OfType<NewConversationView>().Single();

        // The page puts the focus in the input once it is loaded; waited for, so it does not move a focus set later.
        await UiWait.UntilAsync(() => Input(view).IsFocused);
        window.UpdateLayout();
        return (page, view);
    }

    private static GhostTextBox Input(NewConversationView view) => Find<GhostTextBox>(view, "Input");

    private static List<Button> QuestionButtons(NewConversationView view) =>
        [.. view.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "RecommendedQuestionButton")];

    private static List<Button> IgnoreButtons(NewConversationView view) =>
        [.. view.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "IgnoreRecommendationButton")];

    private static IRecommendationStore Store(DesktopHarness app) => app.Services.GetRequiredService<IRecommendationStore>();

    /// <summary>What the conversation remembers of the recommendation it was started from (requirements §10.3.5), or nulls.</summary>
    internal static Task<(string? Recommendation, string? Kind, string? Basis)> StartingAsync(DesktopHarness app, Core.Conversations.ConversationId conversation) =>
        app.Services.GetRequiredService<Infrastructure.Sqlite.SqliteDatabase>().ReadAsync(
            c =>
            {
                using var command = c.Command(null, "SELECT recommendation_id, kind, basis_node_id FROM starting_recommendation WHERE conversation_id = $id;", ("$id", conversation.ToString()));
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))
                    : ((string?)null, (string?)null, (string?)null);
            },
            CancellationToken.None);

    private static void Tab(Window window) => window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
}
