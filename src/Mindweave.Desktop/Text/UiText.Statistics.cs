using System.Globalization;
using Mindweave.Application.Statistics;

namespace Mindweave.Desktop.Text;

// 设置 › 使用统计 (2026-11-12): token use by day or month, by model and by purpose, optional unit prices, and plain
// activity facts. Facts only: nothing here rates learning (requirements §5.4, §10).
public static partial class UiText
{
    public const string StatisticsTitle = "使用统计";
    public const string StatisticsLink = "使用统计";

    // Scope and period
    public const string StatisticsScopeLabel = "范围";
    public const string ScopeAllWorkspaces = "全部工作区";
    public const string ScopeCurrentWorkspace = "当前工作区";
    public const string PeriodDaily = "按日";
    public const string PeriodMonthly = "按月";
    public const string StatisticsPeriodLabel = "时间";

    public static string ScopeCurrentWorkspaceName(string workspace) =>
        string.Format(CultureInfo.CurrentCulture, "当前工作区（{0}）", workspace);

    public static string ScopeCaption(bool all, string workspace) => all
        ? "下面的用量和活动包括所有工作区。"
        : string.Format(CultureInfo.CurrentCulture, "下面的用量和活动只包括工作区“{0}”。", workspace);

    // Loading
    public const string StatisticsLoading = "正在统计…";
    public const string StatisticsLoadFailed = "统计暂时无法读取，请稍后再试。";
    public const string StatisticsRetry = "重试";

    // Token use
    public const string TokenUsageSection = "token 用量";
    public const string InputTokensLabel = "输入 token";
    public const string OutputTokensLabel = "输出 token";
    public const string RequestsLabel = "请求";
    public const string ChartInput = "输入";
    public const string ChartOutput = "输出";
    public const string UsageChartName = "token 用量图";
    public const string ShowAsTable = "以表格查看";
    public const string TableDayHeader = "日期";
    public const string TableMonthHeader = "月份";
    public const string TableInputHeader = "输入";
    public const string TableOutputHeader = "输出";
    public const string TableRequestsHeader = "请求";

    /// <summary>What the totals count, and that billed failures are in them.</summary>
    public const string UsageBilledCaption = "按服务返回的用量统计。失败或中途停止的请求，只要服务返回了用量也计入（服务同样会计费）。";

    public static string PeriodRange(UsagePeriod period, DateOnly from, DateOnly to) => period == UsagePeriod.Daily
        ? string.Format(CultureInfo.CurrentCulture, "最近 {0} 天（{1}–{2}）", UsageStatisticsService.RecentDays, ShortDay(from), ShortDay(to))
        : string.Format(CultureInfo.CurrentCulture, "最近 {0} 个月（{1}–{2}）", UsageStatisticsService.RecentMonths, Month(from), Month(to));

    public static string UnreportedRequests(int count) =>
        string.Format(CultureInfo.CurrentCulture, "另有 {0} 次请求没有用量记录（服务未返回，或请求没有送达），未计入", count);

    public static string RequestCount(int count) => string.Format(CultureInfo.CurrentCulture, "{0:N0} 次", count);

