using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Statistics;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;
using Mindweave.Testing.Usage;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// 设置 › 使用统计 (2026-11-12) in the real window over the real stores: reached from 设置 and back, read again when shown
/// again, the scope and period switches, the table instead of the chart, unit prices and the currency, the chart from the
/// keyboard and for a screen reader, and the empty states. Days are counted with a fixed clock in a fixed zone.
/// </summary>
public class UsageStatisticsUiTests
{
    /// <summary>"Today" for these tests, in <see cref="Zone"/>.</summary>
    internal static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(8));

    /// <summary>A fixed UTC+8, so the local days do not depend on the machine.</summary>
    internal static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test/UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private static DesktopHarness Create() =>
        DesktopHarness.Create(s => s.AddSingleton(new UsageStatisticsOptions { Time = new ManualTime(Now), Zone = Zone }));

    [AvaloniaFact]
    public async Task The_page_opens_from_settings_goes_back_and_reads_again_when_shown_again()
    {
        await using var app = Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var seed = new UsageSeed(app.Services.GetRequiredService<SqliteDatabase>());
        var c = await seed.ConversationAsync(workspace.Id, Now.AddDays(-2));
        await seed.QuestionAsync(c, Now.AddDays(-2));
        await seed.RequestAsync(c, "answer", "deepseek-flash", Now.AddDays(-2), 1200, 300, "https://api.deepseek.com");

        var (window, page, view) = await OpenAsync(app);
        Assert.Equal(UiText.StatisticsTitle, page.Title);
        Assert.True(Part<Button>(window, "BackButton").IsEffectivelyVisible);
        Assert.Equal("1,200", Find<TextBlock>(view, "InputTotal").Text);
        Assert.Equal("300", Find<TextBlock>(view, "OutputTotal").Text);
        Assert.Equal("1 次", Find<TextBlock>(view, "RequestsTotal").Text);
        Assert.Equal(UiText.DeletedUsageCaption, Find<TextBlock>(view, "DeletedUsageCaption").Text);
        Assert.False(Find<StackPanel>(view, "StatisticsLoading").IsEffectivelyVisible);

        // Away to another section and back: the page on top of 设置 is shown again and reads again.
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        window.UpdateLayout();
        await seed.RequestAsync(c, "title", "deepseek-flash", Now.AddHours(-1), 100, 20, "https://api.deepseek.com");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();
        Assert.Same(page, app.Workbench.CurrentPage);
        await page.Loaded;
        window.UpdateLayout();
        view = window.GetVisualDescendants().OfType<UsageStatisticsView>().Single();
        Assert.Equal("1,300", Find<TextBlock>(view, "InputTotal").Text);

        UiInput.Click(window, Part<Button>(window, "BackButton"));
        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        Assert.Empty(window.GetVisualDescendants().OfType<UsageStatisticsView>());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Scope_and_period_switch_the_figures()
    {
        await using var app = Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var seed = new UsageSeed(app.Services.GetRequiredService<SqliteDatabase>());
        var other = await seed.WorkspaceAsync("摄影史", Now.AddDays(-90));
        var mine = await seed.ConversationAsync(workspace.Id, Now.AddDays(-2));
        await seed.QuestionAsync(mine, Now.AddDays(-2));
        await seed.RequestAsync(mine, "answer", "m", Now.AddDays(-2), 100, 10);
        await seed.WorkspaceRequestAsync(workspace.Id, "m", Now.AddDays(-1), 5, 5);
        var theirs = await seed.ConversationAsync(other, Now.AddDays(-60));
        await seed.QuestionAsync(theirs, Now.AddDays(-60));
        await seed.QuestionAsync(theirs, Now.AddDays(-3));
        await seed.RequestAsync(theirs, "answer", "m", Now.AddDays(-3), 1000, 100);
        await seed.RequestAsync(theirs, "answer", "m", Now.AddDays(-60), 7000, 700);

        var (window, page, view) = await OpenAsync(app);
        Assert.True(Find<RadioButton>(view, "ScopeAll").IsChecked);
        Assert.Equal("1,105", Find<TextBlock>(view, "InputTotal").Text);
        Assert.Equal(30, page.Bars.Count);
        Assert.Equal(3, ActivityValue(page, UiText.QuestionsLabel));

        UiInput.Click(window, Find<RadioButton>(view, "ScopeWorkspace"));
        await page.Loaded;
        window.UpdateLayout();
        Assert.Equal("105", Find<TextBlock>(view, "InputTotal").Text);
        Assert.Equal(1, ActivityValue(page, UiText.QuestionsLabel));
        Assert.Contains("火车历史", Find<TextBlock>(view, "ScopeCaption").Text, StringComparison.Ordinal);
        Assert.Equal("当前工作区（火车历史）", AutomationProperties.GetName(Find<RadioButton>(view, "ScopeWorkspace")));

        UiInput.Click(window, Find<RadioButton>(view, "PeriodMonthly"));
        await page.Loaded;
        window.UpdateLayout();
        Assert.Equal(12, page.Bars.Count);
        Assert.Equal("最近 12 个月（2025年12月–2026年11月）", Find<TextBlock>(view, "RangeText").Text);
        Assert.Equal("105", Find<TextBlock>(view, "InputTotal").Text);

        // From the keyboard: the arrow keys move along the segments, as on 设置's appearance picker.
        Find<RadioButton>(view, "ScopeWorkspace").Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        await page.Loaded;
        window.UpdateLayout();
        Assert.True(page.AllWorkspaces);
        Assert.Equal("8,105", Find<TextBlock>(view, "InputTotal").Text);
        Find<RadioButton>(view, "PeriodMonthly").Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        await page.Loaded;
        Assert.Equal(UsagePeriod.Daily, page.Period);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_table_shows_every_day_instead_of_the_chart()
    {
        await using var app = Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var seed = new UsageSeed(app.Services.GetRequiredService<SqliteDatabase>());
        var c = await seed.ConversationAsync(workspace.Id, Now.AddDays(-5));
        await seed.RequestAsync(c, "answer", "m", Now.AddDays(-5), 1234, 56);
        await seed.RequestAsync(c, "answer", "m", Now.AddDays(-5), null, null);

        var (window, page, view) = await OpenAsync(app);
        Assert.True(Find<UsageChart>(view, "UsageChart").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(view, "UsageTable").IsEffectivelyVisible);
        Assert.Equal(UiText.UnreportedRequests(1), Find<TextBlock>(view, "UnreportedText").Text);

        UiInput.Click(window, Find<CheckBox>(view, "TableToggle"));
        window.UpdateLayout();
        Assert.True(page.ShowsTable);
        Assert.False(Find<UsageChart>(view, "UsageChart").IsEffectivelyVisible);
        var rows = Find<ItemsControl>(view, "UsageTableRows").GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("usage-table-row")).ToList();
        Assert.Equal(30, rows.Count);
        Assert.Equal("11月7日：输入 1,234，输出 56，1 次请求，另有 1 次未返回用量", AutomationProperties.GetName(rows[24]));
        Assert.Equal("11月12日：没有请求", AutomationProperties.GetName(rows[^1]));
        var texts = rows[24].GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Equal(["11月7日", "1,234", "56", "1 次（1 次未返回用量）"], texts);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_unit_price_is_checked_saved_and_gives_a_cost()
    {
        await using var app = Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var seed = new UsageSeed(app.Services.GetRequiredService<SqliteDatabase>());
        var c = await seed.ConversationAsync(workspace.Id, Now.AddDays(-1));
        await seed.RequestAsync(c, "answer", "deepseek-flash", Now.AddDays(-1), 2_000_000, 500_000, "https://api.deepseek.com");
        await seed.RequestAsync(c, "answer", "glm-5", Now.AddDays(-1), 100_000, 10_000, "https://open.bigmodel.cn/api/paas/v4");
        var prices = app.Services.GetRequiredService<IUsagePrices>();
        var flash = new ModelKey("deepseek-flash", "api.deepseek.com");

        var (window, page, view) = await OpenAsync(app);
        var row = page.Models.Single(m => m.Key == flash);
        Assert.Equal(["deepseek-flash · api.deepseek.com", "glm-5 · open.bigmodel.cn"], page.Models.Select(m => m.Label));
        Assert.Null(page.TotalCostText);
        var setPrice = Buttons(view, "SetPriceButton").Single(b => b.DataContext == row);
        Assert.Equal("设置 deepseek-flash · api.deepseek.com 的单价", AutomationProperties.GetName(setPrice));

        UiInput.Click(window, setPrice);
        await SettleAsync(window);
        var input = Boxes(view, "InputPriceBox").Single(b => b.DataContext == row);
        var output = Boxes(view, "OutputPriceBox").Single(b => b.DataContext == row);
        Assert.True(input.IsEffectivelyVisible);
        Assert.True(input.IsFocused);

        // Refused: a negative number, then only one of the two. Nothing is stored.
        input.Text = "-1";
        output.Text = "2";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await SettleAsync(window);
        Assert.Equal(UiText.PriceProblemText(PriceProblem.Negative), row.PriceError);
        Assert.True(Texts(view, "PriceError").Single(t => t.DataContext == row).IsEffectivelyVisible);
        input.Text = string.Empty;
        UiInput.Click(window, Buttons(view, "SavePriceButton").Single(b => b.DataContext == row));
        await SettleAsync(window);
        Assert.Equal(UiText.PriceProblemText(PriceProblem.Incomplete), row.PriceError);
        Assert.Null(await prices.GetAsync(flash, CancellationToken.None));

        input.Text = "0.5";
        UiInput.Click(window, Buttons(view, "SavePriceButton").Single(b => b.DataContext == row));
        await page.Saving;
        await SettleAsync(window);
        Assert.False(row.IsEditing);
        Assert.Equal(new ModelPrice(0.5m, 2m), await prices.GetAsync(flash, CancellationToken.None));
        Assert.Equal("约 ¥2.00", Texts(view, "ModelCost").Single(t => t.DataContext == row).Text);
        Assert.Equal("按单价估算的费用：约 ¥2.00（另有 1 个模型未填单价，未计入）", Find<TextBlock>(view, "TotalCost").Text);
        Assert.Equal(UiText.CostCaption, Find<TextBlock>(view, "CostCaption").Text);
        Assert.True(Buttons(view, "SetPriceButton").Single(b => b.DataContext == row).IsFocused);

        // The currency: three characters at most, a longer one is refused and not stored.
        var currency = Find<TextBox>(view, "CurrencyBox");
        currency.Text = "EURO";
        Assert.Equal(UiText.PriceProblemText(PriceProblem.CurrencyTooLong), page.CurrencyError);
        Assert.Equal("¥", await prices.GetCurrencyAsync(CancellationToken.None));
        currency.Text = "$";
        await page.Saving;
        Assert.Null(page.CurrencyError);
        Assert.Equal("$", await prices.GetCurrencyAsync(CancellationToken.None));
        window.UpdateLayout();
        Assert.Equal("约 $2.00", Texts(view, "ModelCost").Single(t => t.DataContext == row).Text);

        // Esc leaves the editor without saving what was typed.
        UiInput.Click(window, Buttons(view, "SetPriceButton").Single(b => b.DataContext == row));
        await SettleAsync(window);
        Boxes(view, "InputPriceBox").Single(b => b.DataContext == row).Text = "9";
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await SettleAsync(window);
        Assert.False(row.IsEditing);
        Assert.Equal(new ModelPrice(0.5m, 2m), await prices.GetAsync(flash, CancellationToken.None));

        // Read back on a fresh page; clearing removes the price.
        page.Refresh();
        await page.Loaded;
        row = page.Models.Single(m => m.Key == flash);
        Assert.Equal(new ModelPrice(0.5m, 2m), row.Price);
        Assert.Equal("$", page.Currency);
        await row.ClearPriceCommand.ExecuteAsync(null);
        await page.Saving;
        Assert.Null(await prices.GetAsync(flash, CancellationToken.None));
        Assert.Null(page.TotalCostText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_chart_works_from_the_keyboard_and_describes_itself()
    {
        await using var app = Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var seed = new UsageSeed(app.Services.GetRequiredService<SqliteDatabase>());
        var c = await seed.ConversationAsync(workspace.Id, Now.AddDays(-3));
        await seed.RequestAsync(c, "answer", "m", Now.AddDays(-3), 900, 100);
        await seed.RequestAsync(c, "answer", "m", Now.AddDays(-1), 40, 2);

        var (window, page, view) = await OpenAsync(app);
        var chart = Find<UsageChart>(view, "UsageChart");
        Assert.Equal(UiText.UsageChartName, AutomationProperties.GetName(chart));
        Assert.Equal(page.ChartSummary, AutomationProperties.GetHelpText(chart));
        Assert.Contains("用量最多的是 11月9日（1,000）", page.ChartSummary, StringComparison.Ordinal);
        Assert.Contains("28 天没有用量", page.ChartSummary, StringComparison.Ordinal);

        // Focus lands on today; the arrows walk the bars and each is told to a screen reader.
        chart.Focus(NavigationMethod.Tab);
        Assert.Equal(29, chart.ActiveIndex);
        Assert.Equal("11月12日：没有请求", AutomationProperties.GetItemStatus(chart));
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Equal(26, chart.ActiveIndex);
        Assert.Equal("11月9日：输入 900，输出 100，1 次请求", AutomationProperties.GetItemStatus(chart));
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Equal(0, chart.ActiveIndex);
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Assert.Equal(29, chart.ActiveIndex);

        // Hovering shows a bar too; leaving the chart (without focus) clears it.
        Find<Button>(view, "SetPriceButton").Focus();
        Assert.Equal(-1, chart.ActiveIndex);
        var point = chart.TranslatePoint(chart.SlotCentre(28), window)!.Value;
        window.MouseMove(point);
        Assert.Equal(28, chart.ActiveIndex);
        Assert.Equal(28, chart.IndexAt(chart.SlotCentre(28)));

        // Every colour the chart draws with is defined in both themes.
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.All(UsageChart.Keys, key => Assert.True(Avalonia.Application.Current!.Resources.TryGetResource(key, theme, out _), $"{key} in {theme}"));
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Without_a_model_the_page_says_so_and_leads_to_the_model_settings()
    {
        await using var app = Create();
        await app.StartWithWorkspaceAsync("火车历史");
        var (window, page, view) = await OpenAsync(app);

        Assert.False(page.HasUsage);
        Assert.Equal(UiText.NoModelConfigured, Find<TextBlock>(view, "UsageEmptyText").Text);
        Assert.False(Find<CheckBox>(view, "TableToggle").IsEffectivelyVisible);
        Assert.False(Find<ItemsControl>(view, "ModelRows").IsEffectivelyVisible);
        Assert.Equal(UiText.NoQuestionsAtAll, Find<TextBlock>(view, "NoQuestions").Text);
        Assert.Equal(0, ActivityValue(page, UiText.QuestionsLabel));
        Assert.False(Find<ItemsControl>(view, "ActivityItems").IsEffectivelyVisible);
        Assert.False(Find<TextBlock>(view, "ActiveDays").IsEffectivelyVisible);
        Assert.False(Find<ItemsControl>(view, "MilestoneList").IsEffectivelyVisible);

        UiInput.Click(window, Find<Button>(view, "GoToModelSettings"));
        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task With_a_model_but_nothing_asked_and_in_an_empty_workspace_the_page_says_what_is_missing()
    {
        await using var app = Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var (window, page, view) = await OpenAsync(app);
        Assert.Equal(UiText.NoRequestsYet, Find<TextBlock>(view, "UsageEmptyText").Text);
        Assert.False(Find<Button>(view, "GoToModelSettings").IsEffectivelyVisible);

        // Requests elsewhere and long ago: "none in this period", and the empty workspace has no questions.
        var seed = new UsageSeed(app.Services.GetRequiredService<SqliteDatabase>());
        var other = await seed.WorkspaceAsync("摄影史", Now.AddDays(-90));
        var c = await seed.ConversationAsync(other, Now.AddDays(-80));
        await seed.QuestionAsync(c, Now.AddDays(-80));
        await seed.RequestAsync(c, "answer", "m", Now.AddDays(-80), 10, 1);
        page.Refresh();
        await page.Loaded;
        window.UpdateLayout();
        Assert.Equal(UiText.NoRequestsInPeriod(UsagePeriod.Daily), Find<TextBlock>(view, "UsageEmptyText").Text);
        Assert.Equal(["第一次提问：2026年8月24日"], page.Milestones);

        UiInput.Click(window, Find<RadioButton>(view, "ScopeWorkspace"));
        await page.Loaded;
        window.UpdateLayout();
        Assert.Equal(UiText.NoRequestsYet, Find<TextBlock>(view, "UsageEmptyText").Text);
        Assert.Equal(UiText.NoQuestionsInWorkspace, Find<TextBlock>(view, "NoQuestions").Text);
        Assert.Empty(page.Milestones);
        Assert.Equal(workspace.Id, app.Workbench.CurrentWorkspace!.Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Only_the_first_read_shows_the_loading_line()
    {
        var hold = new HeldStatistics();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IUsageStatistics>(hold));
        await app.StartWithWorkspaceAsync("火车历史");
        var window = app.ShowWindow(1100, 820);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();
        UiInput.Click(window, Find<Button>(window, "StatisticsButton"));
        var page = Assert.IsType<UsageStatisticsViewModel>(app.Workbench.CurrentPage);
        window.UpdateLayout();
        var view = window.GetVisualDescendants().OfType<UsageStatisticsView>().Single();
        Assert.True(Find<StackPanel>(view, "StatisticsLoading").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(view, "StatisticsBody").IsEffectivelyVisible);

        hold.Release();
        await page.Loaded;
        window.UpdateLayout();
        Assert.False(Find<StackPanel>(view, "StatisticsLoading").IsEffectivelyVisible);
        Assert.True(Find<StackPanel>(view, "StatisticsBody").IsEffectivelyVisible);

        // A later read keeps the figures on screen; only the small spinner turns.
        hold.Hold();
        page.IsMonthly = true;
        window.UpdateLayout();
        Assert.True(Find<StackPanel>(view, "StatisticsBody").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(view, "StatisticsLoading").IsEffectivelyVisible);
        Assert.True(Find<ActivitySpinner>(view, "StatisticsRefreshing").IsVisible);
        hold.Release();
        await page.Loaded;
        Assert.False(page.IsLoading);
        window.Close();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<(MainWindow Window, UsageStatisticsViewModel Page, UsageStatisticsView View)> OpenAsync(DesktopHarness app)
    {
        var window = app.ShowWindow(1100, 820);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).Loaded;
        window.UpdateLayout();
        UiInput.Click(window, Find<Button>(window, "StatisticsButton"));
        var page = Assert.IsType<UsageStatisticsViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        window.UpdateLayout();
        return (window, page, window.GetVisualDescendants().OfType<UsageStatisticsView>().Single());
    }

    private static int ActivityValue(UsageStatisticsViewModel page, string label) => page.ActivityItems.Single(i => i.Label == label).Value;

    private static List<Button> Buttons(Visual root, string name) => [.. root.GetVisualDescendants().OfType<Button>().Where(b => b.Name == name)];

    private static List<TextBox> Boxes(Visual root, string name) => [.. root.GetVisualDescendants().OfType<TextBox>().Where(b => b.Name == name)];

    private static List<TextBlock> Texts(Visual root, string name) => [.. root.GetVisualDescendants().OfType<TextBlock>().Where(b => b.Name == name)];

    private static async Task SettleAsync(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Yield();
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>Statistics that wait for the test before answering (an empty report).</summary>
    private sealed class HeldStatistics : IUsageStatistics
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async Task<StatisticsReport> LoadAsync(WorkspaceId? workspace, UsagePeriod period, CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            var today = new DateOnly(2026, 11, 12);
            var (from, to) = UsageStatisticsService.Window(period, today);
            var usage = UsageStatisticsService.Summarize(period, from, to, new Application.Storage.UsageRows([], 0));
            return new StatisticsReport(usage, new ActivityFacts(0, 0, 0, 0, 0, 0, 0, 0, 0, null, []), HasModel: true, today);
        }
    }
}
