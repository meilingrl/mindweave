using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Preferences;
using Mindweave.Application.Startup;
using Mindweave.Application.Statistics;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Application.SummaryLinks;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;
using Mindweave.Testing;
using Mindweave.Testing.Usage;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// A screenshot of every page and state, for reviewing the UI before and after visual work. Runs only when
/// MINDWEAVE_SCREENSHOT_DIR is set (run with <c>--filter Category=Gallery</c>); each scene is seeded through the real
/// services with realistic content, a fixed clock and a fixed data directory so the frames are comparable run to run.
/// MINDWEAVE_GALLERY_THEME=Dark renders the same scenes in the dark theme.
/// </summary>
[Trait("Category", "Gallery")]
public class GalleryTests
{
    private const string RootQuestion = "火车是怎么发明的？";

    private const string RootAnswer = """
        火车的诞生不是一次发明，而是**蒸汽动力**与**轨道运输**两条技术线索在十九世纪初的汇合。早在 16 世纪，欧洲矿山就用木轨让马拉的矿车省力；1804 年，理查德·特里维西克造出第一台在轨道上行驶的蒸汽机车，但沉重的机车常常压断铸铁轨道。

        真正让铁路走向实用的是乔治·斯蒂芬森。1825 年他主持修建的斯托克顿—达灵顿铁路通车，1829 年他和儿子罗伯特设计的**“火箭号”**在雨山试验中胜出，证明蒸汽机车可以稳定、快速地牵引列车。此后几十年间的关键改进包括：

        1. **多管锅炉**：增加受热面积，使蒸汽产生得更快；
        2. **鼓风管**：用排出的废汽抽风，让炉火更旺；
        3. **熟铁轨道**：取代易碎的铸铁轨，能承受更重的机车；
        4. **标准轨距**：1435 毫米的轨距逐渐成为英国乃至世界的通行标准。

        衡量一台蒸汽机车的牵引能力，工程师常用一个简化公式估算起动牵引力：

        $$F = \frac{0.85\,p\,d^2 s}{D}$$

        其中 $p$ 是锅炉压力，$d$ 是汽缸直径，$s$ 是活塞行程，$D$ 是动轮直径。加大汽缸、提高压力或缩小动轮都能增加牵引力，但缩小动轮会限制速度——这正是货运机车与客运机车外形不同的原因。
        """;

    private const string RainhillAnswer = """
        雨山试验是 1829 年 10 月利物浦—曼彻斯特铁路公司举办的机车比赛，目的是决定新线路用什么方式牵引列车。规则要求参赛机车：

        - 自重不超过 6 吨，并牵引三倍于自重的负载；
        - 在约 2.4 公里的试验段上往返 20 次，相当于两城之间的全程；
        - 平均速度不低于每小时 16 公里。

        五台参赛机车中，只有斯蒂芬森的**火箭号**完成了全部试验，最高时速约 47 公里。它的成功让铁路公司放弃了用固定蒸汽机拉缆绳的方案。
        """;

    private const string CableAnswer = """
        固定蒸汽机方案把蒸汽机装在沿线的机房里，用滚筒卷动长长的缆绳，把车厢一段一段地拖过去。它适合坡度很大的矿区线路，但缆绳磨损快、一段出故障全线停运，速度也受卷筒限制。

        火箭号证明了**机车自带动力**更灵活，此后缆索牵引只在少数陡坡上保留。
        """;

    private const string GaugeAnswer = """
        1435 毫米（4 英尺 8.5 英寸）并不是计算出来的最优值，而是沿用了英格兰东北部煤矿马车道的轨距。斯蒂芬森修建早期铁路时直接采用了它，随着他参与的线路越来越多，这一尺寸就成了事实标准。

        1846 年英国《轨距法》规定新建客运铁路必须采用这一轨距，此后它随英国的机车与工程师传播到世界各地。
        """;

    private const string WheelAnswer = "动轮每转一圈，列车前进的距离等于动轮周长 $\\pi D$。活塞往复的频率有上限，所以动轮越大，同样的转速下跑得越快；但同样的汽缸推力作用在更大的轮子上，起动牵引力就更小。客运机车因此用大动轮，货运机车用小动轮。";

    private const string HighSpeedQuestion = "高铁为什么能跑这么快？";

    private const string HighSpeedAnswer = """
        高铁的速度来自几项技术的共同作用，而不是单纯加大功率：

        1. **无砟轨道**：用混凝土道床代替碎石道砟，轨道几何形状长期稳定，高速下不易变形；
        2. **动力分散**：牵引电机分布在多节车厢上，加速快、黏着利用好；
        3. **受电弓与接触网**：从高压电网持续取电，功率不受车载燃料限制；
        4. **流线型车头**：降低空气阻力和隧道中的压力波。

        在 300 km/h 以上，空气阻力约占总阻力的八成以上，它大致与速度的平方成正比：$F_d = \frac{1}{2}\rho v^2 C_d A$。
        """;

    private const string BallastlessAnswer = "无砟轨道把钢轨固定在预制的混凝土轨道板上，轨道板下面是水泥沥青砂浆层和混凝土底座。它几乎不需要日常捣固维护，平顺性好，代价是建造成本高、出现沉降后很难调整。";

    private const string DistributedAnswer = "动力集中把电机都放在机车上，像一个车头拉着一串车厢；动力分散把电机分到多节车厢的转向架上。后者每根车轴都能出力，起动和制动更快，也更不容易打滑，所以 250 km/h 以上的列车几乎都采用动力分散。";

    private const string PantographAnswer = "受电弓是车顶上可升降的铰接框架，用弹簧或气囊把碳滑板压在接触线上。高速下它要在接触线的轻微起伏中保持稳定的接触压力，否则会产生电弧、磨损接触线。";

    private const string FirstRailwayQuestion = "中国第一条铁路是哪条？";

    private const string FirstRailwayAnswer = "通常认为是 1876 年英国商人修建的**吴淞铁路**，从上海到吴淞，全长约 15 公里。它运营一年多后被清政府买下拆除。中国人自己主持修建的第一条铁路是 1881 年的唐胥铁路。";

    private static readonly DateTimeOffset GalleryStart = new(2026, 3, 14, 1, 30, 0, TimeSpan.Zero);

    // ── Onboarding and exploring ─────────────────────────────────────────────

