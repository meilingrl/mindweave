using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using TextBlock = Avalonia.Controls.TextBlock;
using Mindweave.Core.Models;
using Mindweave.Core.Runs;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Runs in the real window (requirements §10.4.4): the quiet activity line above an answer that used tools, its steps with
/// the result to open, the note when a limit was reached; 设置 › 工具 greyed out with its reason or in use; the 工具 section
/// of the statistics page. Screenshots (MINDWEAVE_SCREENSHOT_DIR) in light and dark.
/// </summary>
public class RunUiTests
{
    private const string Material = "第一段讲铁路的修建。\n\n第二段讲蒸汽机的发明：1769 年，瓦特改良了蒸汽机，使它能带动机器。\n\n第三段讲信号灯。";

    public static TheoryData<string> Themes => new() { "Light", "Dark" };

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task An_answer_that_searched_its_attachment_shows_one_quiet_line_that_expands_into_steps(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme, tools: true);
        app.Client.CallTool(SearchAttachmentsTool.ToolName, JsonSerializer.Serialize(new { query = "蒸汽机" }), "我先在附件里查一下。");
        app.Client.CallTool(SearchAttachmentsTool.ToolName, JsonSerializer.Serialize(new { query = "瓦特 年份" }));
        app.Client.Reply("根据附件，**1769 年**瓦特改良了蒸汽机，使它能带动机器。");

        var page = await AskWithAttachmentAsync(app, "瓦特是哪一年改良蒸汽机的？");
        var answer = page.Messages[1];
        Assert.True(answer.HasActivity);
        Assert.Equal("检索了 2 次附件", answer.ActivityText);
        Assert.Equal("我先在附件里查一下。\n\n根据附件，**1769 年**瓦特改良了蒸汽机，使它能带动机器。", answer.Body);
        Assert.False(answer.IsActivityExpanded);
        Assert.True(AnyVisible<StackPanel>(window, "RunActivityPanel"));
        window.UpdateLayout();
        Screenshots.Capture(window, $"runs-01-activity-line-{theme.ToLowerInvariant()}");

        answer.ToggleActivityCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal(2, answer.ActivitySteps.Count);
        var step = answer.ActivitySteps[0];
        Assert.Equal("检索附件：蒸汽机", step.Title);
        Assert.Equal("找到 1 段", step.Outcome);
        Assert.EndsWith(UiText.RunStepDone, step.Meta, StringComparison.Ordinal);
        Assert.Equal(UiText.OpenToolResult, Assert.Single(step.Links).Label);
        Assert.True(AnyVisible<ItemsControl>(window, "RunSteps"));
        Screenshots.Capture(window, $"runs-02-steps-{theme.ToLowerInvariant()}");

        // The stored result opens in the viewer, selectable like an attachment.
        step.Links[0].Open.Execute(null);
        await UiWait.UntilAsync(() => page.AttachmentViewer.IsOpen && page.AttachmentViewer.Text is not null);
        Assert.Contains("瓦特改良了蒸汽机", page.AttachmentViewer.Text, StringComparison.Ordinal);
        Assert.Equal(UiText.ToolResultType, page.AttachmentViewer.Info.Split(' ')[0]);
        page.AttachmentViewer.CloseCommand.Execute(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_run_that_reached_its_limit_says_so_under_the_answer()
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, "Light", tools: true);
        await app.Services.GetRequiredService<IToolSettings>().SetLimitsAsync(RunLimits.Default with { ModelRequests = 2 }, CancellationToken.None);
        app.Client.KeepCalling = (SearchAttachmentsTool.ToolName, JsonSerializer.Serialize(new { query = "蒸汽机" }));

