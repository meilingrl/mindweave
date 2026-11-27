using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Statistics;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// 设置 › 使用统计 (2026-11-12): token use as the services reported it, by day or month, by model and by purpose, with
/// costs from the user's own unit prices; and plain activity facts and milestones. All workspaces or the open one. Read
/// off the UI thread with aggregate queries; while a newer read is under way the figures already shown stay, and only
/// the very first read shows a loading line. Read again when the page is shown again.
/// </summary>
public sealed partial class UsageStatisticsViewModel : PageViewModel, IShowsWorkspaceName, IDisposable
{
    private readonly IUsageStatistics _statistics;
    private readonly IUsagePrices _prices;
    private readonly IContentNavigator _content;
    private readonly CancellationTokenSource _disposed = new();
    private int _generation;
    private bool _shown;
    private bool _disposedFlag;

    public UsageStatisticsViewModel(Workspace workspace, IUsageStatistics statistics, IUsagePrices prices, IContentNavigator content)
        : base(UsageStatisticsRoute.Instance)
    {
        Workspace = workspace;
        _statistics = statistics;
        _prices = prices;
        _content = content;
        Loaded = LoadAsync();
    }

    public Workspace Workspace { get; private set; }

    public override string Title => UiText.StatisticsTitle;

    /// <summary>The most recent read (tests await it; the page never does).</summary>
    internal Task Loaded { get; private set; }

    /// <summary>The last price or currency write.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    // ── Scope and period ────────────────────────────────────────────────────

    /// <summary>All workspaces (the default) or only the open one; the token and activity sections both follow it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllScope), nameof(IsWorkspaceScope), nameof(ScopeCaption))]
    public partial bool AllWorkspaces { get; set; } = true;

    public bool IsAllScope
    {
        get => AllWorkspaces;
        set
        {
            if (value)
            {
                AllWorkspaces = true;
            }
        }
    }

    public bool IsWorkspaceScope
    {
        get => !AllWorkspaces;
        set
        {
            if (value)
            {
                AllWorkspaces = false;
            }
        }
    }

    public string CurrentWorkspaceName => UiText.ScopeCurrentWorkspaceName(Workspace.Name.Value);

