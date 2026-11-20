using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Runs;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// One run through every stage in the real window, for the owner's review of the reasoning fold and the status line
/// (docs/design/reasoning-and-status.md): waiting, thinking with the fold closed and open, searching, waiting for approval,
/// reading a page, retrying, writing, the finished answer with the fold gone, and the last request after a limit.
/// Runs only when MINDWEAVE_SCREENSHOT_DIR is set (--filter Category=Gallery); MINDWEAVE_GALLERY_THEME=Dark for dark.
/// </summary>
[Trait("Category", "Gallery")]
public class ReasoningGalleryTests
{
    private const string Question = "瓦特对蒸汽机做了哪些关键改进？";

    private static readonly string[] Reasoning =
    [
        "用户问的是瓦特对蒸汽机的改进。我记得最关键的是分离式冷凝器：",
        "纽科门机每个冲程都要冷却整个汽缸，热量浪费很大。",
        "另外还有双作用、行星齿轮把往复运动变成旋转运动、离心调速器。\n\n",
        "年份我不太确定，分离式冷凝器的专利大概在 1769 年，最好搜索核实一下，再读一篇可靠的来源。",
    ];

    private static readonly string[] MoreReasoning =
    [
        "搜索结果里有一篇百科，正好讲分离式冷凝器和 1769 年的专利。",
        "另一个网址不在搜索结果里，需要用户批准才能读。",
    ];

    [AvaloniaFact]
    public async Task G41_reasoning_fold_and_status_line_through_a_run()
    {
        var target = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        var dark = Environment.GetEnvironmentVariable("MINDWEAVE_GALLERY_THEME") is "Dark";
        var suffix = dark ? "-dark" : string.Empty;
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, dark ? ThemeVariant.Dark : ThemeVariant.Light);

        var searching = new TaskCompletionSource();
        var reading = new TaskCompletionSource();
        app.Search.Slow(searching.Task);
        app.Pages.Slow("https://example.org/steam", reading.Task, "蒸汽机 - 百科",
            "瓦特在 1769 年取得分离式冷凝器的专利，冷凝在汽缸之外进行，汽缸保持高温，耗煤大约减少四分之三。");
        app.Pages.Page("https://history.example/watt-engine", "瓦特蒸汽机的改进", "1782 年瓦特取得双作用蒸汽机的专利；1788 年加上离心调速器。");

        var first = app.Client.HoldCalling([Call("c1", WebSearchTool.ToolName, new { query = "瓦特 蒸汽机 改进 分离式冷凝器" })]);
        var second = app.Client.HoldCalling(
        [
            Call("c2", FetchPageTool.ToolName, new { url = "https://example.org/steam" }),
            Call("c3", FetchPageTool.ToolName, new { url = "https://history.example/watt-engine" }),
        ]);
        app.Client.FailWithStatus(GenerationErrorKind.ServerError, 502);
        var last = app.Client.Hold();

        var page = await AskAsync(app, Question, first);
        var answer = page.Messages[1];

        // 1. Waiting: nothing has arrived yet.
        await CaptureAsync(window, $"reasoning-01-waiting{suffix}", () => answer.StatusText == UiText.WaitingForAnswer);

        // 2–3. Thinking: the fold is closed at first; opened, it follows the newest reasoning.
        first.Think(Reasoning);
        await CaptureAsync(window, $"reasoning-02-thinking-folded{suffix}", () => answer.Reasoning.EndsWith(Reasoning[^1], StringComparison.Ordinal));
        answer.ToggleReasoningCommand.Execute(null);
        await CaptureAsync(window, $"reasoning-03-thinking-expanded{suffix}", () => answer.IsReasoningExpanded);
        answer.ToggleReasoningCommand.Execute(null);

        // 4. Searching.
        first.Release.TrySetResult();
        await CaptureAsync(window, $"reasoning-04-searching{suffix}", () => answer.Stage == AnswerStage.Tool && answer.ActiveTool == WebSearchTool.ToolName);
        searching.TrySetResult();

        // 5. Thinking again in the next round; an address the model wrote itself waits for approval (asked before the calls run).
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        second.Think(MoreReasoning);
        second.Release.TrySetResult();
        await CaptureAsync(window, $"reasoning-05-approval{suffix}", () => answer.Stage == AnswerStage.Approval);
        answer.AllowCommand.Execute(null);

