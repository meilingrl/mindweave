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

/// <summary>
/// A running answer shows the weaving indicator (docs/design/generating-indicator.md) in one status line: its motion and its
/// words say the stage (docs/design/reasoning-and-status.md), and no bar sits under the growing text. A reasoning model's
/// reasoning is in a fold at the top of the answer, closed at first, gone when the answer ends.
/// </summary>
public class StreamingIndicatorTests
{
    [AvaloniaFact]
    public async Task The_waiting_indicator_shows_until_the_first_text_and_never_under_streaming_text()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold();
        var page = await AskAsync(app, held);

        await UiWait.UntilAsync(() => VisibleIndicator(window) is not null);
        Assert.Equal(WeavingMotion.Braid, VisibleIndicator(window)!.Motion);
        Assert.True(page.Messages[1].IsWaiting);
        Assert.Equal(UiText.WaitingForAnswer, page.Messages[1].StatusText);
        Assert.Contains(VisibleTexts(window), t => t.Name == "StatusText" && t.Text == UiText.WaitingForAnswer);

        held.Release.TrySetResult();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Streaming_text_has_the_thread_beside_it_and_no_bar_under_it()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("第一句话。\n\n第二段，", "还在继续写。");
        var page = await AskAsync(app, held);

        await UiWait.UntilAsync(() => page.Messages[1].Body.Length > 0);
        window.UpdateLayout();
        Assert.False(page.Messages[1].IsWaiting);
        Assert.Equal(WeavingMotion.Thread, VisibleIndicator(window)!.Motion);
        Assert.Contains(VisibleTexts(window), t => t.Name == "StatusText" && t.Text == UiText.AnswerWriting);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ConversationView>().Single().GetVisualDescendants().OfType<ProgressBar>(), b => b.IsEffectivelyVisible);
        Assert.True(page.Messages[1].IsInProgress);

        held.Release.TrySetResult();
        window.Close();
    }

    [Theory]
    [InlineData(AnswerStage.Waiting, WeavingMotion.Braid)]
    [InlineData(AnswerStage.Compressing, WeavingMotion.Tightening)]
    [InlineData(AnswerStage.Thinking, WeavingMotion.Joining)]
    [InlineData(AnswerStage.Tool, WeavingMotion.Reaching)]
    [InlineData(AnswerStage.Approval, WeavingMotion.Holding)]
    [InlineData(AnswerStage.Retrying, WeavingMotion.Braid)]
    [InlineData(AnswerStage.Finishing, WeavingMotion.Braid)]
    [InlineData(AnswerStage.Writing, WeavingMotion.Thread)]
    public void Each_stage_has_its_motion(AnswerStage stage, WeavingMotion expected)
    {
        var motion = Converters.AnswerMotion.Convert(stage, typeof(WeavingMotion), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, motion);
    }

    [Fact]
    public void Each_stage_has_one_plain_sentence()
    {
        Assert.Equal("正在等待回答…", UiText.AnswerStatus(AnswerStage.Waiting));
        Assert.Equal("正在压缩较早的对话…", UiText.AnswerStatus(AnswerStage.Compressing));
        Assert.Equal("正在思考…", UiText.AnswerStatus(AnswerStage.Thinking));
        Assert.Equal("正在写回答…", UiText.AnswerStatus(AnswerStage.Writing));
        Assert.Equal("请求出错，正在重试…", UiText.AnswerStatus(AnswerStage.Retrying));
        Assert.Equal("正在根据已有结果作答…", UiText.AnswerStatus(AnswerStage.Finishing));
        Assert.Equal("正在搜索：蒸汽机 发明…", UiText.AnswerStatus(AnswerStage.Tool, "web_search", "网络搜索：蒸汽机 发明"));
        Assert.Equal("正在读取网页：zh.wikipedia.org…", UiText.AnswerStatus(AnswerStage.Tool, "fetch_page", "取网页：https://zh.wikipedia.org/wiki/蒸汽机"));
        Assert.Equal("正在检索附件：瓦特…", UiText.AnswerStatus(AnswerStage.Tool, "search_attachments", "检索附件：瓦特"));
        Assert.Equal("正在检索总结…", UiText.AnswerStatus(AnswerStage.Tool, "search_summaries", "检索总结"));
        Assert.Equal("正在调用 notes › echo…", UiText.AnswerStatus(AnswerStage.Tool, "mcp_notes__echo", "notes › echo：你好"));
        Assert.Equal("等待你批准读取网址…", UiText.AnswerStatus(AnswerStage.Approval, approval: new PendingApproval("c1", "fetch_page", "https://a.example/")));
        Assert.Equal("等待你批准调用 notes › write_note…",
            UiText.AnswerStatus(AnswerStage.Approval, approval: new PendingApproval("c1", "mcp_notes__write_note", "{}") { Server = "notes", ToolTitle = "write_note" }));
        var longQuery = new string('词', 60);
        Assert.Equal("正在搜索：" + new string('词', 40) + "……", UiText.AnswerStatus(AnswerStage.Tool, "web_search", "网络搜索：" + longQuery));
    }

    [AvaloniaFact]
    public async Task A_reasoning_model_shows_thinking_and_its_reasoning_in_a_closed_fold_until_the_answer_ends()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.HoldThinking();
        var page = await AskAsync(app, held);

        await UiWait.UntilAsync(() => page.Messages[1].StatusText == UiText.ModelThinking && page.Messages[1].Reasoning == string.Concat(FakeChatClient.ThinkingChunks));
        Assert.Equal(WeavingMotion.Joining, VisibleIndicator(window)!.Motion);
        Assert.Contains(VisibleTexts(window), t => t.Name == "StatusText" && t.Text == UiText.ModelThinking);
        Assert.Contains(VisibleTexts(window), t => t.Name == "StatusElapsed" && !string.IsNullOrEmpty(t.Text));

        // Closed at first: only its header shows.
        Assert.NotNull(Visible<Button>(window, "ReasoningToggle"));
        Assert.Null(Visible<SelectableTextBlock>(window, "ReasoningText"));
        Visible<Button>(window, "ReasoningToggle")!.Command!.Execute(null);
        Assert.Equal(string.Concat(FakeChatClient.ThinkingChunks), Visible<SelectableTextBlock>(window, "ReasoningText")!.Text);

        held.Finish("蒸汽机车");
        await UiWait.UntilAsync(() => page.Messages[1].Body == "蒸汽机车");
        await UiWait.StoredAsync(page, 1, MessageState.Complete);
        Assert.Null(VisibleIndicator(window));
        Assert.Null(Visible<Button>(window, "ReasoningToggle"));
        Assert.Null(Visible<SelectableTextBlock>(window, "ReasoningText"));
        Assert.DoesNotContain(VisibleTexts(window), t => t.Text == UiText.ModelThinking);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ConversationView>().Single().GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text?.Contains(FakeChatClient.ThinkingChunks[0], StringComparison.Ordinal) == true);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_answer_stopped_while_thinking_shows_it_stopped_with_retry_and_nothing_to_copy()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.HoldThinking();
        var page = await AskAsync(app, held);
        await UiWait.UntilAsync(() => page.Messages[1].StatusText == UiText.ModelThinking);

        await app.Services.GetRequiredService<ConversationService>().StopAsync(page.Messages[1].Id);

        await UiWait.StoredAsync(page, 1, MessageState.Stopped);
        Assert.Null(VisibleIndicator(window));
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

    private static T? Visible<T>(Window window, string name)
        where T : Control
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<ConversationView>().Single().GetVisualDescendants().OfType<T>()
            .SingleOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
    }

    private static WeavingIndicator? VisibleIndicator(Window window)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<ConversationView>().Single().GetVisualDescendants().OfType<WeavingIndicator>()
            .SingleOrDefault(i => i.Name == "GeneratingIndicator" && i.IsEffectivelyVisible);
    }
}