    public string ScopeCaption => UiText.ScopeCaption(AllWorkspaces, Workspace.Name.Value);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDaily), nameof(IsMonthly), nameof(TableHeader))]
    public partial UsagePeriod Period { get; set; } = UsagePeriod.Daily;

    public bool IsDaily
    {
        get => Period == UsagePeriod.Daily;
        set
        {
            if (value)
            {
                Period = UsagePeriod.Daily;
            }
        }
    }

    public bool IsMonthly
    {
        get => Period == UsagePeriod.Monthly;
        set
        {
            if (value)
            {
                Period = UsagePeriod.Monthly;
            }
        }
    }

    /// <summary>The chart's text equivalent instead of the chart (以表格查看).</summary>
    [ObservableProperty]
    public partial bool ShowsTable { get; set; }

    // ── Loading ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLoadingLine))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Figures have been shown at least once; later reads replace them in place.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLoadingLine))]
    public partial bool HasReport { get; private set; }

    public bool ShowsLoadingLine => IsLoading && !HasReport;

    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    // ── Token use ───────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string RangeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string InputText { get; private set; } = "0";

    [ObservableProperty]
    public partial string OutputText { get; private set; } = "0";

    [ObservableProperty]
    public partial string RequestsText { get; private set; } = "0";

    /// <summary>"另有 N 次请求没有用量记录（服务未返回，或请求没有送达），未计入"; null when every request reported its usage.</summary>
    [ObservableProperty]
    public partial string? UnreportedText { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<UsageBarViewModel> Bars { get; private set; } = [];

    [ObservableProperty]
    public partial string ChartSummary { get; private set; } = string.Empty;

    /// <summary>The last year of daily use, a square a day (2026-11-17); follows the scope, not the period.</summary>
    [ObservableProperty]
    public partial UsageCalendarViewModel Calendar { get; private set; } = UsageCalendarViewModel.Empty;

    [ObservableProperty]
    public partial string CalendarRangeText { get; private set; } = string.Empty;

    /// <summary>The calendar shows once the scope has had any request; before that the empty-usage words say enough.</summary>
    [ObservableProperty]
    public partial bool HasCalendar { get; private set; }

    public string TableHeader => Period == UsagePeriod.Daily ? UiText.TableDayHeader : UiText.TableMonthHeader;

    /// <summary>Some request in the period: the totals, chart and splits are shown; otherwise <see cref="EmptyUsageText"/>.</summary>
    [ObservableProperty]
    public partial bool HasUsage { get; private set; }

    [ObservableProperty]
    public partial string? EmptyUsageText { get; private set; }

    /// <summary>No model is configured: the empty state offers the way to the model settings.</summary>
    [ObservableProperty]
    public partial bool ShowsModelSetup { get; private set; }

    public ObservableCollection<ModelUsageRowViewModel> Models { get; } = [];

    public ObservableCollection<PurposeRowViewModel> Purposes { get; } = [];

    /// <summary>工具 (requirements §10.4.4): answers that used tools in the period, and each tool's calls.</summary>
    [ObservableProperty]
    public partial bool HasToolUsage { get; private set; }

    /// <summary>"使用了工具的回答 3 次 · 平均每次 2.7 次模型请求".</summary>
    [ObservableProperty]
    public partial string? ToolRunsText { get; private set; }

    public ObservableCollection<ToolCallRowViewModel> ToolCalls { get; } = [];

    // ── Prices ──────────────────────────────────────────────────────────────

    /// <summary>The symbol costs are written with (stored, or the default).</summary>
    [ObservableProperty]
    public partial string Currency { get; private set; } = UsagePricing.DefaultCurrency;

    /// <summary>What is typed in the currency field; checked as typed, stored when valid.</summary>
    [ObservableProperty]
    public partial string CurrencyInput { get; set; } = UsagePricing.DefaultCurrency;

    [ObservableProperty]
    public partial string? CurrencyError { get; private set; }

    /// <summary>"按单价估算的费用：约 ¥12.34"; null when no model of the period has a price.</summary>
    [ObservableProperty]
    public partial string? TotalCostText { get; private set; }

    // ── Activity ────────────────────────────────────────────────────────────

    public ObservableCollection<ActivityItemViewModel> ActivityItems { get; } = [];

    [ObservableProperty]
    public partial string ActiveDaysText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? FirstActiveDayText { get; private set; }

    public ObservableCollection<string> Milestones { get; } = [];

    [ObservableProperty]
    public partial bool HasQuestions { get; private set; }

    /// <summary>Something to count at all; without it the activity card only says there are no questions yet.</summary>
    [ObservableProperty]
    public partial bool HasActivity { get; private set; }

    [ObservableProperty]
    public partial string? NoQuestionsText { get; private set; }

    /// <summary>The page is shown (again): the first showing is the read the page started with, later ones read again.</summary>
    public void Refresh()
    {
        if (!_shown)
        {
            _shown = true;
            return;
        }

        Reload();
    }

    public void WorkspaceRenamed(Workspace renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);
        Workspace = renamed;
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(CurrentWorkspaceName));
        OnPropertyChanged(nameof(ScopeCaption));
    }

    public void Dispose()
    {
        if (_disposedFlag)
        {
            return;
        }

        _disposedFlag = true;
        _disposed.Cancel();
        _disposed.Dispose();
    }

    [RelayCommand]
    private void Retry() => Reload();

    /// <summary>No model yet: back to 设置, where the model connection is.</summary>
    [RelayCommand]
    private void GoToModelSettings() => _content.GoBack();

    partial void OnAllWorkspacesChanged(bool value) => Reload();

    partial void OnPeriodChanged(UsagePeriod value) => Reload();

    partial void OnCurrencyInputChanged(string value)
    {
        var (symbol, problem) = UsagePricing.ParseCurrency(value);
        if (problem is { } p || symbol is null)
        {
            CurrencyError = UiText.PriceProblemText(problem ?? PriceProblem.CurrencyTooLong);
            return;
        }

        CurrencyError = null;
        if (symbol == Currency)
        {
            return;
        }

        Currency = symbol;
        foreach (var row in Models)
        {
            row.Currency = symbol;
        }

        UpdateTotalCost();
        Saving = SaveCurrencyAsync(symbol, Saving);
    }

    private async Task SaveCurrencyAsync(string symbol, Task previous)
    {
        await previous;
        try
        {
            await _prices.SetCurrencyAsync(symbol, CancellationToken.None);
        }
#pragma warning disable CA1031 // A lost currency choice is said beside the field; the page keeps showing it.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            CurrencyError = UiText.CurrencySaveFailed;
        }
    }

    private void Reload()
    {
        if (!_disposedFlag)
        {
            Loaded = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        var generation = ++_generation;
        var workspace = AllWorkspaces ? (WorkspaceId?)null : Workspace.Id;
        var period = Period;
        IsLoading = true;
        try
        {
            var token = _disposed.Token;
            var report = await _statistics.LoadAsync(workspace, period, token);

            // A price or symbol saved just before this read is the one it shows (the writes never throw).
            await Saving;
            var currency = await _prices.GetCurrencyAsync(token);
            var prices = new Dictionary<ModelKey, ModelPrice?>();
            foreach (var model in report.Usage.ByModel)
            {
                prices[model.Key] = await _prices.GetAsync(model.Key, token);
            }

            if (generation != _generation || _disposedFlag)
            {
                return;
            }

            Apply(report, currency, prices);
            LoadError = null;
            HasReport = true;
        }
        catch (OperationCanceledException) when (_disposedFlag)
        {
        }
#pragma warning disable CA1031 // A failed read is said on the page, with 重试; the figures shown before stay.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            if (generation == _generation && !_disposedFlag)
            {
                LoadError = UiText.StatisticsLoadFailed;
            }
        }
        finally
        {
            if (generation == _generation && !_disposedFlag)
            {
                IsLoading = false;
            }
        }
    }

    private void Apply(StatisticsReport report, string currency, Dictionary<ModelKey, ModelPrice?> prices)
    {
        var usage = report.Usage;
        RangeText = UiText.PeriodRange(usage.Period, usage.From, usage.To);
        InputText = UiText.Tokens(usage.Totals.Input);
        OutputText = UiText.Tokens(usage.Totals.Output);
        RequestsText = UiText.RequestCount(usage.Totals.Requests);
        UnreportedText = usage.Totals.Unreported > 0 ? UiText.UnreportedRequests(usage.Totals.Unreported) : null;
        Bars = UsageBarViewModel.From(usage);
        ChartSummary = UiText.ChartSummary(usage.Period, usage.Buckets);
        Calendar = UsageCalendarViewModel.From(report.Days);
        CalendarRangeText = report.Days.Count > 0 ? UiText.CalendarRange(report.Days[0].Start, report.Days[^1].Start) : string.Empty;
        HasCalendar = usage.RequestsEver > 0 && report.Days.Count > 0;

        var inPeriod = usage.Totals.Requests + usage.Totals.Unreported;
        HasUsage = inPeriod > 0;
        ShowsModelSetup = !report.HasModel && usage.RequestsEver == 0;
        EmptyUsageText = HasUsage ? null
            : ShowsModelSetup ? UiText.NoModelConfigured
            : usage.RequestsEver == 0 ? UiText.NoRequestsYet
            : UiText.NoRequestsInPeriod(usage.Period);

        // The stored symbol, unless the reader is typing a different one right now.
        if (CurrencyError is null && CurrencyInput == Currency)
        {
            Currency = currency;
            CurrencyInput = currency;
        }

        Models.Clear();
        foreach (var model in usage.ByModel)
        {
            Models.Add(new ModelUsageRowViewModel(model, usage.Totals.Total, prices.GetValueOrDefault(model.Key), Currency, SavePriceAsync));
        }

        Purposes.Clear();
        foreach (var purpose in usage.ByPurpose)
        {
            Purposes.Add(new PurposeRowViewModel(purpose, usage.Totals.Total));
        }

        HasToolUsage = report.Tools.Runs > 0;
        ToolRunsText = HasToolUsage ? UiText.ToolRuns(report.Tools.Runs, report.Tools.RequestsPerRun) : null;
        ToolCalls.Clear();
        foreach (var call in report.Tools.Calls)
        {
            ToolCalls.Add(new ToolCallRowViewModel(UiText.ToolName(call.Tool, call.Server), UiText.ToolCallCount(call.Calls)));
        }

        UpdateTotalCost();

        var activity = report.Activity;
        ActivityItems.Clear();
        foreach (var (label, value) in new[]
        {
            (UiText.QuestionsLabel, activity.Questions),
            (UiText.ConversationsLabel, activity.Conversations),
            (UiText.BranchesLabel, activity.Branches),
            (UiText.SummariesLabel, activity.Summaries),
            (UiText.SummaryVersionsLabel, activity.SummaryVersions),
            (UiText.ReadSummariesLabel, activity.ReadSummaries),
            (UiText.ArchivedConversationsLabel, activity.ArchivedConversations),
        })
        {
            ActivityItems.Add(new ActivityItemViewModel(label, value));
        }

        ActiveDaysText = UiText.ActiveDaysText(activity.RecentActiveDays, activity.ActiveDays);
        FirstActiveDayText = activity.FirstActiveDay is { } first ? UiText.FirstActiveDay(first) : null;
        Milestones.Clear();
        foreach (var milestone in activity.Milestones)
        {
            Milestones.Add(UiText.MilestoneText(milestone));
        }

        HasQuestions = activity.Questions > 0;
        HasActivity = HasQuestions || activity.Conversations + activity.Summaries > 0;
        NoQuestionsText = HasQuestions ? null : AllWorkspaces ? UiText.NoQuestionsAtAll : UiText.NoQuestionsInWorkspace;
    }

    private async Task<bool> SavePriceAsync(ModelUsageRowViewModel row, ModelPrice? price)
    {
        var previous = Saving;
        var saving = SaveAsync();
        Saving = saving;
        return await saving;

        async Task<bool> SaveAsync()
        {
            await previous;
            try
            {
                await _prices.SetAsync(row.Key, price, CancellationToken.None);
            }
#pragma warning disable CA1031 // Said beside the price; nothing changes on the page.
            catch (Exception swallowed)
#pragma warning restore CA1031
            {
                DesktopLog.Swallowed(swallowed);
                return false;
            }

            row.Price = price;
            UpdateTotalCost();
            return true;
        }
    }

    private void UpdateTotalCost()
    {
        var priced = Models.Where(m => m.Price is not null).ToList();
        TotalCostText = priced.Count == 0
            ? null
            : UiText.TotalCost(Currency, priced.Sum(m => m.Price!.CostOf(m.Counts)), Models.Count - priced.Count);
    }
}