    [AvaloniaFact]
    public async Task G01_onboarding()
    {
        await using var scene = Scene.Create("g01");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await scene.App.StartAsync();
        var onboarding = Assert.IsType<OnboardingViewModel>(scene.App.Root.Current);

        // The guide (2026-11-17): what Mindweave is for, a model, the first workspace, then 新探索.
        await SettleAsync(window);
        Capture(window, "g01-onboarding");
        onboarding.NextCommand.Execute(null);
        await onboarding.Model!.Loaded;
        await SettleAsync(window);
        Capture(window, "g01a-onboarding-model");
        onboarding.Model.SelectedProvider = ViewModels.Pages.ProviderPresets.DeepSeek;
        await onboarding.SaveModelCommand.ExecuteAsync(null);
        await SettleAsync(window);
        Capture(window, "g01b-onboarding-model-refused");
        onboarding.LaterCommand.Execute(null);
        onboarding.Form.Name = "火车历史";
        await SettleAsync(window);
        Capture(window, "g01c-onboarding-workspace");
        await onboarding.Form.SubmitCommand.ExecuteAsync(null);
        await Assert.IsType<NewConversationViewModel>(scene.App.Workbench.CurrentPage).ModelNote!.Checked;
        await SettleAsync(window);
        Capture(window, "g01d-new-exploration-no-model");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G01e_workspace_prompt_after_the_guide()
    {
        // The last workspace deleted: only a workspace is asked for, without the guide.
        await using var scene = Scene.Create("g01e");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await scene.App.StartWithWorkspaceAsync("唯一");
        scene.App.ShellPage.DeleteWorkspaceCommand.Execute(null);
        var dialog = scene.App.Root.WorkspaceDeletion;
        await dialog.Loading;
        dialog.TypedName = "唯一";
        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.False(Assert.IsType<OnboardingViewModel>(scene.App.Root.Current).IsGuided);
        await SettleAsync(window);
        Capture(window, "g01e-workspace-prompt");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G02_explore_home_with_a_branch_tree_in_the_sidebar()
    {
        await using var scene = Scene.Create("g02");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        Assert.IsType<ExploreHomeViewModel>(scene.App.Workbench.CurrentPage);
        await SettleAsync(window);
        Capture(window, "g02-explore-home");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G03_new_conversation()
    {
        await using var scene = Scene.Create("g03");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        scene.App.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(scene.App.Workbench.CurrentPage);
        draft.Composer.Text = "蒸汽机车和内燃机车相比，热效率差多少？";
        await SettleAsync(window);
        Capture(window, "g03-new-conversation");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G04_conversation_with_a_long_answer_branch_entries_and_regenerate()
    {
        await using var scene = Scene.Create("g04");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.Root);
        Assert.Equal(3, page.Messages[1].Branches.Count);
        Assert.True(page.Messages[1].CanRetry);

        // The whole exchange fits: the question bubble above the long answer, regenerate and the branch entries below (scrolled to the end, the bubble stays in view).
        Scroller(window).ScrollToEnd();
        await SettleAsync(window);
        Capture(window, "g04-conversation");
        window.Close();
    }

    /// <summary>
    /// The context ring beside send (2026-11-17): the conversation's own figure, then near the limit with its sentence
    /// open as on hover, then too long (g04a, g04b, g04c; the states are set on the composer, the page's line is not).
    /// </summary>
    [AvaloniaFact]
    public async Task G04a_context_ring_states()
    {
        await using var scene = Scene.Create("g04a");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.Root);
        await UiWait.UntilAsync(() => page.ContextUsage is not null);
        await SettleAsync(window);
        Capture(window, "g04a-context-ring");

        // Hover, then a press: near the limit with every kind of part, and when it no longer fits.
        var button = Find<Button>(window, "ContextRingButton");
        (ContextUsageKind, int)[] parts =
        [
            (ContextUsageKind.Conversation, 21_300), (ContextUsageKind.Ancestors, 14_800), (ContextUsageKind.ContextSummaries, 2_400),
            (ContextUsageKind.Attachments, 8_900), (ContextUsageKind.Instructions, 1_500), (ContextUsageKind.Tools, 3_500),
        ];
        page.Composer.ContextUsage = new ContextUsage(52_400, 71_000, 61_000, true, false) { Parts = ContextUsage.Share(parts, 52_400) };
        ToolTip.SetIsOpen(button, true);
        await SettleAsync(window);
        Capture(window, "g04b-context-ring-near");
        ToolTip.SetIsOpen(button, false);
        button.Flyout!.ShowAt(button);
        await SettleAsync(window);
        Capture(window, "g04b2-context-ring-parts");
        button.Flyout!.Hide();

        page.Composer.ContextUsage = new ContextUsage(82_000, 82_000, 61_000, true, true) { Parts = ContextUsage.Share(parts, 82_000) };
        button.Flyout!.ShowAt(button);
        await SettleAsync(window);
        Capture(window, "g04c-context-ring-too-long");
        button.Flyout!.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task G05_second_level_branch_with_its_quote_and_breadcrumb()
    {
        await using var scene = Scene.Create("g05");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.Cable);
        Assert.Equal(2, page.Lineage.Count);
        Assert.NotNull(page.SourceQuote);
        Capture(window, "g05-branch-page");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G06_an_answer_streaming()
    {
        await using var scene = Scene.Create("g06");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.FirstRailway);
        var held = scene.App.Client.Hold("吴淞铁路被拆除的原因有好几层：\n\n1. **安全事故**：通车不久就轧死了一名行人，引起民众强烈不满；\n2. **主权问题**：", "铁路是英商未经正式许可修建的，");
        page.Composer.Text = "它为什么很快就被拆除了？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].Body.Length > 20);
        await SettleAsync(window);
        Scroller(window).ScrollToEnd();
        await SettleAsync(window);
        Capture(window, "g06-answer-streaming");

        held.Finish("清政府认为有损主权。");
        await UiWait.UntilAsync(() => page.Messages[3].State == MessageState.Complete);
        window.Close();
    }

    [AvaloniaFact]
    public async Task G07_a_failed_answer_with_its_error_and_retry()
    {
        await using var scene = Scene.Create("g07");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.FirstRailway);
        scene.App.Client.Fail(GenerationErrorKind.Network, "connection reset by peer", "唐胥铁路最初只有约 10 公里，起初甚至");
        page.Composer.Text = "唐胥铁路一开始用什么牵引？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].State == MessageState.Failed && page.Messages[3].CanRetry);
        await SettleAsync(window);
        Scroller(window).ScrollToEnd();
        await SettleAsync(window);
        Capture(window, "g07-answer-failed");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G08_storage_paused_notice()
    {
        await using var scene = Scene.Create("g08");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.FirstRailway);
        var held = scene.App.Client.Hold("唐胥铁路由开平矿务局修建，");
        page.Composer.Text = "唐胥铁路是谁修的？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var database = scene.App.Services.GetRequiredService<SqliteDatabase>();
        await StoragePauseUiTests.FillDiskAsync(database);
        held.Finish("用来把开平煤矿的煤运到胥各庄。" + new string('　', 70_000));
        await UiWait.UntilAsync(() => page.IsStorageBlocked && page.Messages.Count == 4 && page.Messages[3] is { State: MessageState.Complete }, timeoutMs: 10_000);
        page.Composer.Text = "后来延长到了哪里？";
        await SettleAsync(window);
        Scroller(window).Offset = default;
        await SettleAsync(window);
        Capture(window, "g08-storage-paused");

        await StoragePauseUiTests.FreeDiskAsync(database);
        await UiWait.UntilAsync(() => !page.IsStorageBlocked, timeoutMs: 15_000);
        window.Close();
    }

    // ── Summaries ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task G09_summary_current_version_unread()
    {
        await using var scene = Scene.Create("g09");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        var summary = await OpenSummaryAsync(scene.App, window, seed.Nodes[seed.Cable]);
        Assert.False(summary.ShownIsRead);
        Capture(window, "g09-summary-unread");

        // The same page with the export options open (the source-note choice sits with the export action).
        var export = Find<Button>(window, "ExportButton");
        export.Flyout!.ShowAt(export);
        await SettleAsync(window);
        Capture(window, "g09b-summary-export-options");
        export.Flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task G10_summary_candidate_beside_an_edited_current_version()
    {
        await using var scene = Scene.Create("g10");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        var summary = await OpenSummaryAsync(scene.App, window, seed.Nodes[seed.Gauge]);
        Assert.True(summary.HasCandidate);
        Assert.Equal(SummaryOrigin.Edited, summary.Shown!.Origin);
        Capture(window, "g10-summary-candidate");

        // Viewing the candidate itself: 采用 becomes the page's one primary action.
        summary.ShowCandidateCommand.Execute(null);
        await SettleAsync(window);
        Capture(window, "g10b-summary-candidate-viewed");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G11_summary_edit_mode()
    {
        await using var scene = Scene.Create("g11");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        var summary = await OpenSummaryAsync(scene.App, window, seed.Nodes[seed.HighSpeed]);
        await summary.BeginEditCommand.ExecuteAsync(null);
        summary.EditBody += "\n\n- 补充：京沪高铁试验段曾跑出 486.1 km/h。";
        await SettleAsync(window);
        Capture(window, "g11-summary-edit");
        await summary.DiscardEditCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task G12_summary_version_history_expanded()
    {
        await using var scene = Scene.Create("g12");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        var summary = await OpenSummaryAsync(scene.App, window, seed.Nodes[seed.Gauge]);
        Assert.Equal(3, summary.History.Count);
        Find<Expander>(window, "HistoryPanel").IsExpanded = true;
        await SettleAsync(window);
        Capture(window, "g12-summary-history");
        window.Close();
    }

    // ── Knowledge network ────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task G13_network_with_a_selected_node()
    {
        await using var scene = Scene.Create("g13");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(scene.App, window);
        Assert.Equal(8, network.Nodes.Count(n => n.HasSummary));
        network.Select(seed.Nodes[seed.Gauge]);
        await SettleAsync(window);
        var canvas = window.GetVisualDescendants().OfType<SummaryNetworkCanvas>().Single();
        Assert.True(canvas.Current.Zoom >= 0.8, $"default zoom {canvas.Current.Zoom:0.00}");
        var gauge = canvas.PaintNodes().Single(p => p.Node.Id == seed.Nodes[seed.Gauge]);
        Assert.All(canvas.PaintEdges(), e => Assert.False(SummaryNetworkGeometry.Crosses(e.From, e.To, gauge.LabelBounds), $"{gauge.LabelBounds} {e.From} {e.To}"));
        Capture(window, "g13-network");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G14_network_list_view()
    {
        await using var scene = Scene.Create("g14");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App, summaries: true);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(scene.App, window);
        network.ToggleViewCommand.Execute(null);
        Assert.True(network.IsListView);
        await SettleAsync(window);
        Capture(window, "g14-network-list");
        window.Close();
    }

    // ── Exit ─────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task G17_exit_task_prompt()
    {
        await using var scene = Scene.Create("g17");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.FirstRailway);
        var held = scene.App.Client.Hold("吴淞铁路的机车是英国制造的“先导号”，");
        page.Composer.Text = "吴淞铁路用的是什么机车？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        window.Close();
        Assert.True(scene.App.Root.Exit.IsTaskPromptOpen);
        await SettleAsync(window);
        Capture(window, "g17-exit-task-prompt");

        scene.App.Root.Exit.KeepRunningCommand.Execute(null);
        held.Finish("重约 1.5 吨。");
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].State == MessageState.Complete);
        window.Close();
    }

    [AvaloniaFact]
    public async Task G18_exit_failed()
    {
        var failing = new FailingDrafts();
        await using var scene = Scene.Create("g18", s => s.AddSingleton<IReadingStateStore>(sp => failing.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.FirstRailway);
        failing.Fail = true;
        page.Composer.Text = "吴淞铁路拆下来的铁轨去了哪里？";

        window.Close();
        await scene.App.Root.Exit.Running;
        Assert.True(scene.App.Root.Exit.IsFailed);
        await SettleAsync(window);
        Capture(window, "g18-exit-failed");

        failing.Fail = false;
        scene.App.Root.Exit.RetryCommand.Execute(null);
        await scene.App.Root.Exit.Running;
        window.Close();
    }

    // ── Archive and delete ───────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task G19_archived_page_with_a_nested_archived_tree()
    {
        await using var scene = Scene.Create("g19");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var conversations = scene.App.Services.GetRequiredService<ConversationService>();
        await conversations.ArchiveAsync(seed.Rainhill, CancellationToken.None);
        await conversations.ArchiveAsync(seed.Root, CancellationToken.None);
        await conversations.ArchiveAsync(seed.FirstRailway, CancellationToken.None);
        await UiWait.UntilAsync(() => scene.App.ShellPage.Conversations.ArchivedCount == 3);

        scene.App.ShellPage.Conversations.OpenArchivedCommand.Execute(null);
        var archived = Assert.IsType<ArchivedViewModel>(scene.App.Workbench.CurrentPage);
        await archived.Loaded;
        Assert.Equal(6, archived.Items.Count);
        await SettleAsync(window);
        Capture(window, "g19-archived-page");

        // Deleting asks first, in the danger colour.
        await archived.DeleteCommand.ExecuteAsync(archived.Items[^1]);
        await SettleAsync(window);
        Capture(window, "g19b-archived-delete-confirm");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G20_archived_conversation_read_only()
    {
        await using var scene = Scene.Create("g20");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        await scene.App.Services.GetRequiredService<ConversationService>().ArchiveAsync(seed.Root, CancellationToken.None);
        var page = await OpenConversationAsync(scene.App, window, seed.Root);
        Assert.True(page.IsArchived);
        Capture(window, "g20-archived-conversation");
        window.Close();
    }

    /// <summary>
    /// A delete is refused only while work runs in it now (requirements v0.12 §8.2): the plain question was asked, an
    /// answer started before the press, and the notice names the conversation.
    /// </summary>
    [AvaloniaFact]
    public async Task G21_delete_refused_while_an_answer_runs()
    {
        await using var scene = Scene.Create("g21");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.FirstRailway);
        await page.Lifecycle.DeleteCommand.ExecuteAsync(null);
        var held = scene.App.Client.Hold("吴淞铁路的机车是英国制造的“先导号”，");
        page.Composer.Text = "它的机车是哪里造的？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.Lifecycle.ConfirmCommand.ExecuteAsync(null);
        Assert.NotNull(page.Lifecycle.ArchiveNotice);
        await SettleAsync(window);
        Capture(window, "g21-delete-refused-running");
        held.Finish("1876 年运到上海。");
        await UiWait.UntilAsync(() => page.RunningAnswer is null);
        window.Close();
    }

    // ── Settings and recovery ────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task G22_settings_page()
    {
        await using var scene = Scene.Create("g22");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        Assert.IsType<SettingsHomeViewModel>(scene.App.Workbench.CurrentPage);
        await SettleAsync(window);
        Capture(window, "g22-settings");
        // The bottom of the page: the version line names the build (version, commit, its date) and the channel.
        window.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t.Text?.StartsWith(UiText.AppName, StringComparison.Ordinal) == true))
            .ScrollToEnd();
        await SettleAsync(window);
        Capture(window, "g22-settings-bottom");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G23_storage_details()
    {
        await using var scene = Scene.Create("g23");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await SettleAsync(window);
        var details = Find<Button>(window, "StorageDetailsButton");
        details.Command!.Execute(details.CommandParameter);
        Assert.IsType<StorageDetailsViewModel>(scene.App.Workbench.CurrentPage);
        await SettleAsync(window);
        Capture(window, "g23-storage-details");
        window.Close();
    }

    // S7 round 3 · settings: the provider presets, the fetched model list, the storage page with backups.

    [AvaloniaFact]
    public async Task G25_settings_model_list_after_fetching()
    {
        await using var scene = Scene.Create("g25");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        scene.App.Client.Models = ["deepseek-chat", "deepseek-reasoner", "deepseek-flash", "deepseek-coder"];
        await scene.App.Services.GetRequiredService<Application.Models.IModelSettings>().SaveAsync(
            new Application.Models.ModelSettingsInput("DeepSeek", "https://api.deepseek.com", "deepseek-chat", "sk-gallery", 128_000), CancellationToken.None);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(scene.App.Workbench.CurrentPage);
        await settings.Model.Loaded;
        await SettleAsync(window);
        await settings.Model.DiscoverCommand.ExecuteAsync(null);
        Assert.True(settings.Model.IsModelListOpen);
        await SettleAsync(window);
        Capture(window, "g25-settings-model-list");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G26_settings_provider_presets()
    {
        await using var scene = Scene.Create("g26");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await SettleAsync(window);
        Find<ComboBox>(window, "ProviderPicker").IsDropDownOpen = true;
        await SettleAsync(window);
        Capture(window, "g26-settings-provider-presets");
        window.Close();
    }

    /// <summary>设置 › 模型连接 with 高级设置 open: 工具调用 and 图片输入 as the preset fills them for a vision model.</summary>
    [AvaloniaFact]
    public async Task G26a_settings_model_capabilities()
    {
        await using var scene = Scene.Create("g26a");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        await scene.App.Services.GetRequiredService<Application.Models.IModelSettings>().SaveAsync(
            new Application.Models.ModelSettingsInput(UiText.ProviderZhipu, "https://open.bigmodel.cn/api/paas/v4", "glm-4.6v", "sk-gallery", 128_000), CancellationToken.None);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(scene.App.Workbench.CurrentPage);
        await settings.Model.Loaded;
        await SettleAsync(window);
        var toggle = Find<Avalonia.Controls.Primitives.ToggleButton>(window, "AdvancedToggle");
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.GetVisualDescendants().Contains(toggle));
        scroller.Offset = new Avalonia.Vector(0, Math.Max(0, scroller.Offset.Y + toggle.TranslatePoint(default, scroller)!.Value.Y - 360));
        await SettleAsync(window);
        Capture(window, "g26a-settings-model-capabilities-closed");
        settings.Model.IsAdvancedOpen = true;
        await SettleAsync(window);
        var section = Find<StackPanel>(window, "ModelCapabilities");
        scroller.Offset = new Avalonia.Vector(0, Math.Max(0, scroller.Offset.Y + section.TranslatePoint(default, scroller)!.Value.Y - 360));
        await SettleAsync(window);
        Capture(window, "g26a-settings-model-capabilities");

        // 输出上限 below the capabilities, then its "i" hovered.
        var limit = Find<TextBox>(window, "OutputLimit");
        settings.Model.OutputLimit = "8192";
        scroller.Offset = new Avalonia.Vector(0, Math.Max(0, scroller.Offset.Y + limit.TranslatePoint(default, scroller)!.Value.Y - 360));
        await SettleAsync(window);
        Capture(window, "g26b-settings-output-limit");
        var info = Find<Border>(window, "OutputLimitInfo");
        ToolTip.SetIsOpen(info, true);
        await SettleAsync(window);
        Capture(window, "g26c-settings-output-limit-tip");
        ToolTip.SetIsOpen(info, false);
        window.Close();
    }

    [AvaloniaFact]
    public async Task G27_storage_details_with_backups()
    {
        await using var scene = Scene.Create("g27");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        // 2026-11-12: every kind of row (a "恢复前" one, before a delete, an upgrade, one from a newer build that cannot be
        // restored, and the files an earlier restore put aside), the total size, and 恢复… / 删除… on each.
        await GalleryBackupsAsync(scene.App);
        var page = await OpenStorageDetailsAsync(scene.App, window);
        Assert.Equal(6, page.Rows.Count);
        await SettleAsync(window);
        Capture(window, "g27-storage-backups");
        window.Close();
    }

    // ── Restoring and removing backups (2026-11-12) ───────────────────────────

    /// <summary>
    /// The restore confirmation over the storage page: when and why the backup was made, that every workspace goes back,
    /// that a deleted workspace comes back with everything else, the "恢复前" copy, stopped work, and the upgrade after.
    /// </summary>
    [AvaloniaFact]
    public async Task G49_restore_confirmation()
    {
        await using var scene = Scene.Create("g49");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        await GalleryBackupsAsync(scene.App);
        var page = await OpenStorageDetailsAsync(scene.App, window);
        page.RestoreCommand.Execute(page.Rows.Single(r => r.Entry.Backup?.Reason == BackupReason.BeforeWorkspaceDelete));
        Assert.True(scene.App.Root.BackupRestore.IsOpen);
        await SettleAsync(window);
        Capture(window, "g49-restore-confirmation");
        window.Close();
    }

    /// <summary>Three rows checked: 删除所选 with the count and size, then its one confirmation with what may be lost.</summary>
    [AvaloniaFact]
    public async Task G49a_delete_selected_backups_confirmation()
    {
        await using var scene = Scene.Create("g49a");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        await GalleryBackupsAsync(scene.App);
        var page = await OpenStorageDetailsAsync(scene.App, window);
        foreach (var row in page.Rows.Where(r => r.Entry.Backup?.Reason is BackupReason.BeforeWorkspaceDelete or BackupReason.Migration || r.Entry.Leftover is not null))
        {
            row.IsChecked = true;
        }

        await SettleAsync(window);
        Capture(window, "g49a-storage-backups-selected");
        page.DeleteSelectedCommand.Execute(null);
        Assert.True(scene.App.Root.BackupDeletion.IsOpen);
        await SettleAsync(window);
        Capture(window, "g49a-delete-selected-confirmation");
        window.Close();
    }

    /// <summary>A real restore of a backup made before a conversation was deleted: the workbench again, with the notice.</summary>
    [AvaloniaFact]
    public async Task G49b_after_a_restore_with_its_notice()
    {
        await using var scene = Scene.Create("g49b");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        await scene.App.Services.GetRequiredService<IDatabaseBackup>().BackupAsync(BackupReason.BeforeConversationDelete, CancellationToken.None);
        var page = await OpenStorageDetailsAsync(scene.App, window);
        var oldRoot = scene.App.Root;
        page.RestoreCommand.Execute(Assert.Single(page.Rows));
        scene.App.Root.BackupRestore.RestoreCommand.Execute(null);
        await UiWait.UntilAsync(() => !ReferenceEquals(scene.App.Root, oldRoot), 15000);
        await scene.App.Session.LastRestore;
        Assert.NotNull(scene.App.ShellPage.RestoreNotice);
        await SettleAsync(window);
        Capture(window, "g49b-after-restore-notice");

        // The storage page afterwards: the "恢复前" copy is listed, marked, first.
        var details = await OpenStorageDetailsAsync(scene.App, window);
        Assert.Equal(BackupReason.BeforeRestore, details.Rows[0].Entry.Backup?.Reason);
        await SettleAsync(window);
        Capture(window, "g49b-storage-after-restore");
        window.Close();
    }

    /// <summary>Backups of every kind in a scene's data directory (empty files: only their names and sizes are shown).</summary>
    internal static async Task GalleryBackupsAsync(DesktopHarness app)
    {
        var backups = Path.Combine(app.Paths.DataDirectory, "backups");
        System.IO.Directory.CreateDirectory(backups);
        var current = MigrationCatalog.Default.LatestVersion;
        await File.WriteAllBytesAsync(Path.Combine(backups, $"mindweave-v{current}-20260313T221000000Z-before-restore.db"), new byte[1300 * 1024]);
        await File.WriteAllBytesAsync(Path.Combine(backups, $"mindweave-v{current - 1}-20260310T090500000Z-before-delete.db"), new byte[980 * 1024]);
        await File.WriteAllBytesAsync(Path.Combine(backups, $"mindweave-v{current}-20260308T181200000Z-before-conversation-delete.db"), new byte[860 * 1024]);
        await File.WriteAllBytesAsync(Path.Combine(backups, "mindweave-v7-20260301T081500000Z.db"), new byte[412 * 1024]);
        await File.WriteAllBytesAsync(Path.Combine(backups, $"mindweave-v{current + 1}-20260226T120000000Z.db"), new byte[1100 * 1024]);
        await File.WriteAllBytesAsync($"{app.Paths.DatabasePath}.replaced-20260212T193000000Z", new byte[(int)(2.4 * 1024 * 1024)]);
    }

    internal static async Task<StorageDetailsViewModel> OpenStorageDetailsAsync(DesktopHarness app, MainWindow window)
    {
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await SettleAsync(window);
        var details = Find<Button>(window, "StorageDetailsButton");
        details.Command!.Execute(details.CommandParameter);
        var page = Assert.IsType<StorageDetailsViewModel>(app.Workbench.CurrentPage);
        await SettleAsync(window);
        return page;
    }

    [AvaloniaFact]
    public async Task G24_recovery_after_a_failed_upgrade()
    {
        Assert.SkipWhen(ScreenshotDir is null, "Set MINDWEAVE_SCREENSHOT_DIR to render the gallery.");
        var directory = Scene.Directory("g24");
        await using (var before = DesktopHarness.Create(FixedTime, directory))
        {
            await before.StartWithWorkspaceAsync("火车历史");
        }

        var broken = new MigrationCatalog(
        [
            .. MigrationCatalog.Default.Migrations,
            new Migration(MigrationCatalog.Default.LatestVersion + 1, "broken", (_, _) => throw new InvalidOperationException("simulated upgrade failure")),
        ]);
        await using var scene = Scene.Create("g24", s => s.AddSingleton(broken), fresh: false);
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await scene.App.StartAsync();
        var recovery = Assert.IsType<RecoveryViewModel>(scene.App.Root.Current);
        Assert.Equal(DatabaseOpenStatus.MigrationFailed, recovery.Result.Status);
        await SettleAsync(window);
        Capture(window, "g24-recovery");

        // Choosing a backup states the consequence before anything happens.
        recovery.ChooseRestoreCommand.Execute(recovery.Backups[0]);
        await SettleAsync(window);
        Capture(window, "g24b-recovery-restore-confirm");
        SqliteConnection.ClearAllPools();
    }

    // ── S7 round 3 · conversation ────────────────────────────────────────────

    [AvaloniaFact]
    public async Task G30_selected_text_with_the_floating_branch_action()
    {
        await using var scene = Scene.Create("g30");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        await OpenConversationAsync(scene.App, window, seed.Root);
        var answer = Answers(window).Single();
        DragSelect(window, answer, "斯托克顿—达灵顿铁路");
        await SettleAsync(window);
        Assert.True(Find<Button>(window, "BranchButton").IsEffectivelyVisible);
        Capture(window, "g30-selection-branch-action");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G32_selection_on_the_answers_first_line_shows_the_action_over_the_question()
    {
        // The floating action lives in the window's overlay, so above the first line (over the question) it is not cut off.
        await using var scene = Scene.Create("g32");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        await OpenConversationAsync(scene.App, window, seed.Root);
        var answer = Answers(window).Single();
        var scroller = answer.FindAncestorOfType<ScrollViewer>()!;
        scroller.Offset = new Avalonia.Vector(0, Math.Max(0, scroller.Offset.Y + answer.TranslatePoint(default, scroller)!.Value.Y - 120));
        await SettleAsync(window);
        DragSelect(window, answer, "蒸汽动力");
        await SettleAsync(window);
        Assert.True(Find<Button>(window, "BranchButton").IsEffectivelyVisible);
        Capture(window, "g32-first-line-branch-action");
        window.Close();
    }

    /// <summary>
    /// After 返回来源 (2026-11-10): the source passage in the parent answer with its tint, its own text colour and the
    /// line along its bottom. The branch is made from a real selection, so the anchor restores as in the app.
    /// </summary>
    [AvaloniaFact]
    public async Task G42_source_passage_highlighted_after_returning_from_a_branch()
    {
        await using var scene = Scene.Create("g42");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var root = await OpenConversationAsync(scene.App, window, seed.Root);
        DragSelect(window, Answers(window).Single(), "斯托克顿—达灵顿铁路");
        await SettleAsync(window);
        Find<Button>(window, "BranchButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await UiWait.UntilAsync(() => scene.App.Workbench.CurrentPage is ConversationViewModel page && page.Id != root.Id);
        var child = (ConversationViewModel)scene.App.Workbench.CurrentPage!;
        await child.Loaded;
        await UiWait.UntilAsync(() => CurrentView(window).DataContext == child && CurrentView(window).Restored.IsCompleted);

        child.ReturnToSourceCommand.Execute(null);
        await UiWait.UntilAsync(() => scene.App.Workbench.CurrentPage is ConversationViewModel page && page.Id == root.Id);
        var back = (ConversationViewModel)scene.App.Workbench.CurrentPage!;
        await back.Loaded;
        await UiWait.UntilAsync(() => CurrentView(window).DataContext == back && CurrentView(window).Restored.IsCompleted);
        await SettleAsync(window);
        Assert.Null(back.SourceNotice);
        Assert.Contains(window.GetVisualDescendants(), v => v is Mindweave.Desktop.Markdown.SourceHighlightMark);
        Capture(window, "g42-source-highlight-after-return");
        window.Close();
    }

    private static void DragSelect(Window window, AnswerView answer, string quote)
    {
        var block = Mindweave.Desktop.Markdown.SourceAnchors.Blocks(answer.Renderer).First(b => b.LayoutText.Contains(quote, StringComparison.Ordinal));
        var start = block.LayoutText.IndexOf(quote, StringComparison.Ordinal);
        var end = start + quote.Length;
        var from = block.GetTextRangeBoundsInControl(start, 1)[0];
        var to = block.GetTextRangeBoundsInControl(end, 1)[0];
        var down = block.TranslatePoint(new Avalonia.Point(from.X + 1, from.Center.Y), window)!.Value;
        var up = block.TranslatePoint(new Avalonia.Point(to.X + 1, to.Center.Y), window)!.Value;
        window.MouseDown(down, Avalonia.Input.MouseButton.Left);
        window.MouseMove(up, Avalonia.Input.RawInputModifiers.LeftMouseButton);
        window.MouseUp(up, Avalonia.Input.MouseButton.Left);
    }

    [AvaloniaFact]
    public async Task G31_answer_copied_with_message_times()
    {
        await using var scene = Scene.Create("g31");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        await OpenConversationAsync(scene.App, window, seed.FirstRailway);
        var copy = window.GetVisualDescendants().OfType<CopyButton>().First(b => b.Name == "AnswerCopy" && b.IsEffectivelyVisible);
        var centre = copy.TranslatePoint(new Avalonia.Point(copy.Bounds.Width / 2, copy.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
        window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
        await UiWait.UntilAsync(() => copy.ShowsCopied);
        await SettleAsync(window);
        Capture(window, "g31-answer-copied");
        window.Close();
    }

    // ── Round 4 · conversation ─────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task G35_answer_generating_with_its_live_timer()
    {
        await using var scene = Scene.Create("g35");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.FirstRailway);
        var held = scene.App.Client.Hold("吴淞铁路被拆除的原因有好几层：\n\n1. **安全事故**：通车不久就轧死了一名行人，引起民众强烈不满；\n2. **主权问题**：");
        page.Composer.Text = "它为什么很快就被拆除了？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].IsStreaming);
        Time(scene.App).Advance(TimeSpan.FromSeconds(12) - (Time(scene.App).GetUtcNow() - page.Messages[3].StartedAt));
        // The page's one-second tick shows the moved clock.
        await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t is { Name: "StatusElapsed", IsEffectivelyVisible: true, Text: var shown } && (shown == UiText.Elapsed(TimeSpan.FromSeconds(12)) || shown == UiText.Elapsed(TimeSpan.FromSeconds(13)))), timeoutMs: 3000);
        Scroller(window).ScrollToEnd();
        await SettleAsync(window);
        Capture(window, "g35-answer-generating-timer");

        held.Finish("清政府认为有损主权。");
        await UiWait.UntilAsync(() => page.Messages[3] is { State: MessageState.Complete, IsLive: false });
        await SettleAsync(window);
        Scroller(window).ScrollToEnd();
        await SettleAsync(window);
        Capture(window, "g35b-answer-finished-duration");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G38_reasoning_model_thinking_with_its_live_timer()
    {
        await using var scene = Scene.Create("g38");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var page = await OpenConversationAsync(scene.App, window, seed.FirstRailway);
        var held = scene.App.Client.HoldThinking();
        page.Composer.Text = "它为什么很快就被拆除了？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].StatusText == UiText.ModelThinking);
        Time(scene.App).Advance(TimeSpan.FromSeconds(48) - (Time(scene.App).GetUtcNow() - page.Messages[3].StartedAt));
        await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t is { Name: "StatusElapsed", IsEffectivelyVisible: true, Text: var shown } && (shown == UiText.Elapsed(TimeSpan.FromSeconds(48)) || shown == UiText.Elapsed(TimeSpan.FromSeconds(49)))), timeoutMs: 3000);
        Scroller(window).ScrollToEnd();
        await SettleAsync(window);
        Capture(window, "g38-answer-thinking");

        held.Finish("清政府认为有损主权。");
        await UiWait.UntilAsync(() => page.Messages[3] is { State: MessageState.Complete, IsLive: false });
        window.Close();
    }

    [AvaloniaFact]
    public async Task G36_long_conversation_with_branch_marks_in_the_margin()
    {
        await using var scene = Scene.Create("g36");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        var app = scene.App;
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var time = Time(app);

        // One long conversation of five exchanges, each answer taking a while; branches from the first, third and fifth.
        var held = app.Client.Hold(RootAnswer);
        var started = await conversations.StartAsync(app.Workbench.CurrentWorkspace!.Id, RootQuestion, CancellationToken.None);
        await conversations.RenameAsync(started.Conversation, "蒸汽机车的演进", CancellationToken.None);
        await TakesAsync(held, time, 14, conversations, started.Conversation);
        foreach (var (question, answer, seconds) in new[]
        {
            ("雨山试验具体比了什么？", RainhillAnswer, 9),
            ("固定蒸汽机拉缆绳是怎样运作的？", CableAnswer, 7),
            ("为什么偏偏是 1435 毫米？", GaugeAnswer, 11),
            (HighSpeedQuestion, HighSpeedAnswer, 16),
        })
        {
            held = app.Client.Hold(answer);
            await conversations.AskAsync(started.Conversation, question, CancellationToken.None);
            await TakesAsync(held, time, seconds, conversations, started.Conversation);
        }

        var answers = (await conversations.LoadAsync(started.Conversation, CancellationToken.None))!.Messages.Where(m => m.Role == MessageRole.Assistant).ToList();
        foreach (var (index, quote) in new[] { (0, "多管锅炉"), (0, "标准轨距"), (2, "缆绳磨损快"), (4, "无砟轨道") })
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var branch = await conversations.CreateBranchAsync(started.Conversation, new BranchSelection(answers[index].Id, 0, quote, "{\"v\":1}", 1, "gallery"), CancellationToken.None);
            Assert.True(branch.Accepted, branch.Rejection?.ToString());
        }

        await OpenConversationAsync(app, window, started.Conversation);
        Scroller(window).ScrollToEnd();
        await SettleAsync(window);
        var marks = window.GetVisualDescendants().OfType<BranchMarks>().Single();
        Assert.Equal(3, marks.Marks.Count);
        Capture(window, "g36-branch-marks");

        // Clicking the first mark brings the first answer's branch list into view.
        var mark = marks.Marks[0].Mark;
        var centre = mark.TranslatePoint(new Avalonia.Point(mark.Bounds.Width / 2, mark.Bounds.Height / 2), window)!.Value;
        window.MouseMove(centre);
        window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
        window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
        await SettleAsync(window);
        Capture(window, "g36b-branch-mark-revealed");
        window.Close();
    }

