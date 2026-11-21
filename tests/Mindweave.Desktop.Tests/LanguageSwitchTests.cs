using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Languages;
using Mindweave.Application.Models;
using Mindweave.Application.Summaries;
using Mindweave.Application.Tools.Mcp;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The language switch rebuilds the view layer in place (bilingual-ui.md §6): what runs in the services goes on, where
/// the user was comes back, input that lives only in a page is carried over, and every piece of interface text is read
/// again in the new language.
/// </summary>
[Collection(Text.LanguageTests.Name)]
public partial class LanguageSwitchTests
{
    private static readonly string Pad = string.Concat(Enumerable.Repeat("蒸汽机车在轨道上行驶，带动了工业革命的发展。", 6));

    private static readonly string LongAnswer = string.Join("\n\n", Enumerable.Range(1, 24).Select(i => $"第{i}段：{Pad}"));

    [AvaloniaFact]
    public async Task Switch_keeps_a_streaming_answer()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow();
            await app.StartWithWorkspaceAsync("火车");
            await app.ConfigureModelAsync();
            var held = app.Client.Hold("第一句。");
            var page = await AskAsync(app, held, "火车是怎么发明的？");
            await UiWait.UntilAsync(() => page.Messages[1].IsStreaming);

            await SwitchAsync(app, UiLanguage.English);

