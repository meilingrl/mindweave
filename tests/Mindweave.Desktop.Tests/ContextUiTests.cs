using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
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

        // The context line says the earlier part now goes as a summary, and how large it all is.
        await UiWait.UntilAsync(() => page.ContextUsage is { Compressed: true });
        Assert.True(page.ContextIsTight);
        Assert.Contains("以摘要发送", page.ContextUsageText, StringComparison.Ordinal);

        // A larger capacity in settings updates the open page.
        await app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", null, ContextWindowTokens: 128_000), CancellationToken.None);
        await UiWait.UntilAsync(() => page.ContextUsage is { Compressed: false, Budget: > 100_000 });
        Assert.False(page.ContextIsTight);
        Assert.StartsWith("上下文约", page.ContextUsageText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task While_older_turns_are_summarized_the_answer_says_so_then_thinks_then_streams()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
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

        // A reasoning model: its summary request reasons too, which must not hide the compression notice.
        app.Client.ReasonFirst = true;
        app.Client.ContextSummaries.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = app.Client.HoldThinking();
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(started.Conversation)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        page.Composer.Text = "最后的问题" + new string('补', 800);
        await page.Composer.SendCommand.ExecuteAsync(null);

        await UiWait.UntilAsync(() => page.Messages.Count == 14 && page.Messages[^1].WaitingLabel == UiText.CompressingContext && WaitingCaption(window) == UiText.CompressingContext);
        Assert.True(page.Messages[^1].IsWaiting);
        Assert.Equal(Views.WeavingMotion.Tightening, IndicatorMotion(window));

        app.Client.ContextSummaries.Gate.TrySetResult();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => WaitingCaption(window) == UiText.ModelThinking);
        Assert.Equal(Views.WeavingMotion.Joining, IndicatorMotion(window));

        held.Finish("回答");
        await UiWait.UntilAsync(() => page.Messages[^1].Body == "回答" && WaitingCaption(window) is null);
        await UiWait.StoredAsync(page, 13, MessageState.Complete);
        Assert.Null(IndicatorMotion(window));
        window.Close();
    }

    /// <summary>The caption beside the visible waiting bar, or null when no answer is waiting.</summary>
    private static string? WaitingCaption(Window window)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<StackPanel>()
            .Where(p => p.Name == "WaitingText" && p.IsEffectivelyVisible)
            .Select(p => p.Children.OfType<Avalonia.Controls.TextBlock>().First().Text)
            .SingleOrDefault();
    }

    /// <summary>The weaving indicator's motion beside the answer being generated (docs/design/generating-indicator.md).</summary>
    private static Views.WeavingMotion? IndicatorMotion(Window window)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<Views.WeavingIndicator>()
            .Where(i => i.Name == "GeneratingIndicator" && i.IsEffectivelyVisible)
            .Select(i => (Views.WeavingMotion?)i.Motion)
            .SingleOrDefault();
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
