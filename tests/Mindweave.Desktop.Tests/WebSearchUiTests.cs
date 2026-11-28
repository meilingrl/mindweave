using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Runs;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Testing;
using TextBlock = Avalonia.Controls.TextBlock;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Web search in the real window (requirements §10.4.5): the composer's switch (greyed out with the reason, the three
/// positions), an answer's 出处 as links and as a list, the viewer at the cited passage with the address shown before the
/// browser opens it, the approval card for an invented address, 设置 › 工具 › 网络搜索, and the statistics rows. Screenshots
/// (MINDWEAVE_SCREENSHOT_DIR) in light and dark.
/// </summary>
public class WebSearchUiTests
{
    private const string Key = "tvly-ui-test-key";

    public static TheoryData<string> Themes => new() { "Light", "Dark" };

    private static string Query(string q) => JsonSerializer.Serialize(new { query = q });

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task The_switch_is_greyed_out_with_its_reason_until_a_key_is_set(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme, key: false);
        var page = await OpenNewAsync(app);
        var web = page.Composer.WebSearch!;
        await web.Loaded;

        Assert.False(web.IsAvailable);
        Assert.Equal(UiText.WebSearchNoKey, web.Tip);
        Assert.True(web.ShowsOff);
        window.UpdateLayout();
        var button = Find<Button>(window, "WebSearchSwitchButton");
        Assert.True(button.IsEffectivelyVisible);
        Assert.False(button.IsEffectivelyEnabled);
        Assert.Equal(UiText.WebSearchNoKey, Avalonia.Automation.AutomationProperties.GetName(button));
        Assert.Equal(UiText.WebSearchNoKey, ToolTip.GetTip(button));

        // Only the globe, the size of "+".
        Assert.Empty(button.GetVisualDescendants().OfType<TextBlock>());
        Assert.Equal(Find<Button>(window, "AttachButton").Bounds.Size, button.Bounds.Size);
        Screenshots.Capture(window, $"web-01-switch-no-key-{theme.ToLowerInvariant()}");