/// <summary>One bar of the chart: a day or a month, with its words for the hover card, the table and a screen reader.</summary>
public sealed record UsageBarViewModel(string AxisLabel, string Name, long Input, long Output, int Requests, int Unreported, string Detail, bool ShowsAxisLabel)
{
    public long Total => Input + Output;

    public string InputText => UiText.Tokens(Input);

    public string OutputText => UiText.Tokens(Output);

    public string RequestsText => UiText.TableRequests(Requests, Unreported);

    public static IReadOnlyList<UsageBarViewModel> From(UsageReport usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var count = usage.Buckets.Count;

        // Days: every seventh label counted back from today, so today's is always there; months: every one.
        return usage.Buckets.Select((b, i) => new UsageBarViewModel(
            UiText.AxisLabel(usage.Period, b.Start),
            UiText.BucketName(usage.Period, b.Start),
            b.Counts.Input,
            b.Counts.Output,
            b.Counts.Requests,
            b.Counts.Unreported,
            UiText.BarDetail(usage.Period, b.Start, b.Counts),
            usage.Period == UsagePeriod.Monthly || (count - 1 - i) % 7 == 0)).ToList();
    }
}

/// <summary>One model (name and service host) of the period, with the user's unit price and the cost it gives.</summary>
public sealed partial class ModelUsageRowViewModel : ObservableObject
{
    private readonly Func<ModelUsageRowViewModel, ModelPrice?, Task<bool>> _save;