        var page = await AskWithAttachmentAsync(app, "蒸汽机？");
        var answer = page.Messages[1];
        await UiWait.UntilAsync(() => answer.HasLimitNote);
        Assert.Equal(UiText.RunLimitNote(RunLimit.ModelRequests), answer.LimitNote);
        window.UpdateLayout();
        Assert.True(AnyVisible<TextBlock>(window, "RunLimitNote"));
        Screenshots.Capture(window, "runs-03-limit-note-light");
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_answer_without_tools_shows_no_activity_line()
    {
        await using var app = DesktopHarness.Create();
        await StartAsync(app, "Light", tools: false);
        var page = await AskWithAttachmentAsync(app, "问");
        Assert.False(page.Messages[1].HasActivity);
        Assert.Null(page.Messages[1].LimitNote);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Tool_settings_are_greyed_out_with_the_reason_until_the_model_declares_tool_calls(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme, tools: false);
        var (settings, panel) = await OpenToolSettingsAsync(app, window);

        Assert.False(panel.IsAvailable);
        Assert.True(Find<TextBlock>(window, "ToolsUnavailable").IsVisible);
        Assert.Equal(UiText.ToolsUnavailable, Find<TextBlock>(window, "ToolsUnavailable").Text);
        Assert.False(Find<CheckBox>(window, "UseToolsToggle").IsEffectivelyEnabled);
        Screenshots.Capture(window, $"runs-04-tool-settings-unavailable-{theme.ToLowerInvariant()}");

        // Declaring tool calls in 设置 › 模型连接 › 高级设置 makes the section usable.
        settings.Model.DeclaresToolCalls = true;
        await settings.Model.SaveCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => panel.IsAvailable);
        window.UpdateLayout();
        Assert.False(Find<TextBlock>(window, "ToolsUnavailable").IsVisible);
        Assert.True(Find<CheckBox>(window, "UseToolsToggle").IsEffectivelyEnabled);
        Find<StackPanel>(window, "ToolsSection").BringIntoView();
        window.UpdateLayout();
        Screenshots.Capture(window, $"runs-05-tool-settings-{theme.ToLowerInvariant()}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Changing_tool_settings_is_kept_for_later_questions()
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, "Light", tools: true);
        var (_, panel) = await OpenToolSettingsAsync(app, window);
        var stored = app.Services.GetRequiredService<IToolSettings>();

        panel.ModelRequests = 4;
        await UiWait.UntilAsync(() => panel.Status is not null);
        Assert.Equal(4, (await stored.GetLimitsAsync(CancellationToken.None)).ModelRequests);
        Assert.Equal(UiText.LimitsSaved, panel.Status);
        panel.UsesTools = false;
        Assert.False(await stored.GetEnabledAsync(CancellationToken.None));
        window.UpdateLayout();
        Assert.False(Find<NumericUpDown>(window, "LimitModelRequests").IsEffectivelyEnabled);

        panel.UsesTools = true;
        await panel.RestoreDefaultsCommand.ExecuteAsync(null);
        Assert.Equal(RunLimits.Default, await stored.GetLimitsAsync(CancellationToken.None));
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task The_statistics_page_counts_runs_and_each_tools_calls(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme, tools: true);
        app.Client.CallTool(SearchAttachmentsTool.ToolName, JsonSerializer.Serialize(new { query = "蒸汽机" }));
        app.Client.Reply("好。");
        await AskWithAttachmentAsync(app, "问");

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).Loaded;
        window.UpdateLayout();
        UiInput.Click(window, Find<Button>(window, "StatisticsButton"));
        var stats = Assert.IsType<UsageStatisticsViewModel>(app.Workbench.CurrentPage);
        await stats.Loaded;
        await UiWait.UntilAsync(() => stats.HasToolUsage);
        Assert.Equal(UiText.ToolRuns(1, 2), stats.ToolRunsText);
        Assert.Equal(new ToolCallRowViewModel(UiText.SearchAttachmentsName, UiText.ToolCallCount(1)), Assert.Single(stats.ToolCalls));
        window.UpdateLayout();
        Find<StackPanel>(window, "ToolUsageSection").BringIntoView();
        window.UpdateLayout();
        Screenshots.Capture(window, $"runs-06-statistics-{theme.ToLowerInvariant()}");
        window.Close();
    }

    private static bool AnyVisible<T>(MainWindow window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    private static async Task<MainWindow> StartAsync(DesktopHarness app, string theme, bool tools)
    {
        var window = app.ShowWindow(1000, 720, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        await app.StartWithWorkspaceAsync("火车历史");
        var capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = tools };
        await app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", null, capabilities), CancellationToken.None);
        window.UpdateLayout();
        return window;
    }

    private static async Task<ConversationViewModel> AskWithAttachmentAsync(DesktopHarness app, string question)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is NewConversationViewModel { Composer.CanAttach: true });
        var composer = ((NewConversationViewModel)app.Workbench.CurrentPage!).Composer;
        Assert.True(composer.TryTakePaste(Material + new string('史', 3_000), 0));
        await UiWait.UntilAsync(() => composer.Attachments is [{ IsReady: true }]);
        composer.Text = question;
        await composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1].State == MessageState.Complete && !p.Messages[1].IsLive, timeoutMs: 10_000);
        var page = (ConversationViewModel)app.Workbench.CurrentPage!;
        await Task.Delay(100);
        return page;
    }

    private static async Task<(SettingsHomeViewModel Settings, ToolSettingsPanel Panel)> OpenToolSettingsAsync(DesktopHarness app, MainWindow window)
    {
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        await settings.Model.Loaded;
        var panel = settings.Tools!;
        await panel.Loaded;
        window.UpdateLayout();
        Find<StackPanel>(window, "ToolsSection").BringIntoView();
        window.UpdateLayout();
        return (settings, panel);
    }
}
