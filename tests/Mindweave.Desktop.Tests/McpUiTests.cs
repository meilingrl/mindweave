using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Models;
using Mindweave.Application.Tools.Mcp;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Runs;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Third-party tools in the real window (requirements §10.4.6): 设置 › 工具 › MCP 服务器 (add with the confirmation of what
/// runs, start, tools off until enabled, the risk of an enabled tool, a server that cannot start), the approval card of a
/// third-party call with the server, tool and arguments, the activity line naming server and tool, and the statistics row.
/// The fake MCP server runs as a real process. Screenshots (MINDWEAVE_SCREENSHOT_DIR) in light and dark.
/// </summary>
public class McpUiTests
{
    public static TheoryData<string> Themes => new() { "Light", "Dark" };

    private static string Text(string value) => JsonSerializer.Serialize(new { text = value });

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Adding_a_server_shows_what_will_run_and_its_tools_start_off(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme);
        var (settings, mcp) = await OpenSettingsAsync(app, window);

        mcp.StartAddCommand.Execute(null);
        mcp.NewName = "笔记工具";
        mcp.NewCommand = FakeMcp.Dotnet;
        mcp.NewArguments = FakeMcp.ServerDll + "\n--pid-file";
        mcp.NewEnvironment = "NOTES_TOKEN=very-secret-value";
        window.UpdateLayout();
        Find<Grid>(window, "McpAddForm").BringIntoView();
        window.UpdateLayout();
        Screenshots.Capture(window, $"mcp-01-add-form-{theme.ToLowerInvariant()}");

        mcp.ReviewCommand.Execute(null);
        Assert.True(mcp.IsConfirming);
        Assert.Equal(FakeMcp.Dotnet + "\n" + FakeMcp.ServerDll + "\n--pid-file", mcp.PreviewCommandLines);
        Assert.Equal("NOTES_TOKEN", mcp.PreviewEnvironment);
        Assert.StartsWith(Path.Combine(app.Paths.DataDirectory, "mcp"), mcp.Preview!.WorkingDirectory, StringComparison.Ordinal);
        window.UpdateLayout();
        Find<Border>(window, "McpConfirm").BringIntoView();
        window.UpdateLayout();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && (t.Text ?? string.Empty).Contains("very-secret-value", StringComparison.Ordinal));
        Screenshots.Capture(window, $"mcp-02-confirm-{theme.ToLowerInvariant()}");

        await mcp.ConfirmCommand.ExecuteAsync(null);
        var server = Assert.Single(mcp.Servers);
        Assert.Equal((McpServerState.NotStarted, UiText.McpAdded), (server.State, mcp.Status));
        await server.StartCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => server.State == McpServerState.Connected && server.Tools.Count > 0, timeoutMs: 20_000);
        Assert.All(server.Tools, t => Assert.False(t.IsOn));
        Assert.True(server.Tools.Single(t => t.Name == "echo").DeclaresReadOnly);

        // Enabling write_note: a side effect unless the user says otherwise.
        server.Tools.Single(t => t.Name == "write_note").IsOn = true;
        await UiWait.UntilAsync(() => server.Tools.SingleOrDefault(t => t.Name == "write_note") is { IsOn: true, RiskIndex: (int)ToolRisk.SideEffect }, timeoutMs: 20_000);
        server.Tools.Single(t => t.Name == "echo").IsOn = true;
        await UiWait.UntilAsync(() => server.Tools.SingleOrDefault(t => t.Name == "echo") is { IsOn: true, RiskIndex: (int)ToolRisk.NetworkRead }, timeoutMs: 20_000);
        server.Tools.Single(t => t.Name == "echo").RiskIndex = (int)ToolRisk.LocalRead;
        var store = app.Services.GetRequiredService<IMcpServers>();
        for (var i = 0; i < 200 && (await store.ListAsync(CancellationToken.None)).Single().Tool("echo")?.Risk != ToolRisk.LocalRead; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(ToolRisk.LocalRead, (await store.ListAsync(CancellationToken.None)).Single().Tool("echo")?.Risk);
        window.UpdateLayout();
        Find<StackPanel>(window, "McpSection").BringIntoView();
        window.UpdateLayout();
        Screenshots.Capture(window, $"mcp-03-server-tools-{theme.ToLowerInvariant()}");
        // The risk drop-down shows the app's chevron (the 16 × 16 icon template would leave Fluent's own glyph off its canvas).
        ComboBox? risk = null;
        await UiWait.UntilAsync(() =>
        {
            window.UpdateLayout();
            risk = window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Name == "McpToolRisk" && c.IsEffectivelyVisible);
            return risk?.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(p => p.Name == "Chevron") == true;
        }, timeoutMs: 20_000);
        Assert.Same(
            Avalonia.Application.Current!.FindResource("IconChevronDown"),
            risk!.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "Chevron").Data);

        // Removing asks first, then ends the process and forgets the server.
        server.AskRemoveCommand.Execute(null);
        Assert.True(server.IsConfirmingRemove);
        await server.RemoveCommand.ExecuteAsync(null);
        Assert.Empty(mcp.Servers);
        Assert.Empty(await app.Services.GetRequiredService<IMcpServers>().ListAsync(CancellationToken.None));
        _ = settings;
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_draft_without_a_command_says_why_and_a_server_that_cannot_start_shows_its_error()
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, "Light");
        var (_, mcp) = await OpenSettingsAsync(app, window);

        mcp.StartAddCommand.Execute(null);
        mcp.NewName = "空";
        mcp.ReviewCommand.Execute(null);
        Assert.Equal((false, true), (mcp.IsConfirming, mcp.StatusIsError));
        Assert.Contains("命令", mcp.Status, StringComparison.Ordinal);

        mcp.NewCommand = FakeMcp.Dotnet;
        mcp.NewArguments = FakeMcp.ServerDll + "\n--exit-at-start";
        mcp.ReviewCommand.Execute(null);
        await mcp.ConfirmCommand.ExecuteAsync(null);
        var server = Assert.Single(mcp.Servers);
        await server.StartCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => server.State == McpServerState.Failed, timeoutMs: 20_000);
        Assert.True(server.HasError);
        Assert.Contains("fatal: cannot start", server.StandardError, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task A_third_party_side_effect_shows_its_card_and_the_activity_line_names_server_and_tool(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme);
        var servers = app.Services.GetRequiredService<IMcpServers>();
        var added = await servers.AddAsync(FakeMcp.Command("notes"), CancellationToken.None);
        var tools = await app.Services.GetRequiredService<IMcpConnections>().StartAsync(added.Id, CancellationToken.None);
        await servers.EnableToolAsync(added.Id, tools.Single(t => t.Name == "echo"), CancellationToken.None);
        await servers.EnableToolAsync(added.Id, tools.Single(t => t.Name == "write_note"), CancellationToken.None);
        app.Client.CallTool("notes__echo", Text("你好"), "我先问一下工具。");
        app.Client.CallTool("notes__write_note", Text("周五前交报告"));

        var composer = (await OpenNewAsync(app)).Composer;
        composer.Text = "帮我记下：周五前交报告";
        await composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1].HasApproval, timeoutMs: 20_000);
        var answer = ((ConversationViewModel)app.Workbench.CurrentPage!).Messages[1];

        Assert.True(answer.IsThirdPartyApproval);
        Assert.Equal(UiText.McpApprovalTitle, answer.ApprovalTitle);
        Assert.Equal(UiText.McpApprovalWaiting("notes", "write_note"), answer.StatusText);
        Assert.Equal(("notes", "write_note"), (answer.PendingApproval!.Server, answer.PendingApproval.ToolTitle));
        window.UpdateLayout();
        Assert.Contains(window.GetVisualDescendants().OfType<Grid>(), g => g.Name == "McpApprovalDetails" && g.IsEffectivelyVisible);
        Assert.Contains("周五前交报告", window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "McpApprovalArguments" && t.IsEffectivelyVisible).Text, StringComparison.Ordinal);
        answer.IsActivityExpanded = true;
        window.UpdateLayout();
        Screenshots.Capture(window, $"mcp-04-approval-card-{theme.ToLowerInvariant()}");

        app.Client.Reply("已经记下了。");
        // The visible 允许 button's own command (a pointer press is unreliable here: the live answer above the card keeps
        // re-laying out under load, moving the button between press and release).
        var allow = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ApprovalAllow" && b.IsEffectivelyVisible);
        Assert.True(allow.Command!.CanExecute(null));
        allow.Command.Execute(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1] is { State: MessageState.Complete, IsLive: false }, timeoutMs: 20_000);
        answer = ((ConversationViewModel)app.Workbench.CurrentPage!).Messages[1];
        Assert.Equal("调用了 1 次 notes › echo · 调用了 1 次 notes › write_note", answer.ActivityText);
        Assert.StartsWith("notes › write_note：", answer.ActivitySteps[1].Title, StringComparison.Ordinal);
        await UiWait.UntilAsync(() => ((ConversationViewModel)app.Workbench.CurrentPage!).Messages[1].ActivitySteps[1].Meta.EndsWith(UiText.RunStepApproved, StringComparison.Ordinal), timeoutMs: 20_000);
        answer = ((ConversationViewModel)app.Workbench.CurrentPage!).Messages[1];
        answer.IsActivityExpanded = true;
        window.UpdateLayout();
        Screenshots.Capture(window, $"mcp-05-activity-{theme.ToLowerInvariant()}");

        // The statistics name the server and tool.
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).Loaded;
        window.UpdateLayout();
        UiInput.Click(window, Find<Button>(window, "StatisticsButton"));
        var stats = Assert.IsType<UsageStatisticsViewModel>(app.Workbench.CurrentPage);
        await stats.Loaded;
        await UiWait.UntilAsync(() => stats.HasToolUsage, timeoutMs: 20_000);
        Assert.Contains(new ToolCallRowViewModel("notes › write_note", UiText.ToolCallCount(1)), stats.ToolCalls);
        Assert.Contains(new ToolCallRowViewModel("notes › echo", UiText.ToolCallCount(1)), stats.ToolCalls);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quick_toggles_all_stick_and_a_tool_turned_off_by_a_run_shows_its_reason_without_reopening()
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, "Light");
        var servers = app.Services.GetRequiredService<IMcpServers>();
        var added = await servers.AddAsync(FakeMcp.Command("notes"), CancellationToken.None);
        var (_, mcp) = await OpenSettingsAsync(app, window);
        var server = Assert.Single(mcp.Servers);
        await server.StartCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => server.State == McpServerState.Connected && server.Tools.Count == 13, timeoutMs: 20_000);

        // Four ticks in a row, before any save finished: none is undone by the refreshes the others cause.
        string[] picked = ["echo", "env", "cwd", "write_note"];
        foreach (var name in picked)
        {
            server.Tools.Single(t => t.Name == name).IsOn = true;
        }

        for (var i = 0; i < 200 && (await servers.GetAsync(added.Id, CancellationToken.None))!.Tools.Count(t => t.Enabled) < picked.Length; i++)
        {
            await Task.Delay(20);
        }

        await UiWait.UntilAsync(() => picked.All(n => server.Tools.Single(t => t.Name == n).IsOn) && server.Tools.Count(t => t.IsOn) == picked.Length, timeoutMs: 20_000);
        Assert.Equal(picked.Order(StringComparer.Ordinal), (await servers.GetAsync(added.Id, CancellationToken.None))!.Tools.Where(t => t.Enabled).Select(t => t.Name).Order(StringComparer.Ordinal));

        // A run finds write_note changed at the server and turns it off (decision M6); the open page shows why.
        var listed = server.Tools.Select(t => t.Tool).Select(t => t.Name == "write_note" ? t with { Description = "改过的说明" } : t).ToList();
        await servers.ReconcileAsync(added.Id, listed, CancellationToken.None);
        await UiWait.UntilAsync(() => server.Tools.Single(t => t.Name == "write_note") is { IsOn: false, HasDisabledReason: true }, timeoutMs: 20_000);
        Assert.Equal(Application.Tools.Mcp.McpServerSettings.ChangedDefinition.ToString(), server.Tools.Single(t => t.Name == "write_note").DisabledReason);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_closed_settings_page_leaves_the_servers_events()
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, "Light");
        var servers = app.Services.GetRequiredService<IMcpServers>();
        var added = await servers.AddAsync(FakeMcp.Command("notes"), CancellationToken.None);
        var tools = await app.Services.GetRequiredService<IMcpConnections>().StartAsync(added.Id, CancellationToken.None);
        await servers.EnableToolAsync(added.Id, tools.Single(t => t.Name == "echo"), CancellationToken.None);
        var (settings, mcp) = await OpenSettingsAsync(app, window);
        var server = Assert.Single(mcp.Servers);
        await UiWait.UntilAsync(() => server.Tools.SingleOrDefault(t => t.Name == "echo")?.IsOn == true, timeoutMs: 20_000);

        settings.Dispose();
        Assert.True(mcp.IsDisposed);

        // A run turns echo off afterwards: the closed page no longer follows (nothing refreshes it or keeps it alive).
        await servers.ReconcileAsync(added.Id, [.. tools.Select(t => t.Name == "echo" ? t with { Description = "改过" } : t)], CancellationToken.None);
        await Task.Delay(200);
        Assert.True(server.Tools.Single(t => t.Name == "echo").IsOn);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task A_long_tool_list_scrolls_filters_and_collapses_to_the_enabled_tools(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await StartAsync(app, theme);
        var servers = app.Services.GetRequiredService<IMcpServers>();
        var added = await servers.AddAsync(FakeMcp.Command("notes"), CancellationToken.None);
        var (_, mcp) = await OpenSettingsAsync(app, window);
        var server = Assert.Single(mcp.Servers);
        await server.StartCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => server.State == McpServerState.Connected && server.Tools.Count == 13, timeoutMs: 20_000);

        // Nothing enabled yet: open, every tool shown in a scroll area of bounded height, with a filter.
        Assert.True(server.IsExpanded);
        Assert.Equal(13, server.VisibleTools.Count);
        Assert.True(server.ShowsFilter);
        window.UpdateLayout();
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "McpToolScroll");
        Assert.True(scroll.Bounds.Height <= 360.5, scroll.Bounds.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);

        server.Filter = "write";
        Assert.Equal(["hang_write", "write_note"], server.VisibleTools.Select(t => t.Name));
        server.Tools.Single(t => t.Name == "write_note").IsOn = true;
        server.Filter = "回声";
        Assert.Equal(UiText.McpToolsNoMatch, server.EmptyNote);
        server.Filter = string.Empty;
        server.Tools.Single(t => t.Name == "echo").IsOn = true;
        await UiWait.UntilAsync(() => server.Tools.Count(t => t.IsOn) == 2 && server.ToolCount.StartsWith("已启用 2 个", StringComparison.Ordinal), timeoutMs: 20_000);
        window.UpdateLayout();
        Find<StackPanel>(window, "McpSection").BringIntoView();
        window.UpdateLayout();
        Screenshots.Capture(window, $"mcp-06-tools-expanded-{theme.ToLowerInvariant()}");

        // Collapsed: only the enabled tools, still with their risk.
        server.ToggleToolsCommand.Execute(null);
        Assert.False(server.IsExpanded);
        Assert.Equal(["echo", "write_note"], server.VisibleTools.Select(t => t.Name));
        Assert.Equal(UiText.McpToolsExpand(13), server.ToggleLabel);
        window.UpdateLayout();
        Find<StackPanel>(window, "McpSection").BringIntoView();
        window.UpdateLayout();
        Screenshots.Capture(window, $"mcp-07-tools-collapsed-{theme.ToLowerInvariant()}");

        // Reopened with tools enabled: collapsed from the start.
        await mcp.ReloadAsync();
        var again = Assert.Single(mcp.Servers);
        await UiWait.UntilAsync(() => again.Tools.Count == 13, timeoutMs: 20_000);
        Assert.False(again.IsExpanded);
        Assert.Equal(2, again.VisibleTools.Count);
        window.Close();
    }

    private static async Task<MainWindow> StartAsync(DesktopHarness app, string theme)
    {
        var window = app.ShowWindow(1000, 760, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        await app.StartWithWorkspaceAsync("火车历史");
        var capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true };
        await app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", null, capabilities), CancellationToken.None);
        window.UpdateLayout();
        return window;
    }

    private static async Task<(SettingsHomeViewModel Settings, McpServersPanel Mcp)> OpenSettingsAsync(DesktopHarness app, MainWindow window)
    {
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        await settings.Tools!.Loaded;
        var mcp = settings.Tools.Mcp!;
        await mcp.Loaded;
        Assert.True(mcp.IsAvailable);
        window.UpdateLayout();
        Find<StackPanel>(window, "McpSection").BringIntoView();
        window.UpdateLayout();
        return (settings, mcp);
    }

    private static async Task<NewConversationViewModel> OpenNewAsync(DesktopHarness app)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is NewConversationViewModel);
        var page = (NewConversationViewModel)app.Workbench.CurrentPage!;
        await page.Composer.WebSearch!.Loaded;
        return page;
    }
}