    public static string Tokens(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"11月3日"</summary>
    public static string ShortDay(DateOnly day) => day.ToString("M'月'd'日'", CultureInfo.InvariantCulture);

    /// <summary>"2026年11月3日"</summary>
    public static string FullDay(DateOnly day) => day.ToString("yyyy'年'M'月'd'日'", CultureInfo.InvariantCulture);

    /// <summary>"2026年11月"</summary>
    public static string Month(DateOnly month) => month.ToString("yyyy'年'M'月'", CultureInfo.InvariantCulture);

    /// <summary>A bar's own label under the chart: "10/14" for a day, "3月" for a month.</summary>
    public static string AxisLabel(UsagePeriod period, DateOnly start) => period == UsagePeriod.Daily
        ? start.ToString("M'/'d", CultureInfo.InvariantCulture)
        : start.ToString("M'月'", CultureInfo.InvariantCulture);

    /// <summary>A bucket's name in the table and the hover card: "11月3日" or "2026年11月".</summary>
    public static string BucketName(UsagePeriod period, DateOnly start) => period == UsagePeriod.Daily ? ShortDay(start) : Month(start);

    /// <summary>One bar in words: "11月3日：输入 12,345，输出 2,345，3 次请求".</summary>
    public static string BarDetail(UsagePeriod period, DateOnly start, TokenCounts counts)
    {
        var text = counts.Requests == 0 && counts.Unreported == 0
            ? string.Format(CultureInfo.CurrentCulture, "{0}：{1}", BucketName(period, start), NoRequests)
            : string.Format(CultureInfo.CurrentCulture, "{0}：输入 {1}，输出 {2}，{3} 次请求", BucketName(period, start), Tokens(counts.Input), Tokens(counts.Output), counts.Requests);
        return counts.Unreported > 0 ? text + string.Format(CultureInfo.CurrentCulture, "，另有 {0} 次未返回用量", counts.Unreported) : text;
    }

    /// <summary>The chart as a screen reader hears it, with the way to every number.</summary>
    public static string ChartSummary(UsagePeriod period, IReadOnlyList<UsageBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var unit = period == UsagePeriod.Daily ? "天" : "个月";
        var busiest = buckets.MaxBy(b => b.Counts.Total);
        var empty = buckets.Count(b => b.Counts.Total == 0);
        if (busiest is null || busiest.Counts.Total == 0)
        {
            return string.Format(CultureInfo.CurrentCulture, "最近 {0} {1}都没有用量。", buckets.Count, unit);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "最近 {0} {1}的 token 用量，每根柱子下段为输入，上段为输出。用量最多的是 {2}（{3}）；{4} {1}没有用量。用左右方向键逐个查看，或打开“以表格查看”。",
            buckets.Count,
            unit,
            BucketName(period, busiest.Start),
            Tokens(busiest.Counts.Total),
            empty);
    }

    public static string ChartMaxLabel(long max) => "最高 " + Tokens(max);

    // Usage calendar (2026-11-17): the last year of daily token use, one square a day.
    public const string CalendarSection = "每日 token 用量";
    public const string CalendarName = "每日 token 用量日历";
    public const string CalendarLess = "少";
    public const string CalendarMore = "多";

    /// <summary>The weekday labels beside the rows, Monday on top; only every other row is labelled, as on a wall calendar's margin.</summary>
    public static IReadOnlyList<string> CalendarWeekdayLabels { get; } = ["一", "", "三", "", "五", "", ""];

    /// <summary>"11月", over the column a month starts in.</summary>
    public static string CalendarMonthLabel(DateOnly month) => month.ToString("M'月'", CultureInfo.InvariantCulture);

    /// <summary>"最近一年（2025年11月17日–2026年11月17日），每格一天，颜色越深用量越多"</summary>
    public static string CalendarRange(DateOnly from, DateOnly to) =>
        string.Format(CultureInfo.CurrentCulture, "最近一年（{0}–{1}），每格一天，颜色越深用量越多", FullDay(from), FullDay(to));

    /// <summary>One square in words: "2026年11月3日：输入 12,345，输出 2,345，3 次请求".</summary>
    public static string CalendarDayDetail(DateOnly day, TokenCounts counts)
    {
        var text = counts.Requests == 0 && counts.Unreported == 0
            ? string.Format(CultureInfo.CurrentCulture, "{0}：{1}", FullDay(day), NoRequests)
            : string.Format(CultureInfo.CurrentCulture, "{0}：输入 {1}，输出 {2}，{3} 次请求", FullDay(day), Tokens(counts.Input), Tokens(counts.Output), counts.Requests);
        return counts.Unreported > 0 ? text + string.Format(CultureInfo.CurrentCulture, "，另有 {0} 次未返回用量", counts.Unreported) : text;
    }

    /// <summary>The calendar as a screen reader hears it, with the way to every day.</summary>
    public static string CalendarSummary(IReadOnlyList<UsageBucket> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var busiest = days.MaxBy(b => b.Counts.Total);
        if (busiest is null || busiest.Counts.Total == 0)
        {
            return string.Format(CultureInfo.CurrentCulture, "最近 {0} 天都没有用量。", days.Count);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "最近 {0} 天每天的 token 用量，每列一周，从周一到周日，颜色越深用量越多。{1} 天有用量，最多的是 {2}（{3}）。用方向键逐日查看：左右换一周，上下换一天。",
            days.Count,
            days.Count(b => b.Counts.Total > 0),
            FullDay(busiest.Start),
            Tokens(busiest.Counts.Total));
    }

    /// <summary>A day, month or purpose without any request.</summary>
    public const string NoRequests = "没有请求";

    /// <summary>The hover card's line for requests whose service returned no usage.</summary>
    public static string BarUnreported(int count) => string.Format(CultureInfo.CurrentCulture, "另有 {0} 次未返回用量", count);

    /// <summary>A purpose row as a screen reader hears it: "回答：输入 … · 3 次请求，占 45%".</summary>
    public static string PurposeRowName(string name, string usage, string share) =>
        string.Format(CultureInfo.CurrentCulture, "{0}：{1}，占 {2}", name, usage, share);

    public static string NoRequestsYet => "还没有向模型发出过请求。提问、生成标题和总结后，服务返回的用量会出现在这里。";

    public static string NoRequestsInPeriod(UsagePeriod period) => period == UsagePeriod.Daily
        ? string.Format(CultureInfo.CurrentCulture, "最近 {0} 天没有请求。", UsageStatisticsService.RecentDays)
        : string.Format(CultureInfo.CurrentCulture, "最近 {0} 个月没有请求。", UsageStatisticsService.RecentMonths);

    public const string NoModelConfigured = "还没有配置模型。配置后，每次请求服务返回的 token 用量会记在这里。";
    public const string GoToModelSettings = "前往模型设置";

    // By model, and prices
    public const string ByModelSection = "按模型";
    public const string ByPurposeSection = "按用途";
    public const string CurrencyLabel = "货币符号";
    public const string InputPriceLabel = "每百万输入 token";
    public const string OutputPriceLabel = "每百万输出 token";
    public const string SetPrice = "单价…";
    public const string SavePrice = "保存";
    public const string CancelPrice = "取消";
    public const string ClearPrice = "清除单价";
    public const string PriceSaveFailed = "单价没能保存，请稍后再试。";
    public const string CurrencySaveFailed = "货币符号没能保存，本次仍按新符号显示。";

    /// <summary>The caption under every cost (the user's own prices; nothing built in or fetched).</summary>
    public const string CostCaption = "按你填写的单价估算，不含服务商的其他计费规则（如缓存、折扣）。";

    public const string PriceHint = "单价按每百万 token 填写，只用于本页估算，不会发送到任何地方。两项都留空即不估算。";

    public static string ModelRowLabel(ModelKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Model + " · " + key.Host;
    }

    /// <summary>"输入 1,234 · 输出 567 · 12 次请求", "· 1 次未返回用量" after it; only that when no request reported usage.</summary>
    public static string UsageLine(TokenCounts counts) => counts.Requests == 0
        ? string.Format(CultureInfo.CurrentCulture, "{0} 次请求未返回用量", counts.Unreported)
        : string.Format(CultureInfo.CurrentCulture, "输入 {0} · 输出 {1} · {2} 次请求", Tokens(counts.Input), Tokens(counts.Output), counts.Requests)
          + (counts.Unreported > 0 ? string.Format(CultureInfo.CurrentCulture, " · {0} 次未返回用量", counts.Unreported) : string.Empty);

    /// <summary>A table cell: "12 次", or "12 次（1 次未返回用量）".</summary>
    public static string TableRequests(int requests, int unreported) => unreported > 0
        ? string.Format(CultureInfo.CurrentCulture, "{0:N0} 次（{1} 次未返回用量）", requests, unreported)
        : RequestCount(requests);

    /// <summary>A share of the period's tokens: "45%", "<1%" for a little, "—" for none.</summary>
    public static string Share(long part, long whole) =>
        whole <= 0 || part <= 0 ? "—"
        : part * 100 < whole ? "<1%"
        : string.Format(CultureInfo.InvariantCulture, "{0:0}%", Math.Round(part * 100.0 / whole, MidpointRounding.AwayFromZero));

    public static string Money(string currency, decimal amount) =>
        currency + Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("N2", CultureInfo.InvariantCulture);

    public static string CostOf(string currency, decimal amount) => "约 " + Money(currency, amount);

    public static string PriceSummary(string currency, ModelPrice? price) => price is null
        ? "未填单价"
        : string.Format(CultureInfo.CurrentCulture, "单价：输入 {0}{1} · 输出 {0}{2}（每百万 token）", currency, UsagePricing.Format(price.InputPerMillion), UsagePricing.Format(price.OutputPerMillion));

    public static string TotalCost(string currency, decimal amount, int unpriced) =>
        string.Format(CultureInfo.CurrentCulture, "按单价估算的费用：约 {0}", Money(currency, amount))
        + (unpriced > 0 ? string.Format(CultureInfo.CurrentCulture, "（另有 {0} 个模型未填单价，未计入）", unpriced) : string.Empty);

    public static string SetPriceName(ModelKey key) => string.Format(CultureInfo.CurrentCulture, "设置 {0} 的单价", ModelRowLabel(key));

    public static string PriceFieldName(ModelKey key, bool input) =>
        string.Format(CultureInfo.CurrentCulture, "{0} 的{1}单价", ModelRowLabel(key), input ? "每百万输入 token " : "每百万输出 token ");

    public static string PriceProblemText(PriceProblem problem) => problem switch
    {
        PriceProblem.NotANumber => "请输入数字，例如 2 或 0.5。",
        PriceProblem.Negative => "单价不能为负数。",
        PriceProblem.TooLarge => "单价过大，请检查是否多输了位数。",
        PriceProblem.Incomplete => "请同时填写两项单价（不收费的一项可填 0）。",
        PriceProblem.CurrencyTooLong => "货币符号最多 3 个字符。",
        _ => "无法识别。",
    };

    public static string PurposeName(UsagePurpose purpose) => purpose switch
    {
        UsagePurpose.Answer => "回答",
        UsagePurpose.Title => "标题",
        UsagePurpose.Summary => "总结",
        UsagePurpose.Naming => "命名",
        UsagePurpose.WorkspaceName => "工作区名称",
        UsagePurpose.Recommendation => "推荐",
        UsagePurpose.LinkSuggestion => "关联建议",
        _ => "其他",
    };

    public static string PurposeHint(UsagePurpose purpose) => purpose switch
    {
        UsagePurpose.Answer => "回答问题，含较长会话中压缩较早的上下文",
        UsagePurpose.Title => "自动生成会话标题",
        UsagePurpose.Summary => "生成总结",
        UsagePurpose.Naming => "为同名的总结取区分名称",
        UsagePurpose.WorkspaceName => "建议工作区名称，含后台准备",
        UsagePurpose.Recommendation => "推荐问题与推荐节点，含后台准备",
        UsagePurpose.LinkSuggestion => "AI 建议关联，只在点“建议关联”时请求",
        _ => "新版本记录的其他用途",
    };

    // Activity
    public const string ActivitySection = "活动";
    public const string MilestonesSection = "里程碑";
    public const string QuestionsLabel = "提问";
    public const string ConversationsLabel = "会话";
    public const string BranchesLabel = "分支";
    public const string SummariesLabel = "总结";
    public const string SummaryVersionsLabel = "总结版本";
    public const string ReadSummariesLabel = "已读的总结";
    public const string ArchivedConversationsLabel = "归档的会话";
    public const string ActiveDaysLabel = "活跃天数";
    public const string NoQuestionsInWorkspace = "这个工作区还没有提问。";
    public const string NoQuestionsAtAll = "还没有提问。";

    /// <summary>What the counts are and are not: top-level conversations, branches apart, deleted content not counted.</summary>
    public const string ActivityCaption = "会话只算顶层会话，分支另计；有一次提问的那一天算一个活跃天数，按本机时区计。已删除的内容不计入。";

    /// <summary>Why deleting lowers the usage (2026-11-12): request records go with what they were made for.</summary>
    public const string DeletedUsageCaption = "已删除的会话和工作区的用量不再计入：它们的请求记录随之删除。";

    public static string ActiveDaysText(int recent, int all) =>
        string.Format(CultureInfo.CurrentCulture, "最近 {0} 天中 {1} 天 · 全部 {2} 天", UsageStatisticsService.RecentDays, recent, all);

    public static string FirstActiveDay(DateOnly day) => "第一次活跃：" + FullDay(day);

    public static string MilestoneText(Milestone milestone)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        var what = (milestone.Kind, milestone.Count) switch
        {
            (MilestoneKind.Question, 1) => "第一次提问",
            (MilestoneKind.Summary, 1) => "第一个总结",
            (MilestoneKind.Question, var n) => string.Format(CultureInfo.CurrentCulture, "第 {0:N0} 个提问", n),
            (_, var n) => string.Format(CultureInfo.CurrentCulture, "第 {0:N0} 个总结", n),
        };
        return what + "：" + FullDay(milestone.Day);
    }

    public static string ActivityItemName(string label, int value) => string.Format(CultureInfo.CurrentCulture, "{0} {1:N0}", label, value);
}