            var again = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(page, again);
            Assert.Equal(page.Id, again.Id);
            await UiWait.UntilAsync(() => again.Messages.Count == 2 && again.Messages[1].IsStreaming);
            held.Push("第二句。");
            held.Release.TrySetResult();
            await UiWait.UntilAsync(() => again.Messages.Count == 2 && again.Messages[1] is { State: MessageState.Complete, IsLive: false });
            Assert.Equal("第一句。第二句。", again.Messages[1].Body);
            var stored = (await app.Services.GetRequiredService<IConversations>().LoadAsync(page.Id, CancellationToken.None))!.Messages[1];
            Assert.Equal(MessageState.Complete, stored.State);
            window.UpdateLayout();
            Assert.Single(Answers(window));
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [AvaloniaFact]
    public async Task Switch_keeps_a_pending_approval()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 760);
            await StartWithToolsAsync(app);
            var servers = app.Services.GetRequiredService<IMcpServers>();
            var added = await servers.AddAsync(FakeMcp.Command("notes"), CancellationToken.None);
            var tools = await app.Services.GetRequiredService<IMcpConnections>().StartAsync(added.Id, CancellationToken.None);
            await servers.EnableToolAsync(added.Id, tools.Single(t => t.Name == "write_note"), CancellationToken.None);
            app.Client.CallTool("notes__write_note", JsonSerializer.Serialize(new { text = "周五前交报告" }));
            app.ShellPage.Conversations.NewCommand.Execute(null);
            var fresh = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
            fresh.Composer.Text = "帮我记下：周五前交报告";
            await fresh.Composer.SendCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1].HasApproval, timeoutMs: 20_000);
            var page = (ConversationViewModel)app.Workbench.CurrentPage!;

            await SwitchAsync(app, UiLanguage.English);

            var again = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(page, again);
            await UiWait.UntilAsync(() => again.Messages.Count == 2 && again.Messages[1].HasApproval);
            Assert.Equal(UiText.McpApprovalWaiting("notes", "write_note"), again.Messages[1].StatusText);
            window.UpdateLayout();
            var allow = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ApprovalAllow" && b.IsEffectivelyVisible);
            Assert.Equal("Allow", ButtonText(allow));

            app.Client.Reply("已经记下了。");
            allow.Command!.Execute(null);
            await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1] is { State: MessageState.Complete, IsLive: false }, timeoutMs: 20_000);
            var done = ((ConversationViewModel)app.Workbench.CurrentPage!).Messages[1];
            Assert.Equal("已经记下了。", done.Body);
            Assert.Contains(done.ActivitySteps, s => s.Title.StartsWith("notes › write_note", StringComparison.Ordinal));
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [AvaloniaFact]
    public async Task Switch_keeps_draft_reading_position_and_settings_subpage()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 700);
            await app.StartWithWorkspaceAsync("火车");
            await app.ConfigureModelAsync();
            var page = await ConversationAsync(app, window, "火车是怎么发明的？", LongAnswer);
            var offset = await ScrollToAsync(window, 0.4);
            page.Composer.Text = "还没发出的追问";

            await SwitchAsync(app, UiLanguage.English);

            var again = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(page, again);
            window.UpdateLayout();
            await CurrentView(window).Restored;
            await UiWait.UntilAsync(() => again.Composer.Text == "还没发出的追问");
            window.UpdateLayout();
            Assert.InRange(Scroller(window).Offset.Y, offset - 4, offset + 4);

            // Settings, scrolled down, with a subpage open over it.
            app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
            var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            await settings.Loaded;
            window.UpdateLayout();
            var scroller = Find<ScrollViewer>(window.GetVisualDescendants().OfType<SettingsHomeView>().Single(), "Scroller");
            scroller.Offset = new Vector(0, 300);
            window.UpdateLayout();
            Assert.Equal(300, settings.ScrollOffset);
            settings.ShowStorageDetailsCommand.Execute(null);
            Assert.IsType<StorageDetailsViewModel>(app.Workbench.CurrentPage);

            await SwitchAsync(app, UiLanguage.Chinese);

            Assert.IsType<StorageDetailsViewModel>(app.Workbench.CurrentPage);
            Assert.Equal(MainSection.Settings, app.Workbench.CurrentSection);
            app.ShellPage.GoBackCommand.Execute(null);
            var settingsAgain = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(settings, settingsAgain);
            await settingsAgain.Loaded;
            window.UpdateLayout();
            scroller = Find<ScrollViewer>(window.GetVisualDescendants().OfType<SettingsHomeView>().Single(), "Scroller");
            await UiWait.UntilAsync(() =>
            {
                window.UpdateLayout();
                return Math.Abs(scroller.Offset.Y - 300) < 1;
            });

            // The explore section's page and its draft are still there behind it.
            app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
            var back = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            Assert.Equal(page.Id, back.Id);
            Assert.Equal("还没发出的追问", back.Composer.Text);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [AvaloniaFact]
    public async Task Switch_keeps_summary_edit_mode_and_unsaved_text()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 720);
            var page = await app.StartWithConversationAsync(waitForTitle: false);
            await page.Summary.SummarizeCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => page.Summary.HasSummary && page.Summary.RunningSummary is null);
            page.Summary.OpenSummaryCommand.Execute(null);
            var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
            await summary.Loaded;
            await summary.BeginEditCommand.ExecuteAsync(null);
            window.UpdateLayout();
            summary.EditTitle = "改过的标题";
            summary.EditBody = "还没保存的修改";

            await SwitchAsync(app, UiLanguage.English);

            var again = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(summary, again);
            await again.Loaded;
            await again.Resumed;
            Assert.True(again.IsEditing);
            Assert.Equal("改过的标题", again.EditTitle);
            Assert.Equal("还没保存的修改", again.EditBody);
            Assert.Null(again.Notice);
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible && t.Text == "还没保存的修改");

            await again.SaveEditCommand.ExecuteAsync(null);
            var detail = await app.Services.GetRequiredService<SummaryService>().GetAsync(again.Node, CancellationToken.None);
            Assert.Equal("还没保存的修改", detail!.Current.Body);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [AvaloniaFact]
    public async Task Switch_keeps_summary_generation_running()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow();
            var page = await app.StartWithConversationAsync(waitForTitle: false);
            var gate = app.Client.Knowledge.Hold();
            await page.Summary.SummarizeCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => page.Summary.RunningSummary is not null);

            await SwitchAsync(app, UiLanguage.English);

            var again = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(page, again);
            await UiWait.UntilAsync(() => again.Summary.RunningSummary is not null);
            gate.TrySetResult();
            await UiWait.UntilAsync(() => again.Summary.HasSummary && again.Summary.RunningSummary is null);
            Assert.Equal(UiText.SummaryEntry(FakeChatClient.DefaultSummaryTitle), again.Summary.SummaryEntryText);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [AvaloniaFact]
    public async Task Switch_keeps_network_viewport()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 720);
            await SummaryNetworkScenario.SeedAsync(app);
            var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
            var viewport = new Viewport(40, -25, 1.5);
            network.Session.Viewport = viewport;

            await SwitchAsync(app, UiLanguage.English);

            var again = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(network, again);
            Assert.Same(network.Session, again.Session);
            await again.Loaded;
            window.UpdateLayout();
            Assert.Equal(viewport, CanvasOf(window).Current);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [AvaloniaFact]
    public async Task Switch_carries_unsaved_model_form_input()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 760);
            await StartWithToolsAsync(app);
            app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
            var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            await settings.Loaded;
            await settings.Model.Loaded;
            await settings.Tools!.Loaded;
            await settings.Tools.Web!.Loaded;
            await settings.Tools.Mcp!.Loaded;
            settings.Model.Name = "我的服务";
            settings.Model.Model = "typed-model";
            settings.Model.ApiKey = "sk-typed";
            settings.Model.OutputLimit = "4096";
            settings.Tools.Web.NewKey = "search-key";
            settings.Tools.Mcp.StartAddCommand.Execute(null);
            settings.Tools.Mcp.NewName = "笔记";
            settings.Tools.Mcp.NewCommand = "notes-server";

            await SwitchAsync(app, UiLanguage.English);

            var again = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(settings, again);
            await again.Model.Loaded;
            Assert.Equal(("我的服务", "typed-model", "sk-typed", "4096"), (again.Model.Name, again.Model.Model, again.Model.ApiKey, again.Model.OutputLimit));
            Assert.Equal("https://fake.example/v1", again.Model.BaseUrl);
            Assert.Equal("search-key", again.Tools!.Web!.NewKey);
            Assert.True(again.Tools.Mcp!.IsAdding);
            Assert.Equal(("笔记", "notes-server"), (again.Tools.Mcp.NewName, again.Tools.Mcp.NewCommand));

            // Nothing was saved on the way.
            var saved = await app.Services.GetRequiredService<IModelSettings>().GetAsync(CancellationToken.None);
            Assert.Equal("fake-model", saved!.Model);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [AvaloniaFact]
    public async Task Elapsed_time_continues_across_a_switch()
    {
        try
        {
            var clock = new ManualTime();
            await using var app = DesktopHarness.Create(s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                s.AddSingleton<IClock>(clock);
            });
            var window = app.ShowWindow();
            await app.StartWithWorkspaceAsync("火车");
            await app.ConfigureModelAsync();
            var held = app.Client.Hold("第一句。");
            var page = await AskAsync(app, held, "问");
            clock.Advance(TimeSpan.FromSeconds(12));
            await UiWait.UntilAsync(() => page.Messages[1].ElapsedText == UiText.Elapsed(TimeSpan.FromSeconds(12)));

            await SwitchAsync(app, UiLanguage.English);

            var again = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            window.UpdateLayout();
            await UiWait.UntilAsync(() => again.Messages.Count == 2 && again.IsTicking);
            Assert.Equal("12 s", again.Messages[1].ElapsedText);
            Assert.False(page.IsTicking);
            clock.Advance(TimeSpan.FromSeconds(53));
            await UiWait.UntilAsync(() => again.Messages[1].ElapsedText == UiText.Elapsed(TimeSpan.FromSeconds(65)));
            held.Release.TrySetResult();
            await UiWait.UntilAsync(() => again.Messages[1] is { State: MessageState.Complete, IsLive: false });
            Assert.Equal(UiText.AnswerDuration(TimeSpan.FromSeconds(65)), again.Messages[1].DurationText);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [AvaloniaFact]
    public async Task Every_visible_text_is_English_after_switch()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1200, 800);
            await SummaryNetworkScenario.SeedAsync(app);
            var conversations = app.Services.GetRequiredService<ConversationService>();
            var listed = await conversations.ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);
            var root = listed.MinBy(c => c.Conversation.CreatedAt)!.Conversation.Id;

            var user = await UserContentAsync(app);

            // The walk sees interface text at all: before the switch it finds Chinese beside the user's content.
            await SettleAsync(window);
            Assert.NotEmpty(ChineseIn(window, "before", user));

            await SwitchAsync(app, UiLanguage.English);
            var problems = new List<string>();

            // Explore: the home, then a conversation with its answer, branch entries and summary.
            app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
            await SettleAsync(window);
            problems.AddRange(ChineseIn(window, "explore home", user));
            app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(root)));
            var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            await page.Loaded;
            await CurrentView(window).Restored;
            await SettleAsync(window);
            problems.AddRange(ChineseIn(window, "conversation", user));

            // New exploration.
            app.ShellPage.Conversations.NewCommand.Execute(null);
            await Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage).DraftLoaded;
            await SettleAsync(window);
            problems.AddRange(ChineseIn(window, "new exploration", user));

            // The summary network and a summary.
            var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
            await SettleAsync(window);
            problems.AddRange(ChineseIn(window, "summary network", user));
            network.OpenCommand.Execute(network.Nodes[0].Id);
            var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
            await summary.Loaded;
            await SettleAsync(window);
            problems.AddRange(ChineseIn(window, "summary", user));

            // Settings and its subpages.
            app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
            var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            await settings.Loaded;
            await SettleAsync(window);
            problems.AddRange(ChineseIn(window, "settings", user));
            settings.ShowStorageDetailsCommand.Execute(null);
            Assert.IsType<StorageDetailsViewModel>(app.Workbench.CurrentPage);
            await SettleAsync(window);
            problems.AddRange(ChineseIn(window, "storage", user));
            app.ShellPage.GoBackCommand.Execute(null);
            settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            settings.ShowWorkspacesCommand.Execute(null);
            await Assert.IsType<WorkspacesViewModel>(app.Workbench.CurrentPage).Loaded;
            await SettleAsync(window);
            problems.AddRange(ChineseIn(window, "workspaces", user));

            Assert.True(problems.Count == 0, $"{problems.Count} texts still in Chinese:\n" + string.Join('\n', problems.Distinct().Take(80)));
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    internal static Task SwitchAsync(DesktopHarness app, UiLanguage language) =>
        app.Services.GetRequiredService<LanguageSwitch>().SwitchAsync(language);

    private static async Task StartWithToolsAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车历史");
        var capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true };
        await app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", null, capabilities), CancellationToken.None);
    }

    private static async Task<ConversationViewModel> AskAsync(DesktopHarness app, FakeChatClient.HeldReply held, string question)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(TestWaits.Ms(5000) / 1000));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2);
        return page;
    }

    private static async Task<ConversationViewModel> ConversationAsync(DesktopHarness app, Window window, string question, string answer)
    {
        app.Client.Reply(answer);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        await CurrentView(window).Restored;
        window.UpdateLayout();
        return page;
    }

    private static async Task<double> ScrollToAsync(Window window, double fraction)
    {
        var view = CurrentView(window);
        var scroller = Scroller(window);
        scroller.Offset = new Vector(0, Math.Round((scroller.Extent.Height - scroller.Viewport.Height) * fraction));
        window.UpdateLayout();
        var last = double.NaN;
        await UiWait.UntilAsync(() =>
        {
            var stable = scroller.Offset.Y == last;
            last = scroller.Offset.Y;
            return stable && !view.IsPositionPending;
        });
        return scroller.Offset.Y;
    }

    private static string ButtonText(Button button) =>
        button.Content as string ?? string.Concat(button.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static async Task SettleAsync(Window window)
    {
        for (var i = 0; i < 5; i++)
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        await UiWait.AnswersRenderedAsync(window);
        window.UpdateLayout();
    }

    /// <summary>What the user wrote or the model produced: names, questions, answers, titles and summaries.</summary>
    private static async Task<List<string>> UserContentAsync(DesktopHarness app)
    {
        var texts = new List<string>();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        foreach (var workspace in app.Workbench.Workspaces)
        {
            texts.Add(workspace.Name.Value);
            foreach (var listed in await conversations.ListAsync(workspace.Id, CancellationToken.None))
            {
                texts.Add(listed.Conversation.Title ?? string.Empty);
                var loaded = await conversations.LoadAsync(listed.Conversation.Id, CancellationToken.None);
                texts.AddRange(loaded!.Messages.Select(m => m.Text));

                // A branch's source passage is quoted where the branch is listed.
                texts.AddRange((await conversations.ListBranchesAsync(listed.Conversation.Id, CancellationToken.None)).Select(b => b.Quote));
            }
        }

        var summaries = app.Services.GetRequiredService<SummaryService>();
        foreach (var node in (await summaries.ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None)))
        {
            var detail = await summaries.GetAsync(node.Node.Id, CancellationToken.None);
            texts.Add(detail!.Current.Title);
            texts.Add(detail.Current.Body);
            texts.Add(detail.Node.BaseTitle);
            texts.Add(detail.Node.DisplayName);
        }

        // Language names are never translated (§4.2).
        texts.Add("简体中文");
        return [.. texts.Where(t => t.Length > 0).Distinct().OrderByDescending(t => t.Length)];
    }

    /// <summary>
    /// Every text shown in <paramref name="window"/> that still holds Chinese once the user's own content is taken out:
    /// text blocks (and their runs), tooltips, accessible names and help texts, and watermarks of visible controls.
    /// </summary>
    private static IEnumerable<string> ChineseIn(Window window, string where, IReadOnlyList<string> user)
    {
        foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible))
        {
            var texts = new List<string?>
            {
                ToolTip.GetTip(control) as string,
                AutomationProperties.GetName(control),
                AutomationProperties.GetHelpText(control),
            };
            switch (control)
            {
                case TextBlock block:
                    texts.Add(block.Text);
                    texts.AddRange(block.Inlines?.OfType<Run>().Select(r => r.Text) ?? []);
                    break;
                case TextBox box:
                    texts.Add(box.PlaceholderText);
                    break;
                case ContentControl { Content: string content }:
                    texts.Add(content);
                    break;
            }

            foreach (var text in texts.OfType<string>())
            {
                var rest = text;
                foreach (var own in user)
                {
                    rest = rest.Replace(own, string.Empty, StringComparison.Ordinal);
                }

                if (Chinese().IsMatch(rest))
                {
                    yield return $"{where}: {control.GetType().Name} {control.Name}: {text}";
                }
            }
        }
    }

    /// <summary>Chinese characters and full-width punctuation.</summary>
    [GeneratedRegex(@"[　-〿㐀-䶿一-鿿＀-￯]")]
    private static partial Regex Chinese();
}