        // 6. Reading the pages: the approved one has been read, the one from the search results is still loading.
        await CaptureAsync(window, $"reasoning-06-reading-page{suffix}",
            () => answer.Stage == AnswerStage.Tool && answer.ActiveTool == FetchPageTool.ToolName && answer.ActiveCallId == "c2" && app.Pages.Fetched.Count == 2);
        reading.TrySetResult();

        // 7. The next request fails by chance and is tried once more.
        await CaptureAsync(window, $"reasoning-07-retrying{suffix}", () => answer.Stage == AnswerStage.Retrying, timeoutMs: 15_000);

        // 8. Writing: the retried request streams the answer; the fold stays, closed.
        await last.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        last.Think("材料够了，按时间顺序写：冷凝器、双作用、调速器。");
        last.Push("瓦特的关键改进有三项：\n\n1. **分离式冷凝器**（1769 年专利）[1]：冷凝在汽缸之外进行，汽缸保持高温，耗煤大约减少四分之三。\n2. **双作用**");
        await CaptureAsync(window, $"reasoning-08-writing{suffix}", () => answer.Stage == AnswerStage.Writing);

        // 9. Finished: the fold and the status line are gone; the steps and 出处 stay.
        last.Finish("（1782 年）[3]：活塞两面轮流受蒸汽推动。\n3. **离心调速器**（1788 年）[3]：自动保持转速稳定。");
        await UiWait.UntilAsync(() => page.Messages[1] is { State: MessageState.Complete, IsLive: false }, timeoutMs: 10_000);
        await CaptureAsync(window, $"reasoning-09-finished{suffix}", () => true);

        // 10. The last request after a limit: answering from what was found.
        await app.Services.GetRequiredService<IToolSettings>().SetLimitsAsync(RunLimits.Default with { ModelRequests = 2 }, CancellationToken.None);
        app.Client.CallTools(string.Empty, [Call("c9", WebSearchTool.ToolName, new { query = "瓦特 离心调速器" })]);
        var finishing = app.Client.Hold();
        page.Composer.Text = "离心调速器是他发明的吗？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await finishing.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await CaptureAsync(window, $"reasoning-10-finishing{suffix}", () => page.Messages.Count == 4 && page.Messages[3].Stage == AnswerStage.Finishing);
        finishing.Finish("不是。离心调速器早已用于风车和磨坊，瓦特把它用在了蒸汽机上。");
        await UiWait.UntilAsync(() => page.Messages[3] is { State: MessageState.Complete, IsLive: false }, timeoutMs: 10_000);
        window.Close();
    }

    private static ChatToolCall Call(string id, string tool, object arguments) => new(id, tool, JsonSerializer.Serialize(arguments));

    private static async Task CaptureAsync(Window window, string name, Func<bool> ready, int timeoutMs = 10_000)
    {
        await UiWait.UntilAsync(ready, timeoutMs: timeoutMs);
        await UiWait.AnswersRenderedAsync(window);
        await Task.Delay(120);
        window.UpdateLayout();
        window.GetVisualDescendants().OfType<ConversationView>().Single().GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(s => s.Name == "MessagesScroller")?.ScrollToEnd();
        await Task.Delay(80);
        window.UpdateLayout();
        Screenshots.Capture(window, name);
    }

    private static async Task<Window> StartAsync(DesktopHarness app, ThemeVariant theme)
    {
        var window = app.ShowWindow(1000, 760, theme);
        await app.StartWithWorkspaceAsync("火车历史");
        var capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true };
        await app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("deepseek", "https://fake.example/v1", "deepseek-flash", "sk-test", null, capabilities), CancellationToken.None);
        await app.Services.GetRequiredService<IWebSearchSettings>().SetKeyAsync("tvly-gallery-key", CancellationToken.None);
        window.UpdateLayout();
        return window;
    }

    private static async Task<ConversationViewModel> AskAsync(DesktopHarness app, string question, FakeChatClient.HeldReply held)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is NewConversationViewModel);
        var draft = (NewConversationViewModel)app.Workbench.CurrentPage!;
        await draft.Composer.WebSearch!.Loaded;
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 });
        return (ConversationViewModel)app.Workbench.CurrentPage!;
    }
}