    public ModelUsageRowViewModel(ModelUsage usage, long periodTotal, ModelPrice? price, string currency, Func<ModelUsageRowViewModel, ModelPrice?, Task<bool>> save)
    {
        ArgumentNullException.ThrowIfNull(usage);
        Key = usage.Key;
        Counts = usage.Counts;
        ShareText = UiText.Share(usage.Counts.Total, periodTotal);
        ShareFraction = periodTotal > 0 ? (double)usage.Counts.Total / periodTotal : 0;
        Price = price;
        Currency = currency;
        _save = save;
    }

    public ModelKey Key { get; }

    public TokenCounts Counts { get; }

    public string Label => UiText.ModelRowLabel(Key);

    public string UsageText => UiText.UsageLine(Counts);

    /// <summary>How much of the input the provider's cache served, or that it did not say (requirements §10.11.4).</summary>
    public string CacheText => UiText.CacheLine(Counts);

    public string ShareText { get; }

    /// <summary>The model's part of the period's tokens, 0 to 1, for the thin share bar.</summary>
    public double ShareFraction { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CostText), nameof(PriceText), nameof(HasPrice))]
    public partial ModelPrice? Price { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CostText), nameof(PriceText))]
    public partial string Currency { get; set; } = UsagePricing.DefaultCurrency;

    public bool HasPrice => Price is not null;

    /// <summary>"约 ¥1.23", or null without a price.</summary>
    public string? CostText => Price is { } price ? UiText.CostOf(Currency, price.CostOf(Counts)) : null;

    public string PriceText => UiText.PriceSummary(Currency, Price);

    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    public partial string InputPrice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OutputPrice { get; set; } = string.Empty;

    /// <summary>Why the typed prices were not saved (shown under the fields); null otherwise.</summary>
    [ObservableProperty]
    public partial string? PriceError { get; private set; }

    public string SetPriceName => UiText.SetPriceName(Key);

    public string InputPriceName => UiText.PriceFieldName(Key, input: true);

    public string OutputPriceName => UiText.PriceFieldName(Key, input: false);

    [RelayCommand]
    private void EditPrice()
    {
        InputPrice = Price is { } p ? UsagePricing.Format(p.InputPerMillion) : string.Empty;
        OutputPrice = Price is { } q ? UsagePricing.Format(q.OutputPerMillion) : string.Empty;
        PriceError = null;
        IsEditing = true;
    }

    [RelayCommand]
    private async Task SavePriceAsync()
    {
        var (price, problem, _) = UsagePricing.ParsePrices(InputPrice, OutputPrice);
        if (problem is { } p)
        {
            PriceError = UiText.PriceProblemText(p);
            return;
        }

        if (!await _save(this, price))
        {
            PriceError = UiText.PriceSaveFailed;
            return;
        }

        PriceError = null;
        IsEditing = false;
    }

    [RelayCommand]
    private void CancelPrice()
    {
        PriceError = null;
        IsEditing = false;
    }

    [RelayCommand]
    private async Task ClearPriceAsync()
    {
        InputPrice = string.Empty;
        OutputPrice = string.Empty;
        await SavePriceAsync();
    }
}

