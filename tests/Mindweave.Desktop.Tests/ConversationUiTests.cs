using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

public class ConversationUiTests
{
    [AvaloniaFact]
    public async Task Asking_from_the_new_conversation_page_opens_the_conversation_and_lists_it()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();

        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "蒸汽机是谁发明的？";
        await draft.Composer.SendCommand.ExecuteAsync(null);

        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await UiWait.StoredAsync(page, 1, MessageState.Complete);
        Assert.Equal("答：蒸汽机是谁发明的？", page.Messages[1].Body);
        Assert.True(page.Messages[1].CanRetry);
        Assert.Equal(UiText.Regenerate, page.Messages[1].RetryLabel);

        // Replacing the draft did not leave it in history: back goes to the explore home.
        Assert.True(app.Workbench.CanGoBack);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 1 && app.ShellPage.Conversations.Items[0].Title == FakeChatClient.DefaultTitle);
        Assert.True(app.ShellPage.Conversations.Items[0].IsSelected);
        await UiWait.UntilAsync(() => page.Title == FakeChatClient.DefaultTitle);
        Assert.Equal(FakeChatClient.DefaultTitle, app.ShellPage.Title);
    }

    [AvaloniaFact]
    public async Task A_title_that_arrives_after_the_answer_reaches_the_open_page()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var titles = app.Client.HoldTitles();

        var page = await OpenConversationAsync(app, "蒸汽机是谁发明的？", null);
        await UiWait.UntilAsync(() => page.Messages[1].State == MessageState.Complete);
        await UiWait.UntilAsync(() => app.Client.Requests.Any(r => r.Turns[0].Role == ChatRole.System));
        Assert.NotEqual(FakeChatClient.DefaultTitle, page.Title);

        titles.SetResult();
        await UiWait.UntilAsync(() => page.Title == FakeChatClient.DefaultTitle);
        Assert.Equal(FakeChatClient.DefaultTitle, app.ShellPage.Title);
    }

    [AvaloniaFact]
    public async Task Without_a_model_the_question_is_kept_and_settings_are_one_click_away()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);

        draft.Composer.Text = "问题";
        await draft.Composer.SendCommand.ExecuteAsync(null);

        Assert.Equal(SendRejection.NotConfigured, draft.Composer.Rejection);
        Assert.True(draft.Composer.ShowSettingsLink);
        Assert.Equal("问题", draft.Composer.Text);
        draft.OpenSettingsCommand.Execute(null);
        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Leaving_and_reopening_a_conversation_mid_answer_never_shows_older_text()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("第一段");
        var started = await app.Services.GetService<ConversationService>()!.StartAsync(app.Workbench.CurrentWorkspace!.Id, "问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(started.Conversation)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].Body == "第一段");
        Assert.NotNull(page.RunningAnswer);
        Assert.True(page.Composer.IsBlocked);

        app.Workbench.GoBack();
        held.After.Add("第二段");
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(started.Conversation)));
        var reopened = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await reopened.Loaded;
        Assert.Equal("第一段", reopened.Messages[1].Body);

        held.Release.TrySetResult();
        await UiWait.UntilAsync(() => reopened.Messages[^1].State == MessageState.Complete);
        Assert.Equal("第一段第二段", reopened.Messages[^1].Body);
        Assert.Null(reopened.RunningAnswer);
        Assert.False(reopened.Composer.IsBlocked);
    }

    [AvaloniaFact]
    public async Task Stop_keeps_received_text_and_offers_retry()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var page = await OpenConversationAsync(app, "问", app.Client.Hold("半句"));

        await page.StopCommand.ExecuteAsync(null);

        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].State == MessageState.Stopped);
        var answer = page.Messages[1];
        Assert.Equal("半句", answer.Body);
        Assert.Equal("已停止", answer.StateLabel);
        Assert.Equal(UiText.Retry, answer.RetryLabel);

        await page.RetryCommand.ExecuteAsync(answer);
        await UiWait.StoredAsync(page, 2, MessageState.Complete);
        Assert.True(page.Messages[1].IsSuperseded);
        Assert.False(page.Messages[1].CanRetry);
        Assert.True(page.Messages[2].CanRetry);
    }

    [AvaloniaFact]
    public async Task Restart_reopens_the_last_conversation()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var page = await OpenConversationAsync(app, "问", null);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].State == MessageState.Complete);

        await app.RestartAsync();

        var restored = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(page.Id, restored.Id);
        await restored.Loaded;
        Assert.Equal(2, restored.Messages.Count);
    }

    [AvaloniaFact]
    public async Task Quitting_with_a_running_answer_asks_first_and_cancel_leaves_it_running()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("进行中");
        var page = await OpenConversationAsync(app, "问", held);
        var exit = app.Root.Exit;

        Assert.True(exit.InterceptClose());
        Assert.True(exit.IsTaskPromptOpen);
        Assert.Contains("1 个回答", exit.TaskMessage, StringComparison.Ordinal);
        exit.KeepRunningCommand.Execute(null);
        Assert.Equal(ExitStage.Idle, exit.Stage);
        Assert.Equal(0, app.Shell.ExitRequests);
        Assert.NotNull(page.RunningAnswer);

        Assert.True(exit.InterceptClose());
        exit.StopAndExitCommand.Execute(null);
        await exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        Assert.False(exit.InterceptClose());
        await UiWait.UntilAsync(() => page.Messages[1].State == MessageState.Stopped);
        Assert.Equal("进行中", page.Messages[1].Body);
    }

    [AvaloniaFact]
    public async Task Quitting_with_nothing_running_does_not_ask()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");

        Assert.True(app.Root.Exit.InterceptClose());
        Assert.False(app.Root.Exit.IsTaskPromptOpen);
        await app.Root.Exit.Running;
        Assert.Equal(1, app.Shell.ExitRequests);
        Assert.False(app.Root.Exit.InterceptClose());
    }

    [AvaloniaFact]
    public async Task Model_settings_save_the_key_without_ever_showing_it_again()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var model = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).Model;
        await model.Loaded;

        model.BaseUrl = "http://api.example.com/v1";
        model.Model = "m";
        model.ApiKey = "sk-123";
        await model.SaveCommand.ExecuteAsync(null);
        Assert.True(model.StatusIsError);
        Assert.Equal("sk-123", model.ApiKey);

        model.BaseUrl = "https://api.example.com/v1";
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.StatusIsError);
        Assert.True(model.HasSavedKey);
        Assert.Equal(string.Empty, model.ApiKey);
        Assert.Equal("sk-123", Assert.Single(app.Credentials.Items).Value);

        await model.DiscoverCommand.ExecuteAsync(null);
        Assert.Equal(["fake-model"], model.AvailableModels);
        await model.TestCommand.ExecuteAsync(null);
        Assert.Equal(UiText.TestSucceeded, model.Status);
    }

    [AvaloniaFact]
    public async Task Typing_and_pressing_enter_in_the_real_composer_sends_shift_enter_does_not()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root };
        window.Show();
        await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        app.ShellPage.Conversations.NewCommand.Execute(null);
        window.UpdateLayout();

        var input = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "Input");
        input.Focus();
        window.KeyTextInput("第一行");
        window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        window.KeyTextInput("第二行");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1].State == MessageState.Complete);
        var page = (ConversationViewModel)app.Workbench.CurrentPage!;
        Assert.Contains("第一行", page.Messages[0].Body, StringComparison.Ordinal);
        Assert.Contains("第二行", page.Messages[0].Body, StringComparison.Ordinal);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 1);
        window.UpdateLayout();
        Assert.Single(window.GetVisualDescendants().OfType<ConversationView>());
        Screenshots.Capture(window, "03-conversation");
        window.Close();
    }

    private static async Task<ConversationViewModel> OpenConversationAsync(DesktopHarness app, string question, FakeChatClient.HeldReply? held)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        if (held is not null)
        {
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await UiWait.UntilAsync(() => page.Messages.Count == 2);
        if (held is not null)
        {
            await UiWait.UntilAsync(() => page.Messages[1].Body.Length > 0);
        }

        return page;
    }
}
