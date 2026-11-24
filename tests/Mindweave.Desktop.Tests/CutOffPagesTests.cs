using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Languages;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Tools;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// No page cuts anything off: every page and its forms, in both languages, at the window's first size and at its smallest
/// (the owner, 2026-11-24). A new page or form gets a scene here.
/// </summary>
[Collection(Text.LanguageTests.Name)]
public class CutOffPagesTests
{
    private static readonly (double Width, double Height)[] Sizes = [(1100, 720), (720, 480)];

    /// <summary>Checks what is on screen, at each size or as it stands (for what a change of size would close).</summary>
    private delegate Task Whole(string name, bool atEachSize = true);

    public static TheoryData<UiLanguage> Languages => new() { UiLanguage.Chinese, UiLanguage.English };

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Onboarding(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        await app.StartAsync();
        Assert.IsType<OnboardingViewModel>(app.Root.Current);
        await whole("onboarding");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task New_conversation(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        await GalleryTests.OpenRecommendationsAsync(app, window);
        await whole("new-conversation");
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore));
        await whole("explore-home");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Conversation(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        var page = await UiPointsGalleryTests.AskWithSourcesAsync(app, window);
        page.Messages[1].ToggleCitationsCommand.Execute(null);
        page.Messages[1].ToggleActivityCommand.Execute(null);
        await whole("conversation");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Summary(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        var seed = await GalleryTests.SeedAsync(app, summaries: true);
        await GalleryTests.LinkGalleryAsync(app, seed);
        var summary = await GalleryTests.OpenSummaryAsync(app, window, seed.Nodes[seed.HighSpeed]);
        await summary.Links!.Loaded;
        await whole("summary-links");
        summary = await GalleryTests.OpenSummaryAsync(app, window, seed.Nodes[seed.Gauge]);
        Find<Expander>(window, "HistoryPanel").IsExpanded = true;
        await whole("summary-history");
        Find<Expander>(window, "HistoryPanel").IsExpanded = false;
        await summary.BeginEditCommand.ExecuteAsync(null);
        await whole("summary-edit");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Network(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        var seed = await GalleryTests.SeedAsync(app, summaries: true);
        await GalleryTests.LinkGalleryAsync(app, seed);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await RecommendedNodesUiTests.PrepareAsync(app, network, window);
        await whole("network");
        network.Select(seed.Nodes[seed.Cable]);
        await network.LinksReloaded;
        await whole("network-selected");
        network.BeginLinkCommand.Execute(null);
        await whole("network-pick");
        await network.PickFromListCommand.ExecuteAsync(null);
        await whole("network-link-picker");
        network.Links!.CancelPickerCommand.Execute(null);
        network.SelectRecommendation(network.RecommendedNodes[0]);
        await whole("network-recommended");
        network.IsListView = !network.IsListView;
        await whole("network-list");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Share(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        var seed = await GalleryTests.SeedAsync(app, summaries: true);
        await GalleryTests.LinkGalleryAsync(app, seed);
        app.Services.GetRequiredService<ISharePrompt>().Open(app.Workbench.CurrentWorkspace!.Id, seed.Gauge);
        await app.Root.Share.Loading;
        await whole("share");
        app.Root.Share.Dots.First(d => d.Id == seed.Rainhill).IsTicked = false;
        app.Shell.NextShare = _ => new ExportDestination("x.html", Path.Combine(app.Paths.DataDirectory, "none", "x.html"), null);
        await app.Root.Share.SaveCommand.ExecuteAsync(null);
        Assert.True(app.Root.Share.NoticeIsError);
        await whole("share-failed");
        app.Root.Share.CloseCommand.Execute(null);

        await app.Services.GetRequiredService<ConversationService>().RenameAsync(seed.Cable, LongName(ConversationRules.MaxTitleLength), CancellationToken.None);
        app.Client.Hold("…");
        await app.Services.GetRequiredService<ConversationService>().AskAsync(seed.Cable, "为什么？", CancellationToken.None);
        app.Services.GetRequiredService<ISharePrompt>().Open(app.Workbench.CurrentWorkspace!.Id, seed.Cable);
        await app.Root.Share.Loading;
        Assert.NotNull(app.Root.Share.AnsweringNote);
        await whole("longest-share");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Settings(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        await app.StartWithWorkspaceAsync("火车");

        // A model that can call tools: the tools' settings and the MCP form can be used.
        var capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true };
        await app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", null, capabilities), CancellationToken.None);
        await app.Services.GetRequiredService<IWebSearchSettings>().SetKeyAsync("tvly-ui-test-key", CancellationToken.None);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        await settings.Model.Loaded;
        settings.Model.IsAdvancedOpen = true;
        await whole("settings");
        var mcp = settings.Tools!.Mcp!;
        await mcp.Loaded;
        mcp.StartAddCommand.Execute(null);
        await whole("settings-mcp-command");
        mcp.IsHttp = true;
        await whole("settings-mcp-http");
        mcp.IsHttp = false;
        mcp.NewName = "笔记工具";
        mcp.NewCommand = FakeMcp.Dotnet;
        mcp.NewArguments = FakeMcp.ServerDll;
        mcp.ReviewCommand.Execute(null);
        Assert.True(mcp.IsConfirming);
        await whole("settings-mcp-confirm");
        await mcp.ConfirmCommand.ExecuteAsync(null);
        var server = Assert.Single(mcp.Servers);
        await server.StartCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => server.Tools.Count > 0, timeoutMs: 20_000);
        foreach (var name in new[] { "write_note", "echo" })
        {
            server.Tools.Single(t => t.Name == name).IsOn = true;
            await UiWait.UntilAsync(() => server.Tools.SingleOrDefault(t => t.Name == name) is { IsOn: true }, timeoutMs: 20_000);
        }

        await whole("settings-mcp-server");
        settings.Tools!.UsesTools = false;
        await whole("settings-tools-off");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Storage(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        await app.StartWithWorkspaceAsync("火车");
        await GalleryTests.GalleryBackupsAsync(app);
        var page = await GalleryTests.OpenStorageDetailsAsync(app, window);
        Assert.Equal(6, page.Rows.Count);
        page.Rows[0].IsChecked = true;
        await whole("storage");
        page.Rows[0].IsChecked = false;
        page.RestoreCommand.Execute(page.Rows.First(r => r.CanRestore));
        await whole("storage-restore");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Statistics(UiLanguage language) => InAsync(
        language,
        async (app, window, whole) =>
        {
            await GalleryTests.SeedStatisticsAsync(app);
            var (page, view) = await GalleryTests.OpenStatisticsAsync(app, window);
            page.Models[0].EditPriceCommand.Execute(null);
            await whole("statistics");
            UiInput.Click(window, Find<CheckBox>(view, "TableToggle"));
            await whole("statistics-table");
        },
        GalleryTests.FixedStatistics);

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Workspaces_and_archived(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Workbench.Workspaces.Add(await app.Services.GetRequiredService<WorkspaceService>().CreateAsync(WorkspaceName.FromStored("摄影史：从银版到数码"), CancellationToken.None));
        app.ShellPage.ManageWorkspacesCommand.Execute(null);
        var page = Assert.IsType<WorkspacesViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await whole("workspaces");
        page.Rows[0].BeginRenameCommand.Execute(null);
        await whole("workspaces-rename");
        page.Rows[0].CancelRenameCommand.Execute(null);
        page.BeginCreateCommand.Execute(null);
        await whole("workspaces-new");

        var conversations = app.Services.GetRequiredService<ConversationService>();
        app.Client.Reply("1804 年。");
        var sent = await conversations.StartAsync(app.Workbench.CurrentWorkspace!.Id, "火车是怎么发明的，为什么先有轨道？", CancellationToken.None);
        await GalleryTests.FinishedAsync(conversations, sent.Conversation);
        await conversations.RenameAsync(sent.Conversation, "轨道的由来", CancellationToken.None);
        await ArchiveAsync(app, sent.Conversation);
        Assert.Equal("轨道的由来", Assert.Single((await OpenArchivedAsync(app)).Items).Title);
        await whole("archived");
    });

    /// <summary>What opens over a page stays inside the window: menus, lists, tips (opened at each size).</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Menus_and_tips(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        var seed = await GalleryTests.SeedAsync(app);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        await settings.Model.Loaded;
        settings.Model.IsAdvancedOpen = true;
        foreach (var (width, height) in Sizes)
        {
            await ResizeAsync(window, width, height);
            var picker = Find<ComboBox>(window, "ProviderPicker");
            await OpenedAsync(picker, () => picker.IsDropDownOpen = true, () => picker.IsDropDownOpen = false);
            var tip = Find<Border>(window, "OutputLimitInfo");
            await OpenedAsync(tip, () => ToolTip.SetIsOpen(tip, true), () => ToolTip.SetIsOpen(tip, false));
            var info = Find<CheckBox>(window, "PrepareNamesToggle").FindAncestorOfType<SettingRow>()!.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_Info");
            await OpenedAsync(info, () => info.Flyout!.ShowAt(info), () => info.Flyout!.Hide());
            var menu = Part<Button>(window, "WorkspacePicker");
            await OpenedAsync(menu, () => app.ShellPage.IsWorkspaceMenuOpen = true, () => app.ShellPage.IsWorkspaceMenuOpen = false);
            GalleryTests.RightClickRow(window, seed.FirstRailway);
            await whole("row-menu", false);
            GalleryTests.CloseRowMenu(window, seed.FirstRailway);
        }

        async Task OpenedAsync(Control from, Action open, Action close)
        {
            from.BringIntoView();
            window.UpdateLayout();
            open();
            await SettleAsync(window);
            await whole("opened-" + from.Name, false);
            close();
            await SettleAsync(window);
        }
    });

    /// <summary>
    /// What a person, a model, a web page or a tool server named fits at its longest: shortened with an ellipsis or on more
    /// lines, and what stands beside it stays whole. A name that fits only because the test's name is short says nothing.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Longest_workspace_and_titles(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        var seed = await GalleryTests.SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        foreach (var conversation in new[] { seed.Root, seed.Rainhill, seed.Cable })
        {
            await conversations.RenameAsync(conversation, LongName(ConversationRules.MaxTitleLength), CancellationToken.None);
        }

        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(seed.Cable)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await whole("longest-branch");
        app.ShellPage.ManageWorkspacesCommand.Execute(null);
        var workspaces = Assert.IsType<WorkspacesViewModel>(app.Workbench.CurrentPage);
        await workspaces.Loaded;
        workspaces.Rows[0].BeginRenameCommand.Execute(null);
        workspaces.Rows[0].RenameText = LongName(WorkspaceName.MaxLength);
        await workspaces.Rows[0].CommitRenameCommand.ExecuteAsync(null);
        Assert.Equal(WorkspaceName.MaxLength, workspaces.Rows[0].Name.Length);
        await whole("longest-workspaces");
        await ArchiveAsync(app, seed.Root);
        Assert.Equal(ConversationRules.MaxTitleLength, (await OpenArchivedAsync(app)).Items[0].Title.Length);
        await whole("longest-archived");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Longest_recommended_question(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        await GalleryTests.OpenRecommendationsAsync(app, window, LongName(RecommendationPrompt.MaxQuestionLength));
        await whole("longest-recommended");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Longest_source_and_tool(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        var host = string.Join('.', Enumerable.Repeat("steam-engines", 8));
        app.Search.Default = _ =>
        [
            new SearchHit(LongName(120), new Uri($"https://{host}.org/steam"), "瓦特在 1769 年改良了蒸汽机。"),
            new SearchHit("铁路的历史", new Uri("https://example.com/rail?a=1"), "第一条公共铁路 1825 年通车。"),
        ];
        var page = await UiPointsGalleryTests.AskWithSourcesAsync(app, window);
        page.Messages[1].ToggleCitationsCommand.Execute(null);
        page.Messages[1].ToggleActivityCommand.Execute(null);
        await whole("longest-sources");

        var (_, statistics) = await GalleryTests.OpenStatisticsAsync(app, window);
        SetText(Find<ItemsControl>(statistics, "ToolCallRows"), LongToolName);
        await whole("longest-tool-calls");
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public Task Longest_tool_server(UiLanguage language) => InAsync(language, async (app, window, whole) =>
    {
        await app.StartWithWorkspaceAsync("火车");
        var capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true };
        await app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", null, capabilities), CancellationToken.None);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        var mcp = settings.Tools!.Mcp!;
        await mcp.Loaded;
        mcp.StartAddCommand.Execute(null);
        mcp.NewName = LongName(80);
        mcp.NewCommand = FakeMcp.Dotnet;
        mcp.NewArguments = FakeMcp.ServerDll;
        mcp.ReviewCommand.Execute(null);
        await whole("longest-server-confirm");
        await mcp.ConfirmCommand.ExecuteAsync(null);
        var server = Assert.Single(mcp.Servers);
        await server.StartCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => server.Tools.Count > 0, timeoutMs: 20_000);
        await SettleAsync(window);
        foreach (var name in window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "McpToolName").Take(2))
        {
            name.SetCurrentValue(TextBlock.TextProperty, LongToolName);
        }

        await whole("longest-server");
    });