    private static ManualTime Time(DesktopHarness app) => (ManualTime)app.Services.GetRequiredService<TimeProvider>();

    /// <summary>Lets a held answer run for <paramref name="seconds"/> on the gallery clock, then finish.</summary>
    private static async Task TakesAsync(Mindweave.Testing.FakeChatClient.HeldReply held, ManualTime time, int seconds, ConversationService conversations, ConversationId conversation)
    {
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(seconds));
        held.Release.TrySetResult();
        await FinishedAsync(conversations, conversation);
    }

    // ── Multi-select and deleting with a stated scope (requirements v0.12 §8.2) ─────────────────────────

    /// <summary>
    /// The sidebar with three conversations selected (one root with its branches, one branch, one lone conversation)
    /// while the root is open, and the bar at the bottom (g37); the same list in the 选择 mode with its checkboxes (g37a).
    /// </summary>
    [AvaloniaFact]
    public async Task G37_sidebar_selection_and_its_bar()
    {
        await using var scene = Scene.Create("g37");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        await OpenConversationAsync(scene.App, window, seed.HighSpeed);
        var list = scene.App.ShellPage.Conversations;
        foreach (var id in new[] { seed.Root, seed.Cable, seed.FirstRailway })
        {
            list.Toggle(list.Items.Single(i => i.Id == id));
        }

        Assert.Equal(3, list.SelectedCount);
        await SettleAsync(window);
        Capture(window, "g37-sidebar-selection-bar");

        list.ClearSelection();
        list.ToggleSelectionModeCommand.Execute(null);
        list.OpenCommand.Execute(list.Items.Single(i => i.Id == seed.Gauge));
        list.OpenCommand.Execute(list.Items.Single(i => i.Id == seed.Wheels));
        await SettleAsync(window);
        Capture(window, "g37a-sidebar-selection-mode");
        window.Close();
    }

    /// <summary>The confirmation for deleting a selection that takes branches and summaries along: counts, 改为归档, 删除.</summary>
    [AvaloniaFact]
    public async Task G37b_cascade_delete_confirmation_with_counts()
    {
        await using var scene = Scene.Create("g37b");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        await OpenConversationAsync(scene.App, window, seed.Root);
        var list = scene.App.ShellPage.Conversations;
        list.Toggle(list.Items.Single(i => i.Id == seed.Root));
        list.Toggle(list.Items.Single(i => i.Id == seed.Rainhill));
        list.Toggle(list.Items.Single(i => i.Id == seed.HighSpeed));
        list.DeleteSelectedCommand.Execute(null);
        var dialog = scene.App.Root.ConversationDeletion;
        await dialog.Loading;
        Assert.True(dialog.IsCascade);
        await SettleAsync(window);
        Capture(window, "g37b-cascade-delete-confirm");
        window.Close();
    }

    // ── The sidebar row's context menu (2026-11-10) ─────────────────────────

    /// <summary>
    /// A right click on a branch row while another conversation is open: the page toolbar's actions, 删除… in the danger
    /// colour (g45); the same menu on a row whose branch is answering, archive and delete disabled (g45a); the row's
    /// title turned into its rename box (g45b); a refused summary named above the sidebar's foot (g45c).
    /// </summary>
    [AvaloniaFact]
    public async Task G45_sidebar_row_context_menu()
    {
        await using var scene = Scene.Create("g45");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        // The answer the disabled-menu shot holds would pop a new-answer card into the later shots (that card is g44).
        await scene.App.Services.GetRequiredService<INewsAlerts>().SetInWorkspaceAsync(false, CancellationToken.None);
        await OpenConversationAsync(scene.App, window, seed.HighSpeed);
        var list = scene.App.ShellPage.Conversations;

        RightClickRow(window, seed.Gauge);
        await SettleAsync(window);
        Capture(window, "g45-sidebar-row-menu");
        CloseRowMenu(window, seed.Gauge);

        var conversations = scene.App.Services.GetRequiredService<ConversationService>();
        var held = scene.App.Client.Hold("雨山试验之后");
        await conversations.AskAsync(seed.Cable, "缆索牵引后来还用在哪里？", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == seed.Rainhill).IsBusyBelow);
        RightClickRow(window, seed.Rainhill);
        await SettleAsync(window);
        Capture(window, "g45a-sidebar-row-menu-disabled");
        CloseRowMenu(window, seed.Rainhill);
        held.Finish("在陡坡上保留。");
        await FinishedAsync(conversations, seed.Cable);

        list.BeginRenameRowCommand.Execute(list.Items.Single(i => i.Id == seed.Wheels));
        await SettleAsync(window);
        Capture(window, "g45b-sidebar-row-rename");
        // Left as a click elsewhere leaves it (Esc would give the row the keyboard focus and its ring, which is not this scene).
        list.Items.Single(i => i.Id == seed.Wheels).IsRenaming = false;

        // A candidate waits on the gauge summary (seed): another summary is refused until it is adopted or discarded.
        await list.SummarizeRowCommand.ExecuteAsync(list.Items.Single(i => i.Id == seed.Gauge));
        Assert.NotNull(list.RowNotice);
        await SettleAsync(window);
        Capture(window, "g45c-sidebar-row-notice");
        window.Close();
    }

    /// <summary>
    /// Keyboard focus (2026-11-10 polish): the ring drawn inside a sidebar row (g47), inside a quiet toolbar button
    /// (g47a), and outside the settings page's filled primary button (g47b). Focus is moved as Tab moves it.
    /// </summary>
    [AvaloniaFact]
    public async Task G47_keyboard_focus_ring()
    {
        await using var scene = Scene.Create("g47");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        await OpenConversationAsync(scene.App, window, seed.HighSpeed);

        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is ConversationItemViewModel item && item.Id == seed.Gauge && b.Classes.Contains("nav"));
        row.Focus(NavigationMethod.Tab);
        await SettleAsync(window);
        Capture(window, "g47-focus-ring-sidebar-row");

        Find<Button>(window, "RenameButton").Focus(NavigationMethod.Tab);
        await SettleAsync(window);
        Capture(window, "g47a-focus-ring-toolbar-button");

        // The settings page's 保存, the filled primary level.
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await SettleAsync(window);
        var save = Find<Button>(window, "SaveModel");
        save.BringIntoView();
        save.Focus(NavigationMethod.Tab);
        await SettleAsync(window);
        Capture(window, "g47b-focus-ring-primary-button");
        window.Close();
    }

    internal static void RightClickRow(MainWindow window, ConversationId id)
    {
        window.UpdateLayout();
        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is ConversationItemViewModel item && item.Id == id && b.Classes.Contains("nav"));
        var title = row.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Single(t => t.Name == "RowTitle");
        var point = title.TranslatePoint(new Point(24, title.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(row.ContextMenu!.IsOpen);
    }

    internal static void CloseRowMenu(MainWindow window, ConversationId id)
    {
        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is ConversationItemViewModel item && item.Id == id && b.Classes.Contains("nav"));
        row.ContextMenu!.Close();
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(new Point(700, 700));
    }

    // ── Shell: sidebar resize and collapse (S7 round 3) ─────────────────────

    [AvaloniaFact]
    public async Task G28_shell_sidebar_collapsed_while_a_conversation_needs_attention()
    {
        await using var scene = Scene.Create("g28");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var conversations = scene.App.Services.GetRequiredService<ConversationService>();
        scene.App.Client.Fail(GenerationErrorKind.Network, "connection reset", "吴淞铁路");
        await conversations.AskAsync(seed.FirstRailway, "吴淞铁路拆下来的铁轨去了哪里？", CancellationToken.None);
        await UiWait.UntilAsync(() => scene.App.ShellPage.Conversations.HasAttention);
        await OpenConversationAsync(scene.App, window, seed.Gauge);
        scene.App.ShellPage.ToggleSidebarCommand.Execute(null);
        await SettleAsync(window);
        Capture(window, "g28-shell-sidebar-collapsed");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G29_shell_sidebar_widened()
    {
        await using var scene = Scene.Create("g29");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        scene.App.ShellPage.SidebarWidth = 340;
        await SettleAsync(window);
        Capture(window, "g29-shell-sidebar-widened");

        // The widest sidebar in the narrowest window: the sidebar gives way, the conversation keeps its minimum width.
        await OpenConversationAsync(scene.App, window, seed.Root);
        scene.App.ShellPage.SidebarWidth = 420;
        window.Width = 720;
        await SettleAsync(window);
        Capture(window, "g29b-shell-narrow-window");
        window.Close();
    }

    // ── Round 4 · topic-delete ───────────────────────────────────────────────

    /// <summary>
    /// 设置 › 工作区 (g33a; 2026-11-10: every workspace, where the delete entries now are), the delete-a-workspace
    /// confirmation opened from its row with the name typed (g33), the dialog refusing while an answer runs (g33b), and
    /// after the delete the storage page with its backup (g33c).
    /// </summary>
    [AvaloniaFact]
    public async Task G33_delete_workspace_confirmation()
    {
        await using var scene = Scene.Create("g33");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);

        // Opened from 设置 › 工作区, behind the dialog.
        scene.App.ShellPage.ManageWorkspacesCommand.Execute(null);
        var workspaces = Assert.IsType<WorkspacesViewModel>(scene.App.Workbench.CurrentPage);
        await workspaces.Loaded;
        await SettleAsync(window);
        Capture(window, "g33a-settings-workspace-section");
        workspaces.Rows[0].DeleteCommand.Execute(null);
        var dialog = scene.App.Root.WorkspaceDeletion;
        await dialog.Loading;
        dialog.TypedName = "火车历史";
        await SettleAsync(window);
        Capture(window, "g33-delete-workspace-confirm");
        dialog.CancelCommand.Execute(null);

        var held = scene.App.Client.Hold("吴淞铁路的机车是英国制造的“先导号”，");
        var page = await OpenConversationAsync(scene.App, window, seed.FirstRailway);
        page.Composer.Text = "吴淞铁路用的是什么机车？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scene.App.ShellPage.DeleteWorkspaceCommand.Execute(null);
        await dialog.Loading;
        Assert.NotNull(dialog.Problem);
        dialog.TypedName = "火车历史"; // the exact name, yet 删除 stays disabled while the notice stands
        Assert.False(dialog.CanDelete);
        await SettleAsync(window);
        Capture(window, "g33b-delete-workspace-refused");

        dialog.CancelCommand.Execute(null);
        held.Finish("重约 1.5 吨。");
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].State == MessageState.Complete);

        // Deleted for real: the workbench moves to the other workspace and the storage page lists the backup made first.
        var other = await scene.App.Services.GetRequiredService<WorkspaceService>().CreateAsync(WorkspaceName.FromStored("摄影史"), CancellationToken.None);
        scene.App.Workbench.Workspaces.Add(other);
        scene.App.ShellPage.DeleteWorkspaceCommand.Execute(null);
        await dialog.Loading;
        dialog.TypedName = "火车历史";
        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(other.Id, scene.App.Workbench.CurrentWorkspace!.Id);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await SettleAsync(window);
        var details = Find<Button>(window, "StorageDetailsButton");
        details.Command!.Execute(details.CommandParameter);
        Assert.Single(Assert.IsType<StorageDetailsViewModel>(scene.App.Workbench.CurrentPage).Rows);
        await SettleAsync(window);
        Capture(window, "g33c-storage-backup-before-delete");
        window.Close();
    }

    // ── New answers in another workspace (2026-11-10) ───────────────────────

    [AvaloniaFact]
    public async Task G41_workspace_picker_shows_new_answers_elsewhere()
    {
        // The hints stand still while captured (a standing clock), so none closes itself under a slow run.
        await using var scene = Scene.Create("g41", s => s.AddSingleton(new WorkspaceNewsOptions { Time = new ManualTime() }));
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var conversations = scene.App.Services.GetRequiredService<ConversationService>();
        var held = scene.App.Client.Hold("轨距之争");
        await conversations.AskAsync(seed.Gauge, "后来为什么统一成标准轨？", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scene.App.ShellPage.NewWorkspace.Name = "城市交通";
        await scene.App.ShellPage.NewWorkspace.SubmitCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => scene.App.Workbench.CurrentWorkspace?.Name.Value == "城市交通");
        held.Finish("以标准轨为主。");
        var news = scene.App.ShellPage.WorkspaceNews!;
        await UiWait.UntilAsync(() => news.HasNews && news.Toasts.Count == 1);
        await NewsSettledAsync(window);
        Capture(window, "g41-workspace-news");

        scene.App.ShellPage.IsWorkspaceMenuOpen = true;
        await SettleAsync(window);
        Capture(window, "g41b-workspace-news-list");
        scene.App.ShellPage.IsWorkspaceMenuOpen = false;

        // Reading a conversation in 城市交通 meanwhile, so the hints are seen over a page with its composer.
        scene.App.Client.Reply("1863 年，伦敦。");
        scene.App.Client.Titles.Replies.Enqueue("伦敦地铁");
        var local = await conversations.StartAsync(scene.App.Workbench.CurrentWorkspace!.Id, "第一条地铁在哪里？", CancellationToken.None);
        await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(local.Conversation));
        await OpenConversationAsync(scene.App, window, local.Conversation);

        // A second conversation there, and a third workspace: the first hint counts two, a second stacks under it; the badge says 3.
        scene.App.Client.Reply("约 1435 毫米。");
        await conversations.AskAsync(seed.Wheels, "标准轨距是多少？", CancellationToken.None);
        await UiWait.UntilAsync(() => news.Toasts.Count == 1 && news.Toasts[0].Count == 2);
        var flights = await scene.App.Services.GetRequiredService<WorkspaceService>().CreateAsync(WorkspaceName.FromStored("航空史"), CancellationToken.None);
        scene.App.Workbench.Workspaces.Add(flights);
        scene.App.Client.Reply("1903 年，莱特兄弟。");
        await conversations.StartAsync(flights.Id, "第一架飞机是谁造的？", CancellationToken.None);
        await UiWait.UntilAsync(() => news.Toasts.Count == 2 && news.Count == 3);
        await NewsSettledAsync(window);
        Capture(window, "g41c-workspace-news-stacked");
        window.Close();

        // Captured after the badge's one pulse, so the picture never catches it half-way.
        async Task NewsSettledAsync(MainWindow shown)
        {
            var shell = shown.GetVisualDescendants().OfType<ShellView>().Single();
            await UiWait.UntilAsync(() => !shell.IsPulsing);
            await SettleAsync(shown);
        }
    }

    [AvaloniaFact]
    public async Task G44_new_answer_hints_for_a_conversation_here_and_for_another_workspace()
    {
        // As in g41, the hints stand still while captured.
        await using var scene = Scene.Create("g44", s => s.AddSingleton(new WorkspaceNewsOptions { Time = new ManualTime() }));
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
        var conversations = scene.App.Services.GetRequiredService<ConversationService>();
        var news = scene.App.ShellPage.WorkspaceNews!;
        var city = await scene.App.Services.GetRequiredService<WorkspaceService>().CreateAsync(WorkspaceName.FromStored("城市交通"), CancellationToken.None);
        scene.App.Workbench.Workspaces.Add(city);

        // Reading the root; a branch of it gets a new answer meanwhile, then a conversation in 城市交通 does.
        await OpenConversationAsync(scene.App, window, seed.Root);
        scene.App.Client.Reply("多数国家用 1435 毫米，俄罗斯用宽轨，日本早期用窄轨。");
        await conversations.AskAsync(seed.Gauge, "其他国家用什么轨距？", CancellationToken.None);
        await UiWait.UntilAsync(() => news.Toasts.Count == 1 && news.Toasts[0].Conversation == seed.Gauge);
        scene.App.Client.Reply("1863 年，伦敦。");
        await conversations.StartAsync(city.Id, "第一条地铁在哪里？", CancellationToken.None);
        await UiWait.UntilAsync(() => news.Toasts.Count == 2 && news.Count == 1);
        var shell = window.GetVisualDescendants().OfType<ShellView>().Single();
        await UiWait.UntilAsync(() => !shell.IsPulsing);
        await SettleAsync(window);
        Capture(window, "g44-news-hints-here-and-elsewhere");
        window.Close();
    }

    /// <summary>
    /// The 提示 section: the open workspace's hints (on, the default), the sound (chosen here), and the workspace names
    /// prepared in the background (2026-11-11, on, the default).
    /// </summary>
    [AvaloniaFact]
    public async Task G43_settings_news_sound()
    {
        await using var scene = Scene.Create("g43");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(scene.App.Workbench.CurrentPage);
        await settings.Loaded;
        settings.PlaysNewsSound = true;
        await settings.Saving;
        await SettleAsync(window);
        Capture(window, "g43-settings-news-sound");
        window.Close();
    }

    // ── Workspaces in one place (2026-11-10) ────────────────────────────────

    /// <summary>
    /// The switcher closed (g46), its menu opened from the keyboard with the open workspace checked (g46a), the inline
    /// new-workspace form (g46b), the settings home's 工作区 entry (g46f), 设置 › 工作区 with every workspace and its
    /// counts (g46c), a row being renamed with a refused name (g46d) and the page's own new-workspace form (g46e).
    /// </summary>
    [AvaloniaFact]
    public async Task G46_workspace_switcher_and_the_workspaces_page()
    {
        await using var scene = Scene.Create("g46");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        var service = scene.App.Services.GetRequiredService<WorkspaceService>();
        var conversations = scene.App.Services.GetRequiredService<ConversationService>();
        foreach (var name in new[] { "城市交通", "摄影史：从银版到数码" })
        {
            scene.App.Workbench.Workspaces.Add(await service.CreateAsync(WorkspaceName.FromStored(name), CancellationToken.None));
        }

        // Something in 城市交通 to count; seen at once, so no hint or badge stands in the shots.
        var city = scene.App.Workbench.Workspaces[1];
        scene.App.Client.Reply("1863 年，伦敦。");
        var subway = await conversations.StartAsync(city.Id, "第一条地铁在哪里？", CancellationToken.None);
        await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(subway.Conversation));
        var marks = scene.App.Services.GetRequiredService<ConversationMarksService>();
        await UiWait.UntilAsync(() => marks.IsUnread(subway.Conversation));
        marks.MarkSeen(subway.Conversation);
        var news = scene.App.ShellPage.WorkspaceNews!;
        await UiWait.UntilAsync(() => !news.HasNews && news.Toasts.Count == 0);
        await OpenConversationAsync(scene.App, window, seed.Root);
        await SettleAsync(window);
        Capture(window, "g46-workspace-switcher");

        var picker = Part<Button>(window, "WorkspacePicker");
        picker.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        await UiWait.UntilAsync(() => scene.App.ShellPage.IsWorkspaceMenuOpen);
        await SettleAsync(window);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        await SettleAsync(window);
        Capture(window, "g46a-workspace-menu");

        UiInput.Click(window, Find<Button>(window, "NewWorkspaceEntry"));
        await SettleAsync(window);
        scene.App.ShellPage.NewWorkspace.Name = "航空史";
        await SettleAsync(window);
        Capture(window, "g46b-new-workspace-form");
        scene.App.ShellPage.CancelCreateWorkspaceCommand.Execute(null);

        // The settings home's 工作区 entry, with how many there are.
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await Assert.IsType<SettingsHomeViewModel>(scene.App.Workbench.CurrentPage).Loaded;
        await SettleAsync(window);
        window.GetVisualDescendants().OfType<SettingsHomeView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
        await SettleAsync(window);
        Capture(window, "g46f-settings-workspaces-entry");

        scene.App.ShellPage.ManageWorkspacesCommand.Execute(null);
        var page = Assert.IsType<WorkspacesViewModel>(scene.App.Workbench.CurrentPage);
        await page.Loaded;
        await SettleAsync(window);
        Capture(window, "g46c-workspaces-page");

        page.Rows[1].BeginRenameCommand.Execute(null);
        page.Rows[1].RenameText = "   ";
        await page.Rows[1].CommitRenameCommand.ExecuteAsync(null);
        await SettleAsync(window);
        Capture(window, "g46d-workspace-rename");
        page.Rows[1].CancelRenameCommand.Execute(null);

        page.BeginCreateCommand.Execute(null);
        page.NewWorkspace.Name = "航空史";
        await SettleAsync(window);
        Capture(window, "g46e-workspaces-page-new");
        window.Close();
    }

    /// <summary>
    /// Renaming a workspace with names suggested from its conversations (2026-11-11; reworked the same day by the user's
    /// decision): the first name offered dimly after the selected name, with its Tab hint, and 换一批 (g48); 更多建议 open,
    /// the second name picked into the box and still hovered (g48c); 换一批 running, with 停止 (g48a); a failed request with
    /// 重试, the names offered before still there (g48b).
    /// </summary>
    [AvaloniaFact]
    public async Task G48_workspace_rename_with_suggested_names()
    {
        await using var scene = Scene.Create("g48");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App, summaries: true);
        var service = scene.App.Services.GetRequiredService<WorkspaceService>();
        foreach (var name in new[] { "城市交通", "摄影史：从银版到数码" })
        {
            scene.App.Workbench.Workspaces.Add(await service.CreateAsync(WorkspaceName.FromStored(name), CancellationToken.None));
        }

        scene.App.ShellPage.ManageWorkspacesCommand.Execute(null);
        var page = Assert.IsType<WorkspacesViewModel>(scene.App.Workbench.CurrentPage);
        await page.Loaded;
        var row = page.Rows[0];
        row.BeginRenameCommand.Execute(null);
        await page.KeptNamesRead;
        await SettleAsync(window);
        scene.App.Client.WorkspaceNames.Replies.Enqueue("火车的演进\n蒸汽机车与铁路\n铁路技术史");
        row.SuggestNamesCommand.Execute(null);
        await page.Suggesting;
        var box = window.GetVisualDescendants().OfType<WorkspacesView>().Single().GetVisualDescendants().OfType<GhostTextBox>().First(b => ReferenceEquals(b.DataContext, row));
        box.Focus(NavigationMethod.Tab);
        await UiWait.UntilAsync(() => box.IsFocused && box.ShowsGhost);
        await SettleAsync(window);
        Capture(window, "g48-workspace-name-suggestions");

        row.ToggleChipsCommand.Execute(null);
        await SettleAsync(window);
        var chips = window.GetVisualDescendants().OfType<WorkspacesView>().Single().GetVisualDescendants().OfType<Button>().Where(b => b.Name == "SuggestionChip").ToList();
        Assert.Equal(3, chips.Count);
        UiInput.Click(window, chips[1], hover: true);
        Assert.Equal("蒸汽机车与铁路", row.RenameText);
        await SettleAsync(window);
        Capture(window, "g48c-workspace-name-suggestion-chips");
        row.ToggleChipsCommand.Execute(null);

        var held = scene.App.Client.WorkspaceNames.Hold();
        row.SuggestNamesCommand.Execute(null);
        await UiWait.UntilAsync(() => row.IsSuggesting);
        await SettleAsync(window);
        Capture(window, "g48a-workspace-name-suggesting");
        row.StopSuggestingNamesCommand.Execute(null);
        scene.App.Client.WorkspaceNames.Gate = null;
        held.TrySetResult();
        await page.Suggesting;

        scene.App.Client.WorkspaceNames.FailNext(GenerationErrorKind.Network);
        row.SuggestNamesCommand.Execute(null);
        await page.Suggesting;
        await SettleAsync(window);
        Capture(window, "g48b-workspace-name-suggestion-failed");
        window.Close();
    }

    // ── Sidebar status (S7 round 4) ─────────────────────────────────────────

    [AvaloniaFact]
    public async Task G34_sidebar_status_spinner_unread_and_attention()
    {
        await using var scene = Scene.Create("g34");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);

        // The sidebar's own marks, without the hints the answers below would pop up (those are in g44).
        await scene.App.Services.GetRequiredService<INewsAlerts>().SetInWorkspaceAsync(false, CancellationToken.None);
        var conversations = scene.App.Services.GetRequiredService<ConversationService>();
        var list = scene.App.ShellPage.Conversations;
        await OpenConversationAsync(scene.App, window, seed.Root);

        // New answers while the root is on screen: two branches get the unread dot.
        foreach (var (id, question) in new[] { (seed.Gauge, "其他国家用什么轨距？"), (seed.Wheels, "动轮最大能做多大？") })
        {
            scene.App.Client.Reply("这是一个新的回答。");
            await conversations.AskAsync(id, question, CancellationToken.None);
            await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == id).IsUnread);
        }

        // Collapsed with only news: the expand button's accent dot.
        scene.App.ShellPage.ToggleSidebarCommand.Execute(null);
        await SettleAsync(window);
        Capture(window, "g34b-sidebar-collapsed-unread");
        scene.App.ShellPage.ToggleSidebarCommand.Execute(null);

        // One answer fails, one is still being written.
        scene.App.Client.Fail(GenerationErrorKind.Network, "connection reset", "吴淞铁路");
        await conversations.AskAsync(seed.FirstRailway, "吴淞铁路拆下来的铁轨去了哪里？", CancellationToken.None);
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == seed.FirstRailway).NeedsAttention);
        var held = scene.App.Client.Hold("受电弓的碳滑板");
        await conversations.AskAsync(seed.HighSpeed, "受电弓多久更换一次？", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => list.Items.Single(i => i.Id == seed.HighSpeed).IsGenerating);
        await SettleAsync(window);
        Capture(window, "g34-sidebar-status");

        held.Finish("通常几个月。");
        await UiWait.UntilAsync(() => !conversations.HasUnfinishedAnswer(seed.HighSpeed));
        window.Close();
    }

    // ── Usage statistics (2026-11-12) ─────────────────────────────────────────

    /// <summary>The statistics' "today" and zone in the gallery: the gallery's own day, in a fixed UTC+8.</summary>
    private static readonly DateTimeOffset StatisticsNow = new(2026, 3, 14, 18, 0, 0, TimeSpan.FromHours(8));

    private static readonly TimeZoneInfo StatisticsZone = TimeZoneInfo.CreateCustomTimeZone("Gallery/UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    internal static void FixedStatistics(IServiceCollection services) =>
        services.AddSingleton(new UsageStatisticsOptions { Time = new ManualTime(StatisticsNow), Zone = StatisticsZone });

    /// <summary>
    /// 设置 › 使用统计 for all workspaces, by day: the totals, a bar hovered with its numbers, by model and by purpose,
    /// the activity counts and milestones (g50, a tall window so the whole page is in one frame).
    /// </summary>
    [AvaloniaFact]
    public async Task G50_usage_statistics()
    {
        await using var scene = Scene.Create("g50", FixedStatistics);
        var window = scene.App.ShowWindow(1100, 2290, Theme);
        await SeedStatisticsAsync(scene.App);
        var (page, view) = await OpenStatisticsAsync(scene.App, window);
        var chart = Find<UsageChart>(view, "UsageChart");
        window.MouseMove(chart.TranslatePoint(chart.SlotCentre(26), window)!.Value);
        await SettleAsync(window);
        Assert.Equal(26, chart.ActiveIndex);
        Assert.True(page.HasUsage);
        Capture(window, "g50-usage-statistics");
        window.Close();
    }

    /// <summary>By month, with the table instead of the chart (g50a).</summary>
    [AvaloniaFact]
    public async Task G50a_usage_statistics_by_month_as_a_table()
    {
        await using var scene = Scene.Create("g50a", FixedStatistics);
        var window = scene.App.ShowWindow(1100, 1000, Theme);
        await SeedStatisticsAsync(scene.App);
        var (page, view) = await OpenStatisticsAsync(scene.App, window);
        UiInput.Click(window, Find<RadioButton>(view, "PeriodMonthly"));
        await page.Loaded;
        UiInput.Click(window, Find<CheckBox>(view, "TableToggle"));
        await SettleAsync(window);
        Assert.Equal(12, page.Bars.Count);
        Capture(window, "g50a-usage-statistics-monthly-table");
        window.Close();
    }

    /// <summary>
    /// Unit prices: two models priced with their costs and the total, the currency, and the third model's editor open
    /// with a refused value and its message (g50b, scrolled to 按模型).
    /// </summary>
    [AvaloniaFact]
    public async Task G50b_usage_statistics_with_prices()
    {
        await using var scene = Scene.Create("g50b", FixedStatistics);
        var window = scene.App.ShowWindow(1100, 1000, Theme);
        await SeedStatisticsAsync(scene.App);
        var (page, view) = await OpenStatisticsAsync(scene.App, window);
        foreach (var (row, input, output) in new[] { (page.Models[0], "1", "4"), (page.Models[1], "2", "8") })
        {
            row.EditPriceCommand.Execute(null);
            row.InputPrice = input;
            row.OutputPrice = output;
            await row.SavePriceCommand.ExecuteAsync(null);
        }

        await page.Saving;
        var editing = page.Models[2];
        editing.EditPriceCommand.Execute(null);
        editing.InputPrice = "0.8";
        editing.OutputPrice = "-2";
        await editing.SavePriceCommand.ExecuteAsync(null);
        Assert.NotNull(editing.PriceError);
        Assert.NotNull(page.TotalCostText);
        await SettleAsync(window);
        // 按模型 at the top of the frame: its rows, the editor and the total below them.
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        var top = Find<ItemsControl>(view, "ModelRows").TranslatePoint(default, (Visual)scroller.Content!)!.Value.Y;
        scroller.Offset = new Vector(0, top - 70);
        await SettleAsync(window);
        Capture(window, "g50b-usage-statistics-prices");
        window.Close();
    }

    /// <summary>A first start: no model yet, nothing asked; the page says so and leads to the model settings (g50c).</summary>
    [AvaloniaFact]
    public async Task G50c_usage_statistics_empty()
    {
        await using var scene = Scene.Create("g50c", FixedStatistics);
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await scene.App.StartWithWorkspaceAsync("火车历史");
        var (page, _) = await OpenStatisticsAsync(scene.App, window);
        Assert.False(page.HasUsage);
        await SettleAsync(window);
        Capture(window, "g50c-usage-statistics-empty");
        window.Close();
    }

    /// <summary>
    /// 每日 token 用量 (2026-11-17): the last year a square a day, a day hovered with its numbers, the legend (g50d,
    /// scrolled to the calendar).
    /// </summary>
    [AvaloniaFact]
    public async Task G50d_usage_calendar()
    {
        await using var scene = Scene.Create("g50d", FixedStatistics);
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedStatisticsAsync(scene.App);
        var (page, view) = await OpenStatisticsAsync(scene.App, window);
        Assert.True(page.HasCalendar);
        var calendar = Find<UsageCalendar>(view, "UsageCalendar");
        Find<StackPanel>(view, "CalendarSection").BringIntoView();
        await SettleAsync(window);
        var day = page.Calendar.Days.Where(d => d.Week > 40).MaxBy(d => d.Counts.Total)!;
        var origin = calendar.CellOrigin(day.Week, day.Weekday);
        window.MouseMove(calendar.TranslatePoint(origin + new Point(3, 3), window)!.Value);
        await SettleAsync(window);
        Assert.Equal(day, calendar.Active);
        Capture(window, "g50d-usage-calendar");
        window.Close();
    }

    // ── Recommended questions on 新探索 (2026-11-12, requirements §10.3.1) ─────────────

    [AvaloniaFact]
    public async Task G51_new_exploration_with_three_recommended_questions_and_the_first_as_ghost_text()
    {
        await using var scene = Scene.Create("g51");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var (page, view) = await OpenRecommendationsAsync(scene.App, window);
        Assert.Equal(3, page.Recommendations.Count);
        Assert.Equal(page.Recommendations[0].Question, Find<GhostTextBox>(view, "Input").Offered);
        await SettleAsync(window);
        Capture(window, "g51-new-exploration-recommended-questions");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G51a_one_question_dismissed_and_a_new_batch_on_its_way()
    {
        await using var scene = Scene.Create("g51a");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var (page, _) = await OpenRecommendationsAsync(scene.App, window);
        await page.Recommendations[0].IgnoreCommand.ExecuteAsync(null);
        scene.App.Client.Recommendations.Hold();
        page.RefreshCommand.Execute(null);
        await UiWait.UntilAsync(() => page.IsRefreshing);
        Assert.Equal(2, page.Recommendations.Count);
        await SettleAsync(window);
        Capture(window, "g51a-new-exploration-dismissed-and-refreshing");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G51b_recommended_questions_could_not_be_prepared_in_the_background()
    {
        await using var scene = Scene.Create("g51b");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var (page, _) = await OpenRecommendationsAsync(scene.App, window);
        scene.App.Client.Recommendations.FailNext(GenerationErrorKind.Network);
        while (page.Recommendations.Count > 0)
        {
            await page.Recommendations[0].IgnoreCommand.ExecuteAsync(null);
        }

        scene.App.PreparationTime.Advance(TimeSpan.FromSeconds(1));
        await scene.App.Services.GetRequiredService<Application.Recommendations.RecommendationService>().SettledAsync();
        await UiWait.UntilAsync(() => page.Note == UiText.RecommendationsQuietFailure);
        await SettleAsync(window);
        Capture(window, "g51b-new-exploration-quiet-failure");
        window.Close();
    }

    // ── Recommended nodes on the summary network (2026-11-12, requirements §10.3.2) ─────────

    [AvaloniaFact]
    public async Task G52_network_with_a_recommended_node_and_its_legend()
    {
        await using var scene = Scene.Create("g52");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App, summaries: true);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(scene.App, window);
        await RecommendedNodesUiTests.PrepareAsync(scene.App, network, window);
        Assert.Single(network.RecommendedNodes);
        await SettleAsync(window);
        Capture(window, "g52-network-recommended-node");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G52a_a_selected_recommended_node_with_its_details()
    {
        await using var scene = Scene.Create("g52a");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App, summaries: true);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(scene.App, window);
        await RecommendedNodesUiTests.PrepareAsync(scene.App, network, window);
        network.SelectRecommendation(network.RecommendedNodes[0]);
        await SettleAsync(window);
        Capture(window, "g52a-network-recommended-node-selected");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G52b_the_list_view_with_a_recommended_node()
    {
        await using var scene = Scene.Create("g52b");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App, summaries: true);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(scene.App, window);
        await RecommendedNodesUiTests.PrepareAsync(scene.App, network, window);
        network.ToggleViewCommand.Execute(null);
        await SettleAsync(window);
        Capture(window, "g52b-network-list-recommended-node");
        window.Close();
    }

    // ── Content links between summaries (2026-11-16, requirements §10.3.3) ─────────

    [AvaloniaFact]
    public async Task G53_network_with_two_links_one_with_a_note_and_the_legend()
    {
        await using var scene = Scene.Create("g53");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var network = await OpenLinkedNetworkAsync(scene.App, window);
        Assert.Equal(2, network.LinkEdges.Count);
        await SettleAsync(window);
        Capture(window, "g53-network-links");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G53a_pick_mode_waits_for_another_summary()
    {
        await using var scene = Scene.Create("g53a");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var network = await OpenLinkedNetworkAsync(scene.App, window);
        network.Select(NodeNamed(network, "缆索牵引"));
        network.BeginLinkCommand.Execute(null);
        Assert.True(network.IsPicking);
        await SettleAsync(window);
        Capture(window, "g53a-network-link-pick-mode");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G53b_the_keyboard_picker_open_with_a_linked_summary_disabled()
    {
        await using var scene = Scene.Create("g53b");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var network = await OpenLinkedNetworkAsync(scene.App, window);
        network.Select(NodeNamed(network, "动轮与速度"));
        await network.LinksReloaded;
        network.BeginLinkCommand.Execute(null);
        await network.PickFromListCommand.ExecuteAsync(null);
        Assert.True(network.ShowsLinkPicker);
        Assert.Contains(network.Links!.PickerItems, i => i.Linked);
        await SettleAsync(window);
        Capture(window, "g53b-network-link-picker");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G53c_the_details_list_the_links_with_a_note_being_edited()
    {
        await using var scene = Scene.Create("g53c");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var network = await OpenLinkedNetworkAsync(scene.App, window);
        network.Select(NodeNamed(network, "高铁速度"));
        await network.LinksReloaded;
        Assert.Equal(2, network.Links!.Items.Count);
        var item = network.Links.Items.Single(i => !i.HasNote);
        item.BeginEditCommand.Execute(null);
        item.EditText = "结构与速度";
        await SettleAsync(window);
        Capture(window, "g53c-network-link-details-edit");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G53d_the_summary_page_with_its_links()
    {
        await using var scene = Scene.Create("g53d");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        await LinkGalleryAsync(scene.App, seed);
        var summary = await OpenSummaryAsync(scene.App, window, seed.Nodes[seed.HighSpeed]);
        await summary.Links!.Loaded;
        Assert.Equal(2, summary.Links.Items.Count);
        await SettleAsync(window);
        Capture(window, "g53d-summary-links");
        window.Close();
    }

    /// <summary>The summarized train workspace with two links (动轮与速度–高铁速度 "对比", 标准轨距–高铁速度 without a note), on the network.</summary>
    private static async Task<SummaryNetworkHomeViewModel> OpenLinkedNetworkAsync(DesktopHarness app, MainWindow window)
    {
        var seed = await SeedAsync(app, summaries: true);
        await LinkGalleryAsync(app, seed);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 2);
        return network;
    }

    internal static async Task LinkGalleryAsync(DesktopHarness app, Seed seed)
    {
        var links = app.Services.GetRequiredService<Application.SummaryLinks.ISummaryLinks>();
        await links.CreateAsync(seed.Nodes[seed.Wheels], seed.Nodes[seed.HighSpeed], "对比", CancellationToken.None);
        await links.CreateAsync(seed.Nodes[seed.Gauge], seed.Nodes[seed.HighSpeed], null, CancellationToken.None);
    }

    private static SummaryNodeId NodeNamed(SummaryNetworkHomeViewModel network, string name) => network.Nodes.Single(n => n.ShortTitle == name).Id;

    // ── A conversation started from a recommendation, linked once summarized (2026-11-17, requirements §10.3.5) ─────────

    [AvaloniaFact]
    public async Task G55_a_recommended_node_explored_then_summarized_links_to_its_basis()
    {
        await using var scene = Scene.Create("g55");
        var app = scene.App;
        var window = app.ShowWindow(1100, 720, Theme);
        await SeedAsync(app, summaries: true);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await RecommendedNodesUiTests.PrepareAsync(app, network, window);
        var recommended = network.RecommendedNodes[0];
        network.SelectRecommendation(recommended);
        await SettleAsync(window);
        Capture(window, "g55-network-before-recommended-node");

        // 开始探索, sent as it is: the conversation exists, unsummarized, so the network has no node for it yet.
        network.StartExplorationCommand.Execute(null);
        var page = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        app.Client.Reply("锅炉爆炸多因水位过低、安全阀失灵或钢板疲劳：水位一低，炉顶钢板干烧变软，内部高压蒸汽便会撕开它。");
        await page.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel);
        await page.Marking;
        var conversation = ((ConversationViewModel)app.Workbench.CurrentPage!).Id;
        await UiWait.UntilAsync(() => !app.Services.GetRequiredService<IConversations>().HasUnfinishedAnswer(conversation));
        app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
        await UiWait.UntilAsync(() => network.RecommendedNodes.Count == 0);
        await SettleAsync(window);
        Capture(window, "g55a-network-sent-not-summarized");

        // Summarized: the new summary is linked to the basis with the note 来自推荐 (in English, "Recommendation"), drawn and listed like any link.
        var service = app.Services.GetRequiredService<SummaryService>();
        app.Client.Knowledge.Replies.Enqueue("标题：蒸汽机车锅炉为什么会爆炸\n短名：锅炉爆炸\n\n- 水位过低：炉顶钢板干烧变软。\n- 安全阀失灵：压力无处释放。\n- 钢板疲劳与水垢。");
        await SummarizeAsync(service, conversation);
        var made = (await service.ForConversationAsync(conversation, CancellationToken.None))!.Node.Id;
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 1 && network.Nodes.Any(n => n.Id == made));
        network.Select(made);
        await network.LinksReloaded;
        var item = Assert.Single(network.Links!.Items);
        Assert.Equal(SummaryLinkNote.FromRecommendation, item.Entry.Link.Note);
        Assert.Equal(recommended.Basis, item.Entry.Other);

        // A new summary is placed beside the others, off the view kept from before; 显示全部 brings it in, as a reader would.
        network.ShowAllCommand.Execute(null);
        await SettleAsync(window);
        Capture(window, "g55b-network-after-summary-linked");
        window.Close();
    }

    // ── AI-suggested links the user confirms (2026-11-16, requirements §10.3.4) ─────────

    [AvaloniaFact]
    public async Task G54_network_with_two_pending_suggestions_one_confirmed_link_and_the_legend()
    {
        await using var scene = Scene.Create("g54");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var network = await OpenSuggestedNetworkAsync(scene.App, window);
        Assert.Equal((1, 2), (network.LinkEdges.Count, network.SuggestionEdges.Count));
        await SettleAsync(window);
        Capture(window, "g54-network-link-suggestions");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G54a_the_suggestion_review_list_with_a_note_being_edited()
    {
        await using var scene = Scene.Create("g54a");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var network = await OpenSuggestedNetworkAsync(scene.App, window);
        var item = network.Suggestions!.Items[0];
        item.BeginEditCommand.Execute(null);
        item.EditText = "轨道结构的演变";
        await SettleAsync(window);
        Capture(window, "g54a-network-link-suggestions-review");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G54b_a_failed_request_said_quietly_with_retry()
    {
        await using var scene = Scene.Create("g54b");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App, summaries: true);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(scene.App, window);
        await network.Suggestions!.Loaded;
        scene.App.Client.LinkSuggestions.FailNext(GenerationErrorKind.ServerError);
        await network.Suggestions.SuggestCommand.ExecuteAsync(null);
        Assert.True(network.Suggestions.NoteCanRetry);
        await SettleAsync(window);
        Capture(window, "g54b-network-link-suggestions-failed");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G54b2_no_new_suggestion_said_quietly()
    {
        await using var scene = Scene.Create("g54b2");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App, summaries: true);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(scene.App, window);
        await network.Suggestions!.Loaded;
        scene.App.Client.LinkSuggestions.Replies.Enqueue("[]");
        await network.Suggestions.SuggestCommand.ExecuteAsync(null);
        Assert.Equal(UiText.LinkSuggestionsNoneNew, network.Suggestions.Note);
        await SettleAsync(window);
        Capture(window, "g54b2-network-link-suggestions-none");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G54c_the_summary_page_with_a_confirmed_link_and_its_marker()
    {
        await using var scene = Scene.Create("g54c");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var network = await OpenSuggestedNetworkAsync(scene.App, window);
        var summary = await OpenSummaryAsync(scene.App, window, NodeNamed(network, "高铁速度"));
        await summary.Links!.Loaded;
        Assert.Contains(summary.Links.Items, i => i.IsConfirmed);
        await SettleAsync(window);
        Capture(window, "g54c-summary-confirmed-link");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G54d_the_links_menu_open_on_the_toolbar()
    {
        await using var scene = Scene.Create("g54d");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await OpenLinkedNetworkAsync(scene.App, window);
        LinksMenu.Open(window);
        await SettleAsync(window);
        Capture(window, "g54d-network-links-menu");
        window.Close();
    }

    /// <summary>
    /// The summarized train workspace on the network with three suggestions asked for through 关联 › 建议关联, the first
    /// (动轮与速度–高铁速度) accepted with its note: one confirmed link and two pending suggestions.
    /// </summary>
    private static async Task<SummaryNetworkHomeViewModel> OpenSuggestedNetworkAsync(DesktopHarness app, MainWindow window)
    {
        await SeedAsync(app, summaries: true);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await LinkSuggestionsUiTests.SuggestAsync(app, window, network,
            ("动轮与速度", "高铁速度", "对比", "蒸汽机车和高铁都在追求更高的速度"),
            ("标准轨距", "无砟轨道", "轨道", "两者都关乎列车跑在什么样的轨道上"),
            ("缆索牵引", "动力分散", "动力方式", "固定动力与分散动力是两种牵引思路"));
        await network.Suggestions!.Items[0].AcceptCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => network.LinkEdges.Count == 1 && network.SuggestionEdges.Count == 2);
        await network.SuggestionsReloaded;
        return network;
    }

    /// <summary>
    /// The summarized train workspace with three questions made the way 换一批 makes them (two extend a summary), and 新探索
    /// open with the input empty and focused.
    /// </summary>
    internal static async Task<(NewConversationViewModel Page, NewConversationView View)> OpenRecommendationsAsync(DesktopHarness app, MainWindow window, string first = "缆索牵引在今天的山区铁路还有用武之地吗？")
    {
        await SeedAsync(app, summaries: true);
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var bases = await app.Services.GetRequiredService<IRecommendationStore>().ListBasesAsync(workspace, Application.Recommendations.RecommendationPrompt.MaxSummaries, 10, CancellationToken.None);
        string Label(string name) => "S" + (bases.Listed.ToList().FindIndex(b => b.DisplayName.StartsWith(name, StringComparison.Ordinal)) + 1);
        app.Client.Recommendations.Replies.Enqueue(
            $"[{{\"question\":\"{first}\",\"reason\":\"顺着缆索牵引的局限往后看\",\"basis\":\"{Label("缆索牵引")}\"}}," +
            $"{{\"question\":\"无砟轨道遇到地基沉降时怎么修复？\",\"reason\":\"接着无砟轨道的代价\",\"basis\":\"{Label("无砟轨道")}\"}}," +
            "{\"question\":\"蒸汽机车是怎样一步步被内燃机车取代的？\",\"reason\":\"还没问过的相邻方向\",\"basis\":\"\"}]");
        var outcome = await app.Services.GetRequiredService<Application.Recommendations.IRecommendations>().RefreshQuestionsAsync(workspace, CancellationToken.None);
        Assert.Equal(3, outcome.Added);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var page = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        window.UpdateLayout();
        var view = window.GetVisualDescendants().OfType<NewConversationView>().Single();
        await UiWait.UntilAsync(() => Find<GhostTextBox>(view, "Input").IsFocused);
        return (page, view);
    }

    /// <summary>
    /// The train workspace (its own answers and titles report their usage), plus 30 days of requests on two more models
    /// and every purpose, and a second workspace with half a year of questions and summaries behind it, so the milestones
    /// and the monthly chart have something to show. Fixed numbers (a seeded random), so frames repeat.
    /// </summary>
    internal static async Task SeedStatisticsAsync(DesktopHarness app)
    {
        app.Client.PromptTokens = _ => 1_800;
        app.Client.CompletionTokens = _ => 420;
        var seed = await SeedAsync(app, summaries: true);
        var usage = new UsageSeed(app.Services.GetRequiredService<SqliteDatabase>());
        var random = new Random(20261112);
        const string DeepSeek = "https://api.deepseek.com";
        const string Glm = "https://open.bigmodel.cn/api/paas/v4";
        ConversationId[] train = [seed.Root, seed.Rainhill, seed.Gauge, seed.HighSpeed, seed.FirstRailway];

        var other = await usage.WorkspaceAsync("摄影史", StatisticsNow.AddDays(-200));
        var history = await usage.ConversationAsync(other, StatisticsNow.AddDays(-176));
        for (var day = 176; day >= 1; day--)
        {
            var at = StatisticsNow.AddDays(-day).AddHours(-random.Next(1, 9));
            var asked = day > 40 ? (random.Next(4) == 0 ? 1 : 0) : random.Next(5) == 0 ? 0 : random.Next(1, 4);
            for (var q = 0; q < asked; q++)
            {
                await usage.QuestionAsync(history, at.AddMinutes(q * 7));
                await usage.RequestAsync(history, "answer", "deepseek-flash", at.AddMinutes(q * 7), random.Next(2_000, 14_000), random.Next(400, 1_800), DeepSeek);
            }

            if (day <= 30 && asked > 0)
            {
                var conversation = train[random.Next(train.Length)];
                await usage.RequestAsync(conversation, "answer", "glm-5", at.AddMinutes(40), random.Next(3_000, 12_000), random.Next(600, 2_000), Glm);
                if (random.Next(3) == 0)
                {
                    await usage.RequestAsync(conversation, "summary", "glm-5", at.AddMinutes(50), random.Next(6_000, 16_000), random.Next(500, 900), Glm);
                }

                if (random.Next(4) == 0)
                {
                    await usage.RequestAsync(conversation, "title", "deepseek-flash", at.AddMinutes(41), random.Next(300, 600), random.Next(10, 30), DeepSeek);
                }
            }

            if (day % 9 == 0)
            {
                await usage.WorkspaceRequestAsync(other, "deepseek-flash", at.AddMinutes(55), random.Next(500, 900), random.Next(40, 80), DeepSeek);
            }
        }

        // Two requests whose service reported no usage, and one stopped answer that was billed.
        await usage.RequestAsync(seed.Gauge, "answer", "deepseek-flash", StatisticsNow.AddDays(-4), null, null, DeepSeek, outcome: "failed");
        await usage.RequestAsync(seed.Gauge, "naming", "glm-5", StatisticsNow.AddDays(-2), null, null, Glm);
        await usage.RequestAsync(seed.Wheels, "answer", "deepseek-flash", StatisticsNow.AddDays(-2), 5_200, 310, DeepSeek, outcome: "stopped");

        for (var i = 0; i < 6; i++)
        {
            var summarized = await usage.ConversationAsync(other, StatisticsNow.AddDays(-150 + (i * 20)));
            await usage.QuestionAsync(summarized, StatisticsNow.AddDays(-150 + (i * 20)));
            await usage.SummaryAsync(summarized, StatisticsNow.AddDays(-149 + (i * 20)), versions: 1 + (i % 3), read: i % 2 == 0);
        }

        await usage.ArchiveAsync(history, StatisticsNow.AddDays(-1));
    }

    internal static async Task<(UsageStatisticsViewModel Page, UsageStatisticsView View)> OpenStatisticsAsync(DesktopHarness app, MainWindow window)
    {
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await SettleAsync(window);
        UiInput.Click(window, Find<Button>(window, "StatisticsButton"));
        var page = Assert.IsType<UsageStatisticsViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await SettleAsync(window);
        return (page, window.GetVisualDescendants().OfType<UsageStatisticsView>().Single());
    }

    // ── Seed ─────────────────────────────────────────────────────────────────

    /// <summary>The conversations of the seeded workspace, and the summary node of each summarized one.</summary>
    internal sealed record Seed(
        ConversationId Root,
        ConversationId Rainhill,
        ConversationId Cable,
        ConversationId Gauge,
        ConversationId Wheels,
        ConversationId HighSpeed,
        ConversationId FirstRailway,
        IReadOnlyDictionary<ConversationId, SummaryNodeId> Nodes);

    /// <summary>
    /// Workspace "火车历史": the train-history exploration (root, three branches, one second-level branch), a
    /// high-speed rail exploration with three branches, and a lone conversation. With <paramref name="summaries"/>:
    /// eight summaries across the two explorations (the first branch has none, so its child links to the root by a
    /// "via" edge), some read, one with an edited current version and a waiting candidate.
    /// </summary>
    internal static async Task<Seed> SeedAsync(DesktopHarness app, bool summaries = false)
    {
        await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;

        var root = await StartAsync(conversations, workspace, "火车的发明", RootQuestion, RootAnswer, app);
        var rainhill = await BranchAsync(conversations, root, "雨山试验", "雨山试验的比赛规则", "雨山试验具体比了什么？", RainhillAnswer, app);
        var cable = await BranchAsync(conversations, rainhill, "用固定蒸汽机拉缆绳的方案", "缆索牵引", "固定蒸汽机拉缆绳是怎样运作的？", CableAnswer, app);
        var gauge = await BranchAsync(conversations, root, "1435 毫米的轨距", "标准轨距的由来", "为什么偏偏是 1435 毫米？", GaugeAnswer, app);
        var wheels = await BranchAsync(conversations, root, "缩小动轮会限制速度", "动轮与速度", "动轮大小和速度有什么关系？", WheelAnswer, app);
        var highSpeed = await StartAsync(conversations, workspace, "高铁为什么快", HighSpeedQuestion, HighSpeedAnswer, app);
        var ballastless = await BranchAsync(conversations, highSpeed, "无砟轨道", "无砟轨道", "无砟轨道是什么结构？", BallastlessAnswer, app);
        var distributed = await BranchAsync(conversations, highSpeed, "动力分散", "动力分散与集中", "动力分散和动力集中有什么区别？", DistributedAnswer, app);
        var pantograph = await BranchAsync(conversations, highSpeed, "受电弓与接触网", "受电弓", "受电弓怎么保证高速下不断电？", PantographAnswer, app);
        var first = await StartAsync(conversations, workspace, "中国第一条铁路", FirstRailwayQuestion, FirstRailwayAnswer, app);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 10);

        // Seeded behind the user's back, every answer finished off screen; the scenes start with all of them seen.
        var marks = app.Services.GetRequiredService<ConversationMarksService>();
        ConversationId[] all = [root, rainhill, cable, gauge, wheels, highSpeed, ballastless, distributed, pantograph, first];
        await UiWait.UntilAsync(() => all.All(marks.IsUnread));
        foreach (var id in all)
        {
            marks.MarkSeen(id);
        }

        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.All(i => !i.IsUnread));

        // The hints those answers popped up in the open workspace go with their marks.
        await UiWait.UntilAsync(() => app.ShellPage.WorkspaceNews!.Toasts.Count == 0);

        var nodes = new Dictionary<ConversationId, SummaryNodeId>();
        if (summaries)
        {
            var service = app.Services.GetRequiredService<SummaryService>();
            var plan = new (ConversationId Conversation, string Title, string Short, string Body, bool Read)[]
            {
                (root, "火车的发明：蒸汽动力与轨道的汇合", "火车的发明", "## 要点\n\n- 1804 年特里维西克造出第一台轨道蒸汽机车。\n- 斯蒂芬森的斯托克顿—达灵顿铁路（1825）和火箭号（1829）让铁路走向实用。\n- 关键改进：多管锅炉、鼓风管、熟铁轨道、标准轨距。\n\n起动牵引力可用 $F = \\frac{0.85\\,p\\,d^2 s}{D}$ 估算。", true),
                (cable, "缆索牵引及其局限", "缆索牵引", "固定蒸汽机通过滚筒卷动缆绳拖动车厢，**适合陡坡**，但缆绳易磨损、一处故障全线停运。\n\n所需功率约为缆绳拉力与速度之积：\n\n$$P = F\\,v$$\n\n**“火箭号”**之后只在少数陡坡保留。", false),
                (gauge, "标准轨距 1435 毫米的由来", "标准轨距", "沿用英格兰东北部煤矿马车道的轨距；斯蒂芬森的线路使它成为事实标准，1846 年《轨距法》将其定为英国客运铁路标准。", true),
                (wheels, "动轮直径与速度、牵引力的取舍", "动轮与速度", "列车每转一圈前进 $\\pi D$。大动轮跑得快、牵引力小，用于客运；小动轮牵引力大，用于货运。", false),
                (highSpeed, "高铁高速的四项关键技术", "高铁速度", "1. 无砟轨道\n2. 动力分散\n3. 受电弓与接触网\n4. 流线型车头\n\n高速下空气阻力约与速度平方成正比。", true),
                (ballastless, "无砟轨道的结构与代价", "无砟轨道", "预制混凝土轨道板加砂浆层与底座；平顺、少维护，但造价高、沉降后难调整。", false),
                (distributed, "动力分散与动力集中", "动力分散", "动力分散把牵引电机分到多节车厢，起动、制动更快、不易打滑；250 km/h 以上列车几乎都采用。", true),
                (pantograph, "受电弓如何稳定取电", "受电弓", "碳滑板被弹簧或气囊压在接触线上；高速下需保持稳定接触压力，避免电弧和磨损。", false),
            };
            foreach (var (conversation, title, shortName, body, read) in plan)
            {
                app.Client.Knowledge.Replies.Enqueue($"标题：{title}\n短名：{shortName}\n\n{body}");
                await SummarizeAsync(service, conversation);
                var entry = (await service.ForConversationAsync(conversation, CancellationToken.None))!;
                nodes[conversation] = entry.Node.Id;
                if (read)
                {
                    await service.SetReadAsync(entry.Node.Id, entry.Current.Id, true, CancellationToken.None);
                }
            }

            // The gauge summary: edited by hand, then regenerated, so a candidate waits beside the edited version.
            var gaugeNode = nodes[gauge];
            var detail = (await service.GetAsync(gaugeNode, CancellationToken.None))!;
            await service.SaveEditAsync(gaugeNode, detail.Current.Id, detail.Current.Title, detail.Current.Body + "\n\n我的笔记：美国南北战争后，南方铁路在 1886 年的两天之内统一改成了这一轨距。", CancellationToken.None);
            app.Client.Knowledge.Replies.Enqueue("标题：标准轨距 1435 毫米的由来与推广\n短名：标准轨距\n\n- 来源：煤矿马车道的轨距。\n- 推广：斯蒂芬森的线路、1846 年《轨距法》。\n- 例外：西班牙、俄罗斯等采用宽轨，日本早期采用窄轨。");
            await SummarizeAsync(service, gauge);
        }

        return new Seed(root, rainhill, cable, gauge, wheels, highSpeed, first, nodes);
    }

    private static async Task<ConversationId> StartAsync(ConversationService conversations, WorkspaceId workspace, string title, string question, string answer, DesktopHarness app)
    {
        app.Client.Reply(answer);
        var sent = await conversations.StartAsync(workspace, question, CancellationToken.None);
        Assert.Null(sent.Rejection);

        // A manual title before the answer finishes, so no automatic title is requested.
        await conversations.RenameAsync(sent.Conversation, title, CancellationToken.None);
        await FinishedAsync(conversations, sent.Conversation);
        return sent.Conversation;
    }

    private static async Task<ConversationId> BranchAsync(ConversationService conversations, ConversationId parent, string quote, string title, string question, string answer, DesktopHarness app)
    {
        var source = (await conversations.LoadAsync(parent, CancellationToken.None))!.Messages.Last(m => m.Role == MessageRole.Assistant);
        Assert.True(source.Text.Contains(quote, StringComparison.Ordinal), $"Quote not in its source: {quote}");
        var branch = await conversations.CreateBranchAsync(parent, new BranchSelection(source.Id, 0, quote, "{\"v\":1}", 1, "gallery"), CancellationToken.None);
        Assert.True(branch.Accepted, branch.Rejection?.ToString());
        var child = branch.Child;
        await conversations.RenameAsync(child, title, CancellationToken.None);
        app.Client.Reply(answer);
        var sent = await conversations.AskAsync(child, question, CancellationToken.None);
        Assert.Null(sent.Rejection);
        await FinishedAsync(conversations, child);
        return child;
    }

    private static Task FinishedAsync(ConversationService conversations, ConversationId conversation) =>
        UiWait.UntilAsync(() => conversations.LoadAsync(conversation, CancellationToken.None).Result!.Messages.All(m => m.IsFinished));

    private static async Task SummarizeAsync(SummaryService service, ConversationId conversation)
    {
        var outcome = await service.SummarizeAsync(conversation, CancellationToken.None);
        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        await UiWait.UntilAsync(() => service.LatestTaskAsync(conversation, CancellationToken.None).Result is { State: GenerationTaskState.Completed });
    }

    // ── Pages ────────────────────────────────────────────────────────────────

    internal static async Task<ConversationViewModel> OpenConversationAsync(DesktopHarness app, MainWindow window, ConversationId id)
    {
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(id)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        window.UpdateLayout();
        await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<ConversationView>().SingleOrDefault() is { } view && view.DataContext == page && view.Restored.IsCompleted);
        await UiWait.UntilAsync(() => Answers(window).All(a => a.IsLayoutCurrent));
        await SettleAsync(window);
        return page;
    }

    internal static async Task<SummaryViewModel> OpenSummaryAsync(DesktopHarness app, MainWindow window, SummaryNodeId node)
    {
        app.Workbench.Navigate(new NavigationRequest(MainSection.SummaryNetwork, new SummaryRoute(node)));
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        await UiWait.UntilAsync(() => summary.Shown is not null);
        await SettleAsync(window);
        return summary;
    }

    /// <summary>Lets bindings, Markdown layout and deferred work settle before a frame is taken.</summary>
    internal static async Task SettleAsync(MainWindow window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Delay(50);
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Capture(MainWindow window, string name)
    {
        window.UpdateLayout();
        Screenshots.Capture(window, name);
    }

    /// <summary>
    /// MINDWEAVE_GALLERY_THEME=Light|Dark (default Light) renders the same scenes in either theme (D-022). The theme is
    /// set on each scene's window, not on the application the tests share.
    /// </summary>
    private static ThemeVariant Theme =>
        string.Equals(Environment.GetEnvironmentVariable("MINDWEAVE_GALLERY_THEME"), "Dark", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Dark : ThemeVariant.Light;

    private static string? ScreenshotDir =>
        Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>The application's clocks start at a fixed instant and then run in real time, so shown times repeat.</summary>
    private static void FixedTime(IServiceCollection services)
    {
        var time = ManualTime.Running(GalleryStart);
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IClock>(time);
    }

    /// <summary>One scene's application over a fixed data directory (so paths shown repeat), removed afterwards.</summary>
    private sealed class Scene : IAsyncDisposable
    {
        private readonly string _directory;

        private Scene(string directory, DesktopHarness app)
        {
            _directory = directory;
            App = app;
        }

        public DesktopHarness App { get; }

        public static string Directory(string name) => Path.Combine(Path.GetTempPath(), "mindweave-gallery", name);

        public static Scene Create(string name, Action<IServiceCollection>? overrides = null, bool fresh = true)
        {
            Assert.SkipWhen(ScreenshotDir is null, "Set MINDWEAVE_SCREENSHOT_DIR to render the gallery.");
            var directory = Directory(name);
            if (fresh && System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }

            return new Scene(directory, DesktopHarness.Create(
                services =>
                {
                    FixedTime(services);
                    overrides?.Invoke(services);
                },
                directory));
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            try
            {
                System.IO.Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
