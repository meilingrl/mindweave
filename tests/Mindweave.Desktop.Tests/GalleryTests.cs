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
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
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

        真正让铁路走向实用的是乔治·斯蒂芬森。1825 年他主持修建的斯托克顿—达灵顿铁路通车，1829 年他和儿子罗伯特设计的“火箭号”在雨山试验中胜出，证明蒸汽机车可以稳定、快速地牵引列车。此后几十年间的关键改进包括：

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
        onboarding.Form.Name = "火车历史";
        await SettleAsync(window);
        Capture(window, "g01-onboarding");
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
        var network = await NetworkScenario.OpenNetworkAsync(scene.App, window);
        Assert.Equal(8, network.Nodes.Count);
        Assert.Contains(network.Edges, e => !e.IsDirect);
        network.Select(seed.Nodes[seed.Gauge]);
        await SettleAsync(window);
        var canvas = window.GetVisualDescendants().OfType<NetworkCanvas>().Single();
        Assert.True(canvas.Current.Zoom >= 0.8, $"default zoom {canvas.Current.Zoom:0.00}");
        var gauge = canvas.PaintNodes().Single(p => p.Node.Id == seed.Nodes[seed.Gauge]);
        Assert.All(canvas.PaintEdges(), e => Assert.False(NetworkCanvas.Crosses(e.From, e.To, gauge.LabelBounds), $"{gauge.LabelBounds} {e.From} {e.To}"));
        Capture(window, "g13-network");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G14_network_list_view()
    {
        await using var scene = Scene.Create("g14");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App, summaries: true);
        var network = await NetworkScenario.OpenNetworkAsync(scene.App, window);
        network.ToggleViewCommand.Execute(null);
        Assert.True(network.IsListView);
        await SettleAsync(window);
        Capture(window, "g14-network-list");
        window.Close();
    }

    [AvaloniaFact]
    public async Task G15_network_after_arrange_is_unsaved()
    {
        await using var scene = Scene.Create("g15");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App, summaries: true);
        var network = await NetworkScenario.OpenNetworkAsync(scene.App, window);
        await NetworkScenario.MoveSomethingAsync(scene.App, network);
        network.ArrangeCommand.Execute(null);
        Assert.True(network.IsDirty);
        await SettleAsync(window);
        Capture(window, "g15-network-arranged-unsaved");
        await network.SaveLayoutCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task G16_layout_leave_prompt()
    {
        await using var scene = Scene.Create("g16");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App, summaries: true);
        var other = await scene.App.Services.GetRequiredService<WorkspaceService>().CreateAsync(WorkspaceName.FromStored("摄影史"), CancellationToken.None);
        scene.App.Workbench.Workspaces.Add(other);
        var network = await NetworkScenario.OpenNetworkAsync(scene.App, window);
        await NetworkScenario.MoveSomethingAsync(scene.App, network);
        network.ArrangeCommand.Execute(null);
        scene.App.ShellPage.SelectedWorkspace = other;
        Assert.True(scene.App.Root.LayoutPrompt.IsOpen);
        await SettleAsync(window);
        Capture(window, "g16-layout-leave-prompt");
        scene.App.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        window.Close();
    }

    /// <summary>Round 3: a node lifted mid-drag (g16a), then dropped, leaving the layout unsaved (g16b).</summary>
    [AvaloniaFact]
    public async Task G16a_network_node_drag()
    {
        await using var scene = Scene.Create("g16a");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);
        var network = await NetworkScenario.OpenNetworkAsync(scene.App, window);
        await SettleAsync(window);
        var canvas = window.GetVisualDescendants().OfType<NetworkCanvas>().Single();
        var node = network.Nodes.Single(n => n.Id == seed.Nodes[seed.Gauge]);
        var start = canvas.ToScreen(node.X, node.Y);
        Point InWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;

        window.MouseMove(InWindow(start));
        window.MouseDown(InWindow(start), MouseButton.Left);
        window.MouseMove(InWindow(start + new Vector(-20, 40)));
        window.MouseMove(InWindow(start + new Vector(-40, 85)));
        Assert.Equal(node.Id, canvas.Dragging);
        await SettleAsync(window);
        Capture(window, "g16a-network-node-dragging");

        window.MouseUp(InWindow(start + new Vector(-40, 85)), MouseButton.Left);
        window.MouseMove(InWindow(start + new Vector(260, 200)));
        Assert.True(network.IsDirty);
        await SettleAsync(window);
        Capture(window, "g16b-network-dragged-unsaved");
        network.Session.DiscardChanges();
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

    [AvaloniaFact]
    public async Task G27_storage_details_with_backups()
    {
        await using var scene = Scene.Create("g27");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        await SeedAsync(scene.App);
        var backups = Path.Combine(Scene.Directory("g27"), "backups");
        System.IO.Directory.CreateDirectory(backups);
        await File.WriteAllBytesAsync(Path.Combine(backups, "mindweave-v7-20260301T081500000Z.db"), new byte[412 * 1024]);
        await File.WriteAllBytesAsync(Path.Combine(backups, "mindweave-v6-20260212T193000000Z.db"), new byte[(int)(2.4 * 1024 * 1024)]);
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await SettleAsync(window);
        var details = Find<Button>(window, "StorageDetailsButton");
        details.Command!.Execute(details.CommandParameter);
        var page = Assert.IsType<StorageDetailsViewModel>(scene.App.Workbench.CurrentPage);
        Assert.Equal(2, page.Backups.Count);
        await SettleAsync(window);
        Capture(window, "g27-storage-backups");
        window.Close();
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
        await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t is { Name: "StreamingElapsed", IsEffectivelyVisible: true, Text: "12 秒" or "13 秒" }), timeoutMs: 3000);
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
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].WaitingLabel == UiText.ModelThinking);
        Time(scene.App).Advance(TimeSpan.FromSeconds(48) - (Time(scene.App).GetUtcNow() - page.Messages[3].StartedAt));
        await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t is { Name: "WaitingElapsed", IsEffectivelyVisible: true, Text: "48 秒" or "49 秒" }), timeoutMs: 3000);
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

    private static GalleryTime Time(DesktopHarness app) => (GalleryTime)app.Services.GetRequiredService<TimeProvider>();

    /// <summary>Lets a held answer run for <paramref name="seconds"/> on the gallery clock, then finish.</summary>
    private static async Task TakesAsync(Mindweave.Testing.FakeChatClient.HeldReply held, GalleryTime time, int seconds, ConversationService conversations, ConversationId conversation)
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
    /// The 工作区 section of the settings (g33a), the delete-a-workspace confirmation opened from it with the name typed
    /// (g33), the dialog refusing while an answer runs (g33b), and after the delete the storage page with its backup (g33c).
    /// </summary>
    [AvaloniaFact]
    public async Task G33_delete_workspace_confirmation()
    {
        await using var scene = Scene.Create("g33");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App, summaries: true);

        // Opened from 设置 › 工作区, scrolled into view behind the dialog.
        scene.App.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(scene.App.Workbench.CurrentPage);
        await SettleAsync(window);
        window.GetVisualDescendants().OfType<SettingsHomeView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
        await SettleAsync(window);
        Capture(window, "g33a-settings-workspace-section");
        settings.DeleteWorkspaceCommand.Execute(null);
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
        Assert.Single(Assert.IsType<StorageDetailsViewModel>(scene.App.Workbench.CurrentPage).Backups);
        await SettleAsync(window);
        Capture(window, "g33c-storage-backup-before-delete");
        window.Close();
    }

    // ── Sidebar status (S7 round 4) ─────────────────────────────────────────

    [AvaloniaFact]
    public async Task G34_sidebar_status_spinner_unread_and_attention()
    {
        await using var scene = Scene.Create("g34");
        var window = scene.App.ShowWindow(1100, 720, Theme);
        var seed = await SeedAsync(scene.App);
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

    // ── Seed ─────────────────────────────────────────────────────────────────

    /// <summary>The conversations of the seeded workspace, and the summary node of each summarized one.</summary>
    private sealed record Seed(
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
    private static async Task<Seed> SeedAsync(DesktopHarness app, bool summaries = false)
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

        var nodes = new Dictionary<ConversationId, SummaryNodeId>();
        if (summaries)
        {
            var service = app.Services.GetRequiredService<SummaryService>();
            var plan = new (ConversationId Conversation, string Title, string Short, string Body, bool Read)[]
            {
                (root, "火车的发明：蒸汽动力与轨道的汇合", "火车的发明", "## 要点\n\n- 1804 年特里维西克造出第一台轨道蒸汽机车。\n- 斯蒂芬森的斯托克顿—达灵顿铁路（1825）和火箭号（1829）让铁路走向实用。\n- 关键改进：多管锅炉、鼓风管、熟铁轨道、标准轨距。\n\n起动牵引力可用 $F = \\frac{0.85\\,p\\,d^2 s}{D}$ 估算。", true),
                (cable, "缆索牵引及其局限", "缆索牵引", "固定蒸汽机通过滚筒卷动缆绳拖动车厢，**适合陡坡**，但缆绳易磨损、一处故障全线停运。\n\n所需功率约为缆绳拉力与速度之积：\n\n$$P = F\\,v$$\n\n火箭号之后只在少数陡坡保留。", false),
                (gauge, "标准轨距 1435 毫米的由来", "标准轨距", "沿用英格兰东北部煤矿马车道的轨距；斯蒂芬森的线路使它成为事实标准，1846 年《轨距法》将其定为英国客运铁路标准。", true),
                (wheels, "动轮直径与速度、牵引力的取舍", "动轮与速度", "列车每转一圈前进 $\\pi D$。大动轮跑得快、牵引力小，用于客运；小动轮牵引力大，用于货运。", false),
                (highSpeed, "高铁高速的四项关键技术", "高铁速度", "1. 无砟轨道\n2. 动力分散\n3. 受电弓与接触网\n4. 流线型车头\n\n高速下空气阻力约与速度平方成正比。", true),
                (ballastless, "无砟轨道的结构与代价", "无砟轨道", "预制混凝土轨道板加砂浆层与底座；平顺、少维护，但造价高、沉降后难调整。", false),
                (distributed, "动力分散与动力集中", "动力分散", "动力分散把牵引电机分到多节车厢，起动、制动更快、不易打滑；250 km/h 以上列车几乎都采用。", true),
                (pantograph, "受电弓如何稳定取电", "受电弓", "碳滑板被弹簧或气囊压在接触线上；高速下需保持稳定接触压力，避免电弧和磨损。", false),
            };
            foreach (var (conversation, title, shortName, body, read) in plan)
            {
                app.Client.SummaryReplies.Enqueue($"标题：{title}\n短名：{shortName}\n\n{body}");
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
            app.Client.SummaryReplies.Enqueue("标题：标准轨距 1435 毫米的由来与推广\n短名：标准轨距\n\n- 来源：煤矿马车道的轨距。\n- 推广：斯蒂芬森的线路、1846 年《轨距法》。\n- 例外：西班牙、俄罗斯等采用宽轨，日本早期采用窄轨。");
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

    private static async Task<ConversationViewModel> OpenConversationAsync(DesktopHarness app, MainWindow window, ConversationId id)
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

    private static async Task<SummaryViewModel> OpenSummaryAsync(DesktopHarness app, MainWindow window, SummaryNodeId node)
    {
        app.Workbench.Navigate(new NavigationRequest(MainSection.Network, new SummaryRoute(node)));
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        await UiWait.UntilAsync(() => summary.Shown is not null);
        await SettleAsync(window);
        return summary;
    }

    /// <summary>Lets bindings, Markdown layout and deferred work settle before a frame is taken.</summary>
    private static async Task SettleAsync(MainWindow window)
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
        var time = new GalleryTime(GalleryStart);
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IClock>(new GalleryClock(time));
    }

    private sealed class GalleryTime(DateTimeOffset start) : TimeProvider
    {
        private readonly long _origin = System.GetTimestamp();
        private long _ahead;

        /// <summary>Moves the clock forward, so a scene can show an answer that took (or is taking) a while.</summary>
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ahead, by.Ticks);

        public override DateTimeOffset GetUtcNow() => start + System.GetElapsedTime(_origin) + TimeSpan.FromTicks(Interlocked.Read(ref _ahead));
    }

    private sealed class GalleryClock(TimeProvider time) : IClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
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