        await app.Services.GetRequiredService<IWebSearchSettings>().SetKeyAsync(Key, CancellationToken.None);
        await UiWait.UntilAsync(() => web.IsAvailable);
        window.UpdateLayout();
        Assert.Equal("网络搜索：自动（由模型决定是否搜索）", Avalonia.Automation.AutomationProperties.GetName(button));
        Assert.DoesNotContain("on", button.Classes);
        Assert.DoesNotContain("off", button.Classes);
        Screenshots.Capture(window, $"web-02a-switch-auto-{theme.ToLowerInvariant()}");
        web.ChooseCommand.Execute(WebSearchMode.Off);
        window.UpdateLayout();
        Assert.Contains("off", button.Classes);
        Assert.Equal(UiText.WebSearchModeTip(WebSearchMode.Off), ToolTip.GetTip(button));
        Screenshots.Capture(window, $"web-02b-switch-off-{theme.ToLowerInvariant()}");
        web.ChooseCommand.Execute(WebSearchMode.On);
        window.UpdateLayout();
        Assert.True(button.IsEffectivelyEnabled);
        Assert.Contains("on", button.Classes);
        Assert.Equal(UiText.WebSearchModeTip(WebSearchMode.On), ToolTip.GetTip(button));
        Assert.Equal(new Application.Conversations.SendOptions(WebSearchMode.On), page.Composer.Options);
        Screenshots.Capture(window, $"web-02-switch-on-{theme.ToLowerInvariant()}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Without_tool_calls_the_switch_says_the_model_cannot_call_tools()
    {
        await using var app = DesktopHarness.Create();
        await StartAsync(app, "Light", key: true, tools: false);
        var page = await OpenNewAsync(app);
        await page.Composer.WebSearch!.Loaded;
        Assert.False(page.Composer.WebSearch.IsAvailable);
        Assert.Equal(UiText.ToolsUnavailable, page.Composer.WebSearch.Tip);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task An_answer_with_sources_shows_its_citations_as_links_and_a_list_that_opens_the_passage(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme, key: true);
        app.Pages.Page(
            "https://example.org/steam", "蒸汽机 - 百科",
            "蒸汽机是将蒸汽的能量转换为机械功的往复式动力机械。\n\n纽科门在 1712 年制造了第一台实用的蒸汽机。\n\n1769 年，瓦特取得了分离冷凝器的专利，大大提高了效率。");
        app.Client.CallTool(WebSearchTool.ToolName, Query("瓦特 蒸汽机"), "我先搜索一下。");
        app.Client.CallTool(FetchPageTool.ToolName, JsonSerializer.Serialize(new { url = "https://example.org/steam" }));
        app.Client.Reply("1769 年，瓦特取得了分离冷凝器的专利[1]。第一条公共铁路在 1825 年通车[2]。这一点没有来源[7]。");

        var page = await AskAsync(app, "瓦特对蒸汽机做了什么？");
        var answer = page.Messages[1];

        Assert.Equal("搜索了 1 次 · 读了 1 个网页", answer.ActivityText);
        Assert.Equal([1, 2], answer.Citations.Select(c => c.Block.Number));
        Assert.Equal(("[1]", "蒸汽机 - 百科", "https://example.org/steam"), (answer.Citations[0].Number, answer.Citations[0].Title, answer.Citations[0].Address));
        window.UpdateLayout();
        Assert.True(AnyVisible<StackPanel>(window, "CitationsPanel"));

        // Closed at first, as one line with the number of sources; the steps above the text too.
        Assert.False(answer.IsCitationsExpanded);
        Assert.False(answer.IsActivityExpanded);
        Assert.Equal("出处 · 2", answer.CitationsText);
        Assert.False(AnyVisible<ItemsControl>(window, "CitationList"));
        Screenshots.Capture(window, $"web-03a-citations-closed-{theme.ToLowerInvariant()}");
        UiInput.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CitationsToggle" && b.IsEffectivelyVisible));
        window.UpdateLayout();
        Assert.True(AnyVisible<ItemsControl>(window, "CitationList"));
        Assert.Equal(UiText.CitationsCollapse, answer.CitationsToggleLabel);

        // In the text, [1] and [2] are links; [7] stays text. The shown text is the stored text.
        var renderer = window.GetVisualDescendants().OfType<MarkdownRenderer>().Last(r => r.DocumentUpdate is not null);
        var document = renderer.DocumentUpdate!.Document;
        var links = Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.Inlines.LinkInline>(document)
            .Select(l => CitationLinks.NumberOf(new Uri(l.Url!))).ToList();
        Assert.Equal([1, 2], links);
        Assert.Equal(answer.Body.Replace("\n\n", string.Empty, StringComparison.Ordinal), string.Concat(Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.Inlines.LiteralInline>(document).Select(l => l.Content.ToString())));
        Screenshots.Capture(window, $"web-03-citations-{theme.ToLowerInvariant()}");

        // A 出处 opens the stored page at the cited passage; the address is shown in full before anything opens.
        Assert.True(answer.OpenCitation(1));
        var viewer = page.AttachmentViewer;
        await UiWait.UntilAsync(() => viewer.IsOpen && viewer.Highlight is not null);
        Assert.Equal("蒸汽机 - 百科", viewer.Title);
        Assert.Contains("分离冷凝器", viewer.Text![viewer.Highlight!.Value.Start..viewer.Highlight.Value.End], StringComparison.Ordinal);
        Assert.Equal("https://example.org/steam", viewer.SourceAddress);
        Assert.False(answer.OpenCitation(7));
        var opened = new List<Uri>();
        viewer.OpenRequested += (_, url) => opened.Add(url);
        viewer.AskToOpenCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(viewer.IsConfirmingOpen);
        Assert.Empty(opened);
        Assert.Equal("https://example.org/steam", Find<SelectableTextBlock>(window, "ViewerConfirmAddress").Text);
        Screenshots.Capture(window, $"web-04-viewer-passage-{theme.ToLowerInvariant()}");
        viewer.ConfirmOpenCommand.Execute(null);
        Assert.Equal([new Uri("https://example.org/steam")], opened);
        viewer.CloseCommand.Execute(null);

        // The steps list the search results by number.
        answer.ToggleActivityCommand.Execute(null);
        Assert.Contains(answer.ActivitySteps[0].Links, l => l.Label == "[1] 蒸汽机 - 百科");
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task A_cited_attachment_is_in_the_list_with_the_web_sources_and_opens_at_its_part(string theme)
    {
        // docs/design/agent-loop.md L11: local results share the run's numbers; their rows name the material, without an address.
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme, key: true);
        app.Client.CallTool(WebSearchTool.ToolName, Query("瓦特 蒸汽机"));
        app.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        app.Client.Reply("1769 年，瓦特改良了蒸汽机[3]。第一条公共铁路在 1825 年通车[2]。");

        app.ShellPage.Conversations.NewCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is NewConversationViewModel { Composer.CanAttach: true });
        var composer = ((NewConversationViewModel)app.Workbench.CurrentPage!).Composer;
        await composer.WebSearch!.Loaded;
        Assert.True(composer.TryTakePaste("第一段讲铁路的修建。\n\n第二段讲蒸汽机的发明：1769 年，瓦特改良了蒸汽机，使它能带动机器。\n\n第三段讲信号灯。" + new string('史', 3_000), 0));
        await UiWait.UntilAsync(() => composer.Attachments is [{ IsReady: true }]);
        composer.Text = "瓦特和铁路各在哪一年？";
        await composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1] is { State: MessageState.Complete, IsLive: false }, timeoutMs: 10_000);
        var page = (ConversationViewModel)app.Workbench.CurrentPage!;
        await Task.Delay(150);
        var answer = page.Messages[1];

        Assert.Equal([2, 3], answer.Citations.Select(c => c.Block.Number));
        var local = answer.Citations[1];
        Assert.Equal(("[3]", $"附件《{UiText.PastedTextName}》", "本会话的附件", "本会话的附件", false), (local.Number, local.Title, local.Address, local.Meta, local.HasAddress));
        Assert.True(answer.Citations[0].HasAddress);
        window.UpdateLayout();
        Screenshots.Capture(window, $"loop-l11-citations-{theme.ToLowerInvariant()}");

        Assert.True(answer.OpenCitation(3));
        var viewer = page.AttachmentViewer;
        await UiWait.UntilAsync(() => viewer.IsOpen && viewer.Highlight is not null);
        Assert.Equal(($"附件《{UiText.PastedTextName}》", "出处 [3]", null), (viewer.Title, viewer.Info, viewer.SourceAddress));
        Assert.Contains("瓦特改良了蒸汽机", viewer.Text![viewer.Highlight!.Value.Start..viewer.Highlight.Value.End], StringComparison.Ordinal);
        window.UpdateLayout();
        Screenshots.Capture(window, $"loop-l11-viewer-{theme.ToLowerInvariant()}");
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task An_invented_address_shows_an_approval_card_and_allowing_reads_it(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme, key: true);
        app.Pages.Page("https://unknown.example/report?id=42", "报告", "报告正文：结论是可行的。");
        app.Client.CallTool(FetchPageTool.ToolName, JsonSerializer.Serialize(new { url = "https://unknown.example/report?id=42" }), "我想读一下这份报告。");

        var composer = (await OpenNewAsync(app)).Composer;
        composer.Text = "那份报告的结论是什么？";
        await composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1].HasApproval, timeoutMs: 10_000);
        var page = (ConversationViewModel)app.Workbench.CurrentPage!;
        var answer = page.Messages[1];
        Assert.Equal(UiText.ApprovalWaiting, answer.StatusText);
        Assert.Equal(Views.WeavingMotion.Holding, Converters.AnswerMotion.Convert(answer.Stage, typeof(Views.WeavingMotion), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotContain("等待", answer.ActivityText, StringComparison.Ordinal);
        Assert.Equal("https://unknown.example/report?id=42", answer.PendingApproval!.Target);

        // The card is bound to the answer's state and shows at once; the streamed text above it (我想读一下这份报告。) is
        // parsed in the background and lands a moment later, a line higher up. Landing between the press and the release
        // below, it moved the card down and the click was lost.
        await UiWait.AnswersRenderedAsync(window);
        window.UpdateLayout();
        Assert.True(AnyVisible<Border>(window, "ApprovalCard"));
        Assert.Empty(app.Pages.Fetched);
        Screenshots.Capture(window, $"web-05-approval-card-{theme.ToLowerInvariant()}");

        app.Client.Reply("报告的结论是可行的[1]。");
        UiInput.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ApprovalAllow" && b.IsEffectivelyVisible));
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1] is { State: MessageState.Complete, IsLive: false }, timeoutMs: 10_000);
        Assert.Single(app.Pages.Fetched);
        Assert.Equal("报告", Assert.Single(((ConversationViewModel)app.Workbench.CurrentPage!).Messages[1].Citations).Title);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Web_search_settings_keep_the_key_hidden_test_it_and_set_the_default(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme, key: false);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        var web = settings.Tools!.Web!;
        await settings.Tools.Loaded;
        await web.Loaded;
        window.UpdateLayout();
        Find<StackPanel>(window, "WebSearchSection").BringIntoView();
        window.UpdateLayout();

        Assert.True(web.IsAvailable);
        Assert.Equal(("Tavily", UiText.WebSearchKeyNotSet), (web.ServiceName, web.KeyState));
        web.NewKey = Key;
        await web.TestCommand.ExecuteAsync(null);
        Assert.Equal((UiText.WebSearchTestOk, false), (web.Status, web.StatusIsError));
        Assert.Equal(Key, app.Search.Searches.Last().Key);
        await web.SaveKeyCommand.ExecuteAsync(null);
        Assert.Equal((string.Empty, UiText.WebSearchKeySet), (web.NewKey, web.KeyState));
        Assert.Equal(Key, await app.Services.GetRequiredService<IWebSearchSettings>().ReadKeyAsync(CancellationToken.None));

        // The choice saves in the background and says so when stored; the test's earlier status is already set, so wait for this one.
        web.IsOn = true;
        await UiWait.UntilAsync(() => web.Status == UiText.LimitsSaved && web.DefaultMode == WebSearchMode.On);
        Assert.Equal(WebSearchMode.On, await app.Services.GetRequiredService<IWebSearchSettings>().GetDefaultModeAsync(CancellationToken.None));
        app.Search.Fails(SearchFailureKind.QuotaExhausted, "搜索服务的额度已用完。");
        await web.TestCommand.ExecuteAsync(null);
        Assert.Equal(("搜索服务的额度已用完。", true), (web.Failure?.Headline, web.StatusIsError)); // The failure block (D2 §2.1).
        Assert.Null(web.Status);
        Assert.Equal(UiText.SearchFailureAdvice(Application.Tools.SearchFailureKind.QuotaExhausted), web.Failure!.Advice);
        window.UpdateLayout();
        Find<StackPanel>(window, "WebSearchSection").BringIntoView();
        window.UpdateLayout();
        Screenshots.Capture(window, $"web-06-settings-{theme.ToLowerInvariant()}");

        // Tools turned off: the section is greyed out with 工具, the key kept.
        Assert.False(Find<TextBlock>(window, "WebSearchOffReason").IsEffectivelyVisible);
        Assert.False(Find<TextBlock>(window, "McpOffReason").IsEffectivelyVisible);
        settings.Tools.UsesTools = false;
        window.UpdateLayout();
        Assert.False(web.IsAvailable);
        Assert.True(web.HasKey);

        // … with the reason (requirements §10.4.5); so are the MCP servers.
        foreach (var name in new[] { "WebSearchOffReason", "McpOffReason" })
        {
            var reason = Find<TextBlock>(window, name);
            Assert.True(reason.IsEffectivelyVisible);
            Assert.Equal(UiText.WebSearchToolsOff, reason.Text);
        }

        Screenshots.Capture(window, $"web-07-settings-tools-off-{theme.ToLowerInvariant()}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_statistics_page_names_the_web_tools()
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, "Light", key: true);
        app.Client.CallTool(WebSearchTool.ToolName, Query("一"));
        app.Client.Reply("好[1]。");
        await AskAsync(app, "问");

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).Loaded;
        window.UpdateLayout();
        await SettingsInput.OpenAsync(window, "StatisticsButton");
        var stats = Assert.IsType<UsageStatisticsViewModel>(app.Workbench.CurrentPage);
        await stats.Loaded;
        await UiWait.UntilAsync(() => stats.HasToolUsage);
        Assert.Contains(new ToolCallRowViewModel(UiText.WebSearchName, UiText.ToolCallCount(1)), stats.ToolCalls);
        window.Close();
    }

    private static bool AnyVisible<T>(MainWindow window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    private static async Task<MainWindow> StartAsync(DesktopHarness app, string theme, bool key, bool tools = true)
    {
        var window = app.ShowWindow(1000, 760, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        await app.StartWithWorkspaceAsync("火车历史");
        var capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = tools };
        await app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", null, capabilities), CancellationToken.None);
        if (key)
        {
            await app.Services.GetRequiredService<IWebSearchSettings>().SetKeyAsync(Key, CancellationToken.None);
        }

        window.UpdateLayout();
        return window;
    }

    private static async Task<NewConversationViewModel> OpenNewAsync(DesktopHarness app)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is NewConversationViewModel);
        var page = (NewConversationViewModel)app.Workbench.CurrentPage!;
        await page.Composer.WebSearch!.Loaded;
        return page;
    }

    private static async Task<ConversationViewModel> AskAsync(DesktopHarness app, string question)
    {
        var composer = (await OpenNewAsync(app)).Composer;
        composer.Text = question;
        await composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1] is { State: MessageState.Complete, IsLive: false }, timeoutMs: 10_000);
        var page = (ConversationViewModel)app.Workbench.CurrentPage!;
        await Task.Delay(150);
        return page;
    }
}