    private static async Task ArchiveAsync(DesktopHarness app, ConversationId conversation)
    {
        var outcome = await app.Services.GetRequiredService<ConversationService>().ArchiveAsync(conversation, CancellationToken.None);
        Assert.True(outcome.Archived, "Archiving was refused.");
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.HasArchived);
    }

    private static async Task<ArchivedViewModel> OpenArchivedAsync(DesktopHarness app)
    {
        app.ShellPage.Conversations.OpenArchivedCommand.Execute(null);
        var page = Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        return page;
    }

    /// <summary>A name without a place to break it, of the widest characters.</summary>
    private static string LongName(int length) => string.Concat(Enumerable.Repeat("火车", length / 2));

    private const string LongToolName = "read_the_page_and_write_a_note_about_everything_on_it_into_the_notebook";

    private static void SetText(ItemsControl rows, string text) =>
        rows.GetVisualDescendants().OfType<TextBlock>().First(t => !t.Classes.Contains("caption")).SetCurrentValue(TextBlock.TextProperty, text);

    /// <summary>
    /// A chosen entry of the sidebar is bold: its text has the room it needs, however the choice came about (chosen while
    /// the window changes its size, it was laid out in the room of the regular text).
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Selected_navigation(UiLanguage language)
    {
        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(language);
        try
        {
            await using var scene = await WorkspaceNewsHintTests.Scene.StartAsync();
            foreach (var section in new[] { MainSection.Settings, MainSection.SummaryNetwork, MainSection.Explore })
            {
                scene.Window.Width = 1100;
                scene.Window.Height = 720;
                scene.App.ShellPage.ShowSectionCommand.Execute(section);
                if (scene.App.Workbench.CurrentPage is SettingsHomeViewModel settings)
                {
                    await settings.Loaded;
                }

                await SettleAsync(scene.Window);
                Assert.Empty(CutOff.In(scene.Window));
            }

            scene.Window.Close();
        }
        finally
        {
            CurrentLanguage.Set(before);
        }
    }

    private static async Task InAsync(UiLanguage language, Func<DesktopHarness, MainWindow, Whole, Task> scene, Action<IServiceCollection>? overrides = null)
    {
        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(language);
        var problems = new List<string>();
        try
        {
            await using var app = DesktopHarness.Create(overrides);
            var window = app.ShowWindow(Sizes[0].Width, Sizes[0].Height, ThemeVariant.Light);
            await scene(app, window, async (name, atEachSize) =>
            {
                foreach (var (width, height) in atEachSize ? Sizes : [(window.Width, window.Height)])
                {
                    if (atEachSize)
                    {
                        await ResizeAsync(window, width, height);
                    }

                    problems.AddRange(CutOff.In(window).Select(p => $"{name} at {width} × {height}: {p}"));
                    Screenshots.Capture(window, $"cut-off-{name}-{width}-{(language == UiLanguage.English ? "en" : "zh")}");
                }

                if (atEachSize)
                {
                    await ResizeAsync(window, Sizes[0].Width, Sizes[0].Height);
                }
            });
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(before);
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>The size reaches the page through the dispatcher, and a page may arrange itself anew for it.</summary>
    private static async Task ResizeAsync(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        await SettleAsync(window);
    }

    private static async Task SettleAsync(Window window)
    {
        for (var settle = 0; settle < 4; settle++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }
}
