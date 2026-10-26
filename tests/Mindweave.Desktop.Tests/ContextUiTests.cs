using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>S3 as the reader sees it: a light note when older context was summarized, clear refusals, the setting.</summary>
public class ContextUiTests
{
    private static readonly string LongAnswer = new('史', 1_000);

    [AvaloniaFact]
    public async Task An_answer_sent_with_a_summary_carries_a_light_note()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await SmallModelAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        app.Client.Reply(LongAnswer);
        var started = await conversations.StartAsync(app.Workbench.CurrentWorkspace!.Id, "问题 0", CancellationToken.None);
        await AnsweredAsync(conversations, started.Conversation, 2);
        for (var i = 1; i < 6; i++)
        {
            app.Client.Reply(LongAnswer);
            await conversations.AskAsync(started.Conversation, $"问题 {i}", CancellationToken.None);
            await AnsweredAsync(conversations, started.Conversation, (i + 1) * 2);
        }

        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(started.Conversation)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        page.Composer.Text = "最后的问题" + new string('补', 800);
        await page.Composer.SendCommand.ExecuteAsync(null);

        await UiWait.UntilAsync(() => page.Messages.Count == 14 && page.Messages[^1].State == MessageState.Complete && page.Messages[^1].WasCompressed);
        Assert.False(page.Messages[1].WasCompressed);
        Assert.Equal(1, app.Client.Requests.Count(FakeChatClient.IsSummary));
    }

    [AvaloniaFact]
    public async Task A_question_too_large_for_the_model_is_refused_with_a_way_to_settings()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await SmallModelAsync(app);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        var question = new string('长', 7_000);

        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);

        Assert.Equal(SendRejection.QuestionTooLargeForModel, draft.Composer.Rejection);
        Assert.Equal(UiText.SendRejected(SendRejection.QuestionTooLargeForModel), draft.Composer.Notice);
        Assert.True(draft.Composer.ShowSettingsLink);
        Assert.Equal(question, draft.Composer.Text);
        Assert.Empty(app.ShellPage.Conversations.Items);
    }

    [AvaloniaFact]
    public async Task The_context_capacity_is_set_in_settings_and_survives_reopening()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var model = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).Model;
        await model.Loaded;
        model.BaseUrl = "https://api.example.com/v1";
        model.Model = "m";
        model.ApiKey = "sk-123";

        model.ContextWindow = "很多";
        await model.SaveCommand.ExecuteAsync(null);
        Assert.True(model.StatusIsError);
        Assert.Equal(UiText.SettingsProblem(ModelSettingsProblem.InvalidContextWindow), model.Status);

        model.ContextWindow = "128,000";
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.StatusIsError);

        var reopened = new ModelSettingsViewModel(app.Services.GetRequiredService<ModelSettingsService>());
        await reopened.Loaded;
        Assert.Equal("128000", reopened.ContextWindow);
    }

    private static Task SmallModelAsync(DesktopHarness app) =>
        app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", ContextWindowTokens: 12_000), CancellationToken.None);

    private static Task AnsweredAsync(ConversationService conversations, ConversationId id, int count) =>
        UiWait.UntilAsync(() => conversations.LoadAsync(id, CancellationToken.None).Result is { } c && c.Messages.Count == count && c.Messages[^1].IsFinished);
}