/// <summary>One purpose (回答, 标题, …) of the period.</summary>
/// <summary>One tool's calls in the period, on the statistics page.</summary>
public sealed record ToolCallRowViewModel(string Name, string CallsText);

public sealed class PurposeRowViewModel(PurposeUsage usage, long periodTotal)
{
    public UsagePurpose Purpose { get; } = usage?.Purpose ?? throw new ArgumentNullException(nameof(usage));

    public TokenCounts Counts { get; } = usage.Counts;

    public string Name => UiText.PurposeName(Purpose);

    public string Hint => UiText.PurposeHint(Purpose);

    public string UsageText => Counts.Requests + Counts.Unreported == 0 ? UiText.NoRequests : UiText.UsageLine(Counts);

    public string ShareText { get; } = UiText.Share(usage.Counts.Total, periodTotal);

    public bool IsEmpty => Counts.Requests + Counts.Unreported == 0;

    public string AutomationName => UiText.PurposeRowName(Name, UsageText, ShareText);
}

/// <summary>One activity count: "提问 128".</summary>
public sealed class ActivityItemViewModel(string label, int value)
{
    public string Label { get; } = label;

    public string ValueText { get; } = value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public int Value { get; } = value;

    public string AutomationName => UiText.ActivityItemName(Label, Value);
}
