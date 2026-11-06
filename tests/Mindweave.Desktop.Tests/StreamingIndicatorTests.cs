using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using MessageState = Mindweave.Core.Conversations.MessageState;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>A running answer shows a waiting indicator only until its first text; growing text is its own progress.</summary>
public class StreamingIndicatorTests
{
    [AvaloniaFact]
    public async Task The_waiting_indicator_shows_until_the_first_text_and_never_under_streaming_text()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold();
        var page = await AskAsync(app, held);

        await UiWait.UntilAsync(() => VisibleWaitingBars(window) == 1);
        Assert.True(page.Messages[1].IsWaiting);
        Assert.Equal(UiText.WaitingForAnswer, page.Messages[1].WaitingLabel);

        held.Release.TrySetResult();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Streaming_text_has_no_progress_bar_under_it()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("第一句话。\n\n第二段，", "还在继续写。");
        var page = await AskAsync(app, held);

        await UiWait.UntilAsync(() => page.Messages[1].Body.Length > 0);
        window.UpdateLayout();
        Assert.False(page.Messages[1].IsWaiting);
        Assert.Equal(0, VisibleWaitingBars(window));
        Assert.True(page.Messages[1].IsInProgress);

        held.Release.TrySetResult();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_reasoning_model_shows_thinking_with_its_timer_until_the_text_arrives()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.HoldThinking();
        var page = await AskAsync(app, held);

        await UiWait.UntilAsync(() => page.Messages[1].WaitingLabel == UiText.ModelThinking);
        Assert.Equal(1, VisibleWaitingBars(window));
        Assert.Contains(VisibleTexts(window), t => t.Text == UiText.ModelThinking);
        Assert.Contains(VisibleTexts(window), t => t.Name == "WaitingElapsed" && !string.IsNullOrEmpty(t.Text));

        held.Finish("蒸汽机车");
        await UiWait.UntilAsync(() => page.Messages[1].Body == "蒸汽机车");
        Assert.Equal(0, VisibleWaitingBars(window));
        Assert.DoesNotContain(VisibleTexts(window), t => t.Text == UiText.ModelThinking);
        await UiWait.StoredAsync(page, 1, MessageState.Complete);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_answer_stopped_while_thinking_shows_it_stopped_with_retry_and_nothing_to_copy()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.HoldThinking();
        var page = await AskAsync(app, held);
        await UiWait.UntilAsync(() => page.Messages[1].WaitingLabel == UiText.ModelThinking);

        await app.Services.GetRequiredService<ConversationService>().StopAsync(page.Messages[1].Id);

        await UiWait.StoredAsync(page, 1, MessageState.Stopped);
        Assert.Equal(0, VisibleWaitingBars(window));
        var texts = VisibleTexts(window);
        Assert.Contains(texts, t => t.Name == "StateText" && t.Text == "已停止");
        Assert.Contains(texts, t => t.Name == "AnswerDuration");
        var view = window.GetVisualDescendants().OfType<ConversationView>().Single();
        Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "RetryButton" && b.IsEffectivelyVisible && b.IsEffectivelyEnabled);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<CopyButton>(), b => b.Name == "AnswerCopy" && b.IsEffectivelyVisible && b.IsEffectivelyEnabled);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Name == "ErrorNotice" && b.IsEffectivelyVisible);
        window.Close();
    }

    private static List<TextBlock> VisibleTexts(Window window)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<ConversationView>().Single().GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible).ToList();
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

    private static int VisibleWaitingBars(Window window)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<ConversationView>().Single().GetVisualDescendants().OfType<ProgressBar>()
            .Count(b => b.Name == "WaitingBar" && b.IsEffectivelyVisible);
    }
}
