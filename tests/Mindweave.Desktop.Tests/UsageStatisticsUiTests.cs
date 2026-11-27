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
using Mindweave.Application.Languages;
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

        // Review 2026-11-12: the summary names the parts by position, not by shade: in the dark theme input is the
        // lighter of the two, so "深色为输入" was true only in the light theme.
        Assert.Contains("下段为输入，上段为输出", page.ChartSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("深色", page.ChartSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("浅色", page.ChartSummary, StringComparison.Ordinal);

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
        chart.BringIntoView();   // focusing the price button scrolled down past the usage calendar
        window.UpdateLayout();
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
        await SettingsInput.OpenAsync(window, "StatisticsButton");
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

    /// <summary>
    /// Review 2026-11-12: a price saved just before the period (or scope) switches is the one the new rows show, even
    /// when the write is still under way as the new read starts.
    /// </summary>
    [AvaloniaFact]
    public async Task A_price_saved_just_before_a_switch_is_shown_after_it()
    {
        await using var app = Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var seed = new UsageSeed(app.Services.GetRequiredService<SqliteDatabase>());
        var c = await seed.ConversationAsync(workspace.Id, Now.AddDays(-2));
        await seed.RequestAsync(c, "answer", "deepseek-flash", Now.AddDays(-2), 1_000_000, 500_000, "https://api.deepseek.com");

        var prices = new HeldPrices();
        var statistics = new ThenReleasing(app.Services.GetRequiredService<IUsageStatistics>(), prices);
        var page = new UsageStatisticsViewModel(app.Workbench.CurrentWorkspace!, statistics, prices, app.Workbench);
        await page.Loaded;
        var row = Assert.Single(page.Models);
        Assert.Null(row.Price);

        prices.HoldWrites();
        row.EditPriceCommand.Execute(null);
        row.InputPrice = "2";
        row.OutputPrice = "8";
        var saving = row.SavePriceCommand.ExecuteAsync(null);

        // The write finishes only once the new read has its figures and the page has gone on from there.
        statistics.ReleaseAfterNextLoad = true;
        page.IsMonthly = true;
        await saving;
        await page.Loaded;

        var shown = Assert.Single(page.Models);
        Assert.Equal(new ModelPrice(2, 8), shown.Price);
        Assert.Equal("约 ¥6.00", shown.CostText);
        page.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Recommendation_requests_have_their_own_purpose_row_and_the_other_rows_stay()
    {
        await using var app = Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var seed = new UsageSeed(app.Services.GetRequiredService<SqliteDatabase>());
        var c = await seed.ConversationAsync(workspace.Id, Now.AddDays(-2));
        await seed.RequestAsync(c, "answer", "m", Now.AddDays(-2), 600, 200);
        await seed.WorkspaceRequestAsync(workspace.Id, "m", Now.AddDays(-1), 150, 50, kind: "recommend-questions");
        await seed.WorkspaceRequestAsync(workspace.Id, "m", Now.AddDays(-1), 40, 10, kind: "workspace-name");

        // Link suggestions (2026-11-16, §10.3.4) are a row of their own after 推荐.
        await seed.WorkspaceRequestAsync(workspace.Id, "m", Now.AddDays(-1), 700, 90, kind: "suggest-links");

        var (window, page, view) = await OpenAsync(app);

        Assert.Equal(["回答", "标题", "总结", "命名", "工作区名称", "推荐", "关联建议", "附件摘要"], page.Purposes.Select(p => p.Name));
        var links = page.Purposes.Single(p => p.Purpose == UsagePurpose.LinkSuggestion);
        Assert.Equal((790, UiText.PurposeHint(UsagePurpose.LinkSuggestion)), (links.Counts.Total, links.Hint));
        Assert.StartsWith("关联建议：", links.AutomationName, StringComparison.Ordinal);
        var row = page.Purposes.Single(p => p.Purpose == UsagePurpose.Recommendation);
        Assert.Equal(200, row.Counts.Total);
        Assert.Equal(UiText.PurposeHint(UsagePurpose.Recommendation), row.Hint);
        Assert.StartsWith("推荐：", row.AutomationName, StringComparison.Ordinal);
        Assert.Contains(view.GetVisualDescendants().OfType<Grid>(), g => g.Classes.Contains("purpose-row") && AutomationProperties.GetName(g) == row.AutomationName);
        window.Close();
    }

    /// <summary>
    /// Prompt caching, step 1 (requirements §10.11.4): under each model, the share of input read from the cache, what was
    /// written, how many requests reported it when not all did, and "not reported" for a service that says nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task Each_model_shows_the_share_of_input_read_from_the_cache_or_that_it_was_not_reported()
    {
        await using var app = Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var seed = new UsageSeed(app.Services.GetRequiredService<SqliteDatabase>());
        var c = await seed.ConversationAsync(workspace.Id, Now.AddDays(-2));
        await seed.RequestAsync(c, "answer", "deepseek-flash", Now.AddDays(-2), 2000, 100, "https://api.deepseek.com", cacheRead: 1500);
        await seed.RequestAsync(c, "answer", "deepseek-flash", Now.AddDays(-2), 1000, 50, "https://api.deepseek.com");
        await seed.RequestAsync(c, "answer", "qwen-plus", Now.AddDays(-1), 1000, 10, "https://dashscope.aliyuncs.com/compatible-mode/v1", cacheRead: 0, cacheWrite: 900);
        await seed.RequestAsync(c, "answer", "glm-5", Now.AddDays(-1), 500, 20, "https://open.bigmodel.cn/api/paas/v4");

        var (window, page, view) = await OpenAsync(app);
        Assert.Equal(["deepseek-flash", "qwen-plus", "glm-5"], page.Models.Select(m => m.Key.Model));
        var lines = Texts(view, "ModelCache");
        Assert.All(lines, l => Assert.True(l.IsEffectivelyVisible));
        Assert.Equal(
            ["缓存：输入的 75% 读自缓存 · 2 次请求中 1 次返回", "缓存：输入的 0% 读自缓存 · 写入 900", "缓存：未返回"],
            lines.Select(l => l.Text));

        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(UiLanguage.English);
        try
        {
            Assert.Equal(
                ["Cache: 75% of input read from it · reported by 1 of 2 requests", "Cache: 0% of input read from it · 900 written", "Cache: not reported"],
                page.Models.Select(m => m.CacheText));
        }
        finally
        {
            CurrentLanguage.Set(before);
        }

        window.Close();
    }

    private static async Task<(MainWindow Window, UsageStatisticsViewModel Page, UsageStatisticsView View)> OpenAsync(DesktopHarness app)
    {
        var window = app.ShowWindow(1100, 820);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).Loaded;
        window.UpdateLayout();
        await SettingsInput.OpenAsync(window, "StatisticsButton");
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
            var days = UsageStatisticsService.Buckets(UsagePeriod.Daily, UsageStatisticsService.CalendarStart(today), today, new Application.Storage.UsageRows([], 0));
            return new StatisticsReport(usage, new ActivityFacts(0, 0, 0, 0, 0, 0, 0, 0, 0, null, []), HasModel: true, today, days);
        }
    }

    /// <summary>
    /// The real statistics; after the next read it lets the held price writes finish, queued behind whatever the page
    /// does next with the figures on the UI thread.
    /// </summary>
    private sealed class ThenReleasing(IUsageStatistics inner, HeldPrices prices) : IUsageStatistics
    {
        public bool ReleaseAfterNextLoad { get; set; }

        public async Task<StatisticsReport> LoadAsync(WorkspaceId? workspace, UsagePeriod period, CancellationToken cancellationToken)
        {
            var report = await inner.LoadAsync(workspace, period, cancellationToken);
            if (ReleaseAfterNextLoad)
            {
                ReleaseAfterNextLoad = false;
                Dispatcher.UIThread.Post(prices.ReleaseWrites, DispatcherPriority.Background);
            }

            return report;
        }
    }

    /// <summary>Prices in memory whose writes wait for the test (a slow disk) while reads answer at once.</summary>
    private sealed class HeldPrices : IUsagePrices
    {
        private readonly Dictionary<ModelKey, ModelPrice> _prices = [];
        private TaskCompletionSource _writes = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HeldPrices() => _writes.SetResult();

        public void HoldWrites() => _writes = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseWrites() => _writes.TrySetResult();

        public Task<string> GetCurrencyAsync(CancellationToken cancellationToken) => Task.FromResult(UsagePricing.DefaultCurrency);

        public Task SetCurrencyAsync(string symbol, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ModelPrice?> GetAsync(ModelKey key, CancellationToken cancellationToken) =>
            Task.FromResult(_prices.TryGetValue(key, out var price) ? price : null);

        public async Task SetAsync(ModelKey key, ModelPrice? price, CancellationToken cancellationToken)
        {
            await _writes.Task.WaitAsync(cancellationToken);
            if (price is null)
            {
                _prices.Remove(key);
            }
            else
            {
                _prices[key] = price;
            }
        }
    }
}
