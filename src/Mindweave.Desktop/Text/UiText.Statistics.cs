using System.Globalization;
using Mindweave.Application.Statistics;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// 设置 › 使用统计 (2026-11-12): token use by day or month, by model and by purpose, optional unit prices, and plain
// activity facts. Facts only: nothing here rates learning (requirements §5.4, §10).
public static partial class UiText
{
    public static string StatisticsTitle => T("Usage statistics", "使用统计");
    public static string StatisticsLink => T("Usage statistics", "使用统计");

    // Scope and period
    public static string ScopeAllWorkspaces => T("All workspaces", "全部工作区");
    public static string ScopeCurrentWorkspace => T("Current workspace", "当前工作区");
    public static string PeriodDaily => T("By day", "按日");
    public static string PeriodMonthly => T("By month", "按月");

    public static string ScopeCurrentWorkspaceName(string workspace) => T(
        string.Format(Culture, "Current workspace ({0})", workspace),
        string.Format(CultureInfo.CurrentCulture, "当前工作区（{0}）", workspace));

    public static string ScopeCaption(bool all, string workspace) => all
        ? T("The usage and activity below include all workspaces.", "下面的用量和活动包括所有工作区。")
        : T(
            string.Format(Culture, "The usage and activity below include only the workspace “{0}”.", workspace),
            string.Format(CultureInfo.CurrentCulture, "下面的用量和活动只包括工作区“{0}”。", workspace));

    // Loading
    public static string StatisticsLoading => T("Loading statistics…", "正在统计…");
    public static string StatisticsLoadFailed => T("Statistics can’t be read right now. Please try again later.", "统计暂时无法读取，请稍后再试。");
    public static string StatisticsRetry => T("Retry", "重试");

    // Token use
    public static string TokenUsageSection => T("Token usage", "token 用量");
    public static string InputTokensLabel => T("Input tokens", "输入 token");
    public static string OutputTokensLabel => T("Output tokens", "输出 token");
    public static string RequestsLabel => T("Requests", "请求");
    public static string ChartInput => T("Input", "输入");
    public static string ChartOutput => T("Output", "输出");
    public static string UsageChartName => T("Token usage chart", "token 用量图");
    public static string ShowAsTable => T("Show as table", "以表格查看");
    public static string TableDayHeader => T("Date", "日期");
    public static string TableMonthHeader => T("Month", "月份");
    public static string TableInputHeader => T("Input", "输入");
    public static string TableOutputHeader => T("Output", "输出");
    public static string TableRequestsHeader => T("Requests", "请求");

    /// <summary>What the totals count, and that billed failures are in them.</summary>
    public static string UsageBilledCaption => T(
        "Counted from the usage the service returns. Requests that failed or were stopped partway are included whenever the service returned usage (the service bills them too).",
        "按服务返回的用量统计。失败或中途停止的请求，只要服务返回了用量也计入（服务同样会计费）。");

    public static string PeriodRange(UsagePeriod period, DateOnly from, DateOnly to) => period == UsagePeriod.Daily
        ? T(
            string.Format(Culture, "Last {0} ({1} – {2})", UsageSpan(period, UsageStatisticsService.RecentDays), ShortDay(from), ShortDay(to)),
            string.Format(CultureInfo.CurrentCulture, "最近 {0} 天（{1}–{2}）", UsageStatisticsService.RecentDays, ShortDay(from), ShortDay(to)))
        : T(
            string.Format(Culture, "Last {0} ({1} – {2})", UsageSpan(period, UsageStatisticsService.RecentMonths), Month(from), Month(to)),
            string.Format(CultureInfo.CurrentCulture, "最近 {0} 个月（{1}–{2}）", UsageStatisticsService.RecentMonths, Month(from), Month(to)));

    public static string UnreportedRequests(int count) => T(
        string.Format(Culture, "{0} not counted: no usage was recorded (the service returned none, or the request never arrived)", Plural(count, "request", "requests")),
        string.Format(CultureInfo.CurrentCulture, "另有 {0} 次请求没有用量记录（服务未返回，或请求没有送达），未计入", count));

    public static string RequestCount(int count) => T(
        string.Format(Culture, "{0:N0} {1}", count, count == 1 ? "request" : "requests"),
        string.Format(CultureInfo.CurrentCulture, "{0:N0} 次", count));

    /// <summary>A bar's or a day's request count in the chart and calendar cards: "3 requests", "3 次请求".</summary>
    public static string ChartRequests(int count) => T(
        string.Format(Culture, "{0:N0} {1}", count, count == 1 ? "request" : "requests"),
        string.Format(CultureInfo.CurrentCulture, "{0:N0} 次请求", count));

    public static string Tokens(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"11月3日"</summary>
    public static string ShortDay(DateOnly day) => T(
        day.ToString("MMM d", Culture),
        day.ToString("M'月'd'日'", CultureInfo.InvariantCulture));

    /// <summary>"2026年11月3日"</summary>
    public static string FullDay(DateOnly day) => T(
        day.ToString("MMM d, yyyy", Culture),
        day.ToString("yyyy'年'M'月'd'日'", CultureInfo.InvariantCulture));

    /// <summary>"2026年11月"</summary>
    public static string Month(DateOnly month) => T(
        month.ToString("MMM yyyy", Culture),
        month.ToString("yyyy'年'M'月'", CultureInfo.InvariantCulture));

    /// <summary>A bar's own label under the chart: "10/14" for a day, "3月" for a month.</summary>
    public static string AxisLabel(UsagePeriod period, DateOnly start) => period == UsagePeriod.Daily
        ? start.ToString("M'/'d", CultureInfo.InvariantCulture)
        : T(start.ToString("MMM", Culture), start.ToString("M'月'", CultureInfo.InvariantCulture));

    /// <summary>A bucket's name in the table and the hover card: "11月3日" or "2026年11月".</summary>
    public static string BucketName(UsagePeriod period, DateOnly start) => period == UsagePeriod.Daily ? ShortDay(start) : Month(start);

    /// <summary>One bar in words: "11月3日：输入 12,345，输出 2,345，3 次请求".</summary>
    public static string BarDetail(UsagePeriod period, DateOnly start, TokenCounts counts)
    {
        var text = counts.Requests == 0 && counts.Unreported == 0
            ? T(
                string.Format(Culture, "{0}: {1}", BucketName(period, start), NoRequests),
                string.Format(CultureInfo.CurrentCulture, "{0}：{1}", BucketName(period, start), NoRequests))
            : T(
                string.Format(Culture, "{0}: input {1}, output {2}, {3}", BucketName(period, start), Tokens(counts.Input), Tokens(counts.Output), Plural(counts.Requests, "request", "requests")),
                string.Format(CultureInfo.CurrentCulture, "{0}：输入 {1}，输出 {2}，{3} 次请求", BucketName(period, start), Tokens(counts.Input), Tokens(counts.Output), counts.Requests));
        return counts.Unreported > 0
            ? text + T(
                string.Format(Culture, ", plus {0} with no usage returned", Plural(counts.Unreported, "request", "requests")),
                string.Format(CultureInfo.CurrentCulture, "，另有 {0} 次未返回用量", counts.Unreported))
            : text;
    }

    /// <summary>The chart as a screen reader hears it, with the way to every number.</summary>
    public static string ChartSummary(UsagePeriod period, IReadOnlyList<UsageBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var unit = T(period == UsagePeriod.Daily ? "days" : "months", period == UsagePeriod.Daily ? "天" : "个月");
        var busiest = buckets.MaxBy(b => b.Counts.Total);
        var empty = buckets.Count(b => b.Counts.Total == 0);
        if (busiest is null || busiest.Counts.Total == 0)
        {
            return T(
                string.Format(Culture, "No usage in the last {0}.", UsageSpan(period, buckets.Count)),
                string.Format(CultureInfo.CurrentCulture, "最近 {0} {1}都没有用量。", buckets.Count, unit));
        }

        return T(
            string.Format(
                Culture,
                "Token usage over the last {0}; in each bar, the lower part is input and the upper part is output. The highest was {1} ({2} tokens); {3} had no usage. Use the left and right arrow keys to go through the bars one by one, or open “Show as table”.",
                UsageSpan(period, buckets.Count),
                BucketName(period, busiest.Start),
                Tokens(busiest.Counts.Total),
                UsageSpan(period, empty)),
            string.Format(
                CultureInfo.CurrentCulture,
                "最近 {0} {1}的 token 用量，每根柱子下段为输入，上段为输出。用量最多的是 {2}（{3}）；{4} {1}没有用量。用左右方向键逐个查看，或打开“以表格查看”。",
                buckets.Count,
                unit,
                BucketName(period, busiest.Start),
                Tokens(busiest.Counts.Total),
                empty));
    }

    /// <summary>English only: a count of days or months with its noun, "1 day", "12 months".</summary>
    private static string UsageSpan(UsagePeriod period, int count) => period == UsagePeriod.Daily
        ? Plural(count, "day", "days")
        : Plural(count, "month", "months");

    public static string ChartMaxLabel(long max) => T("Max " + Tokens(max), "最高 " + Tokens(max));

    // Usage calendar (2026-11-17): the last year of daily token use, one square a day.
    public static string CalendarSection => T("Daily token usage", "每日 token 用量");
    public static string CalendarName => T("Daily token usage calendar", "每日 token 用量日历");
    public static string CalendarLess => T("Less", "少");
    public static string CalendarMore => T("More", "多");

    /// <summary>The weekday labels beside the rows, Monday on top; only every other row is labelled, as on a wall calendar's margin.</summary>
    public static IReadOnlyList<string> CalendarWeekdayLabels => [T("Mon", "一"), "", T("Wed", "三"), "", T("Fri", "五"), "", ""];

    /// <summary>"11月", over the column a month starts in.</summary>
    public static string CalendarMonthLabel(DateOnly month) => T(
        month.ToString("MMM", Culture),
        month.ToString("M'月'", CultureInfo.InvariantCulture));

    /// <summary>"最近一年（2025年11月17日–2026年11月17日），每格一天，颜色越深用量越多"</summary>
    public static string CalendarRange(DateOnly from, DateOnly to) => T(
        string.Format(Culture, "The past year ({0} – {1}), one square a day; darker squares mean more usage", FullDay(from), FullDay(to)),
        string.Format(CultureInfo.CurrentCulture, "最近一年（{0}–{1}），每格一天，颜色越深用量越多", FullDay(from), FullDay(to)));

    /// <summary>One square in words: "2026年11月3日：输入 12,345，输出 2,345，3 次请求".</summary>
    public static string CalendarDayDetail(DateOnly day, TokenCounts counts)
    {
        var text = counts.Requests == 0 && counts.Unreported == 0
            ? T(
                string.Format(Culture, "{0}: {1}", FullDay(day), NoRequests),
                string.Format(CultureInfo.CurrentCulture, "{0}：{1}", FullDay(day), NoRequests))
            : T(
                string.Format(Culture, "{0}: input {1}, output {2}, {3}", FullDay(day), Tokens(counts.Input), Tokens(counts.Output), Plural(counts.Requests, "request", "requests")),
                string.Format(CultureInfo.CurrentCulture, "{0}：输入 {1}，输出 {2}，{3} 次请求", FullDay(day), Tokens(counts.Input), Tokens(counts.Output), counts.Requests));
        return counts.Unreported > 0
            ? text + T(
                string.Format(Culture, ", plus {0} with no usage returned", Plural(counts.Unreported, "request", "requests")),
                string.Format(CultureInfo.CurrentCulture, "，另有 {0} 次未返回用量", counts.Unreported))
            : text;
    }

    /// <summary>The calendar as a screen reader hears it, with the way to every day.</summary>
    public static string CalendarSummary(IReadOnlyList<UsageBucket> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var busiest = days.MaxBy(b => b.Counts.Total);
        if (busiest is null || busiest.Counts.Total == 0)
        {
            return T(
                string.Format(Culture, "No usage in the last {0}.", Plural(days.Count, "day", "days")),
                string.Format(CultureInfo.CurrentCulture, "最近 {0} 天都没有用量。", days.Count));
        }

        return T(
            string.Format(
                Culture,
                "Daily token usage over the last {0}. Each column is a week, Monday to Sunday; darker squares mean more usage. {1} had usage; the highest was {2} ({3} tokens). Use the arrow keys to go day by day: left and right change the week, up and down change the day.",
                Plural(days.Count, "day", "days"),
                Plural(days.Count(b => b.Counts.Total > 0), "day", "days"),
                FullDay(busiest.Start),
                Tokens(busiest.Counts.Total)),
            string.Format(
                CultureInfo.CurrentCulture,
                "最近 {0} 天每天的 token 用量，每列一周，从周一到周日，颜色越深用量越多。{1} 天有用量，最多的是 {2}（{3}）。用方向键逐日查看：左右换一周，上下换一天。",
                days.Count,
                days.Count(b => b.Counts.Total > 0),
                FullDay(busiest.Start),
                Tokens(busiest.Counts.Total)));
    }

    /// <summary>A day, month or purpose without any request.</summary>
    public static string NoRequests => T("No requests", "没有请求");

    /// <summary>The hover card's line for requests whose service returned no usage.</summary>
    public static string BarUnreported(int count) => T(
        string.Format(Culture, "Plus {0} with no usage returned", Plural(count, "request", "requests")),
        string.Format(CultureInfo.CurrentCulture, "另有 {0} 次未返回用量", count));

    /// <summary>A purpose row as a screen reader hears it: "回答：输入 … · 3 次请求，占 45%".</summary>
    public static string PurposeRowName(string name, string usage, string share) => T(
        string.Format(Culture, "{0}: {1}, {2} of the total", name, usage, share),
        string.Format(CultureInfo.CurrentCulture, "{0}：{1}，占 {2}", name, usage, share));

    public static string NoRequestsYet => T(
        "No requests have been sent to a model yet. When you ask questions, or titles and summaries are generated, the usage the service returns appears here.",
        "还没有向模型发出过请求。提问、生成标题和总结后，服务返回的用量会出现在这里。");

    public static string NoRequestsInPeriod(UsagePeriod period) => period == UsagePeriod.Daily
        ? T(
            string.Format(Culture, "No requests in the last {0}.", UsageSpan(period, UsageStatisticsService.RecentDays)),
            string.Format(CultureInfo.CurrentCulture, "最近 {0} 天没有请求。", UsageStatisticsService.RecentDays))
        : T(
            string.Format(Culture, "No requests in the last {0}.", UsageSpan(period, UsageStatisticsService.RecentMonths)),
            string.Format(CultureInfo.CurrentCulture, "最近 {0} 个月没有请求。", UsageStatisticsService.RecentMonths));

    public static string NoModelConfigured => T(
        "No model is set up yet. After you set one up, the token usage the service returns for each request is recorded here.",
        "还没有配置模型。配置后，每次请求服务返回的 token 用量会记在这里。");
    public static string GoToModelSettings => T("Go to Model connection", "前往模型设置");

    // By model, and prices
    public static string ByModelSection => T("By model", "按模型");
    public static string ByPurposeSection => T("By purpose", "按用途");
    public static string CurrencyLabel => T("Currency symbol", "货币符号");
    public static string InputPriceLabel => T("Per million input tokens", "每百万输入 token");
    public static string OutputPriceLabel => T("Per million output tokens", "每百万输出 token");
    public static string SetPrice => T("Set prices…", "单价…");
    public static string SavePrice => T("Save", "保存");
    public static string CancelPrice => T("Cancel", "取消");
    public static string ClearPrice => T("Clear prices", "清除单价");
    public static string PriceSaveFailed => T("The prices couldn’t be saved. Please try again later.", "单价没能保存，请稍后再试。");
    public static string CurrencySaveFailed => T(
        "The currency symbol couldn’t be saved. The new symbol is still shown for now.",
        "货币符号没能保存，本次仍按新符号显示。");

    /// <summary>The caption under every cost (the user's own prices; nothing built in or fetched).</summary>
    public static string CostCaption => T(
        "Estimated from the prices you entered. The provider’s other billing rules (such as caching or discounts) are not included.",
        "按你填写的单价估算，不含服务商的其他计费规则（如缓存、折扣）。");

    public static string PriceHint => T(
        "Enter prices per million tokens. They are used only for the estimates on this page and are never sent anywhere. Leave both empty for no estimate.",
        "单价按每百万 token 填写，只用于本页估算，不会发送到任何地方。两项都留空即不估算。");

    public static string ModelRowLabel(ModelKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Model + " · " + key.Host;
    }

    /// <summary>"输入 1,234 · 输出 567 · 12 次请求", "· 1 次未返回用量" after it; only that when no request reported usage.</summary>
    public static string UsageLine(TokenCounts counts) => counts.Requests == 0
        ? T(
            string.Format(Culture, "{0} returned no usage", Plural(counts.Unreported, "request", "requests")),
            string.Format(CultureInfo.CurrentCulture, "{0} 次请求未返回用量", counts.Unreported))
        : T(
            string.Format(Culture, "Input {0} · Output {1} · {2}", Tokens(counts.Input), Tokens(counts.Output), Plural(counts.Requests, "request", "requests")),
            string.Format(CultureInfo.CurrentCulture, "输入 {0} · 输出 {1} · {2} 次请求", Tokens(counts.Input), Tokens(counts.Output), counts.Requests))
          + (counts.Unreported > 0
              ? T(
                  string.Format(Culture, " · {0} with no usage returned", Plural(counts.Unreported, "request", "requests")),
                  string.Format(CultureInfo.CurrentCulture, " · {0} 次未返回用量", counts.Unreported))
              : string.Empty);

    /// <summary>A table cell: "12 次", or "12 次（1 次未返回用量）".</summary>
    public static string TableRequests(int requests, int unreported) => unreported > 0
        ? T(
            string.Format(Culture, "{0} ({1:N0} with no usage returned)", RequestCount(requests), unreported),
            string.Format(CultureInfo.CurrentCulture, "{0:N0} 次（{1} 次未返回用量）", requests, unreported))
        : RequestCount(requests);

    /// <summary>
    /// A model's cached input (requirements §10.11.4): "缓存：输入的 62% 读自缓存 · 写入 1,234", with how many requests reported it
    /// when not all did. "缓存：未返回" when none did: not reported is never shown as zero.
    /// </summary>
    public static string CacheLine(TokenCounts counts)
    {
        var cache = counts.Cache;
        var reporting = counts.Requests + counts.Unreported;
        if (cache.ReadShare is not { } share)
        {
            return cache.WriteReported == 0
                ? T("Cache: not reported", "缓存：未返回")
                : T(string.Format(Culture, "Cache: reads not reported · {0} written", Tokens(cache.Written)),
                    string.Format(CultureInfo.CurrentCulture, "缓存：读取未返回 · 写入 {0}", Tokens(cache.Written)));
        }

        var percent = share <= 0 ? "0%"
            : share < 0.005 ? "<1%"
            : string.Format(CultureInfo.InvariantCulture, "{0:0}%", Math.Round(share * 100, MidpointRounding.AwayFromZero));
        var line = T(string.Format(Culture, "Cache: {0} of input read from it", percent), string.Format(CultureInfo.CurrentCulture, "缓存：输入的 {0} 读自缓存", percent));
        if (cache.WriteReported > 0)
        {
            line += T(string.Format(Culture, " · {0} written", Tokens(cache.Written)), string.Format(CultureInfo.CurrentCulture, " · 写入 {0}", Tokens(cache.Written)));
        }

        if (cache.ReadReported < reporting)
        {
            line += T(
                string.Format(Culture, " · reported by {0} of {1}", cache.ReadReported, Plural(reporting, "request", "requests")),
                string.Format(CultureInfo.CurrentCulture, " · {0} 次请求中 {1} 次返回", reporting, cache.ReadReported));
        }

        return line;
    }

    /// <summary>A share of the period's tokens: "45%", "<1%" for a little, "—" for none.</summary>
    public static string Share(long part, long whole) =>
        whole <= 0 || part <= 0 ? "—"
        : part * 100 < whole ? "<1%"
        : string.Format(CultureInfo.InvariantCulture, "{0:0}%", Math.Round(part * 100.0 / whole, MidpointRounding.AwayFromZero));

    public static string Money(string currency, decimal amount) =>
        currency + Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("N2", CultureInfo.InvariantCulture);

    public static string CostOf(string currency, decimal amount) => T("About " + Money(currency, amount), "约 " + Money(currency, amount));

    public static string PriceSummary(string currency, ModelPrice? price) => price is null
        ? T("No prices set", "未填单价")
        : T(
            string.Format(Culture, "Prices: input {0}{1} · output {0}{2} (per million tokens)", currency, UsagePricing.Format(price.InputPerMillion), UsagePricing.Format(price.OutputPerMillion)),
            string.Format(CultureInfo.CurrentCulture, "单价：输入 {0}{1} · 输出 {0}{2}（每百万 token）", currency, UsagePricing.Format(price.InputPerMillion), UsagePricing.Format(price.OutputPerMillion)));

    public static string TotalCost(string currency, decimal amount, int unpriced) =>
        T(
            string.Format(Culture, "Estimated cost at your prices: about {0}", Money(currency, amount)),
            string.Format(CultureInfo.CurrentCulture, "按单价估算的费用：约 {0}", Money(currency, amount)))
        + (unpriced > 0
            ? T(
                string.Format(Culture, " (not including {0} without prices)", Plural(unpriced, "model", "models")),
                string.Format(CultureInfo.CurrentCulture, "（另有 {0} 个模型未填单价，未计入）", unpriced))
            : string.Empty);

    public static string SetPriceName(ModelKey key) => T(
        string.Format(Culture, "Set prices for {0}", ModelRowLabel(key)),
        string.Format(CultureInfo.CurrentCulture, "设置 {0} 的单价", ModelRowLabel(key)));

    public static string PriceFieldName(ModelKey key, bool input) => T(
        string.Format(Culture, "Price per million {1} tokens for {0}", ModelRowLabel(key), input ? "input" : "output"),
        string.Format(CultureInfo.CurrentCulture, "{0} 的{1}单价", ModelRowLabel(key), input ? "每百万输入 token " : "每百万输出 token "));

    public static string PriceProblemText(PriceProblem problem) => problem switch
    {
        PriceProblem.NotANumber => T("Enter a number, such as 2 or 0.5.", "请输入数字，例如 2 或 0.5。"),
        PriceProblem.Negative => T("A price can’t be negative.", "单价不能为负数。"),
        PriceProblem.TooLarge => T("The price is too large. Check for extra digits.", "单价过大，请检查是否多输了位数。"),
        PriceProblem.Incomplete => T("Fill in both prices (enter 0 for one that is free).", "请同时填写两项单价（不收费的一项可填 0）。"),
        PriceProblem.CurrencyTooLong => T("The currency symbol can be at most 3 characters.", "货币符号最多 3 个字符。"),
        _ => T("Not recognized.", "无法识别。"),
    };

    public static string PurposeName(UsagePurpose purpose) => purpose switch
    {
        UsagePurpose.Answer => T("Answers", "回答"),
        UsagePurpose.Title => T("Titles", "标题"),
        UsagePurpose.Summary => T("Summaries", "总结"),
        UsagePurpose.Naming => T("Naming", "命名"),
        UsagePurpose.WorkspaceName => T("Workspace names", "工作区名称"),
        UsagePurpose.Recommendation => T("Recommendations", "推荐"),
        UsagePurpose.LinkSuggestion => T("Link suggestions", "关联建议"),
        UsagePurpose.AttachmentSummary => T("Attachment digests", "附件摘要"),
        _ => T("Other", "其他"),
    };

    public static string PurposeHint(UsagePurpose purpose) => purpose switch
    {
        UsagePurpose.Answer => T("Answering questions, including compressing earlier context in longer conversations", "回答问题，含较长会话中压缩较早的上下文"),
        UsagePurpose.Title => T("Generating conversation titles automatically", "自动生成会话标题"),
        UsagePurpose.Summary => T("Generating summaries", "生成总结"),
        UsagePurpose.Naming => T("Giving distinct names to summaries that share a name", "为同名的总结取区分名称"),
        UsagePurpose.WorkspaceName => T("Suggesting workspace names, including background preparation", "建议工作区名称，含后台准备"),
        UsagePurpose.Recommendation => T("Recommended questions and recommended nodes, including background preparation", "推荐问题与推荐节点，含后台准备"),
        UsagePurpose.LinkSuggestion => T("AI link suggestions, requested only when you click “Suggest links”", "AI 建议关联，只在点“建议关联”时请求"),
        UsagePurpose.AttachmentSummary => T("Digests of longer attachments, generated once per attachment", "较长附件的摘要，每个附件只生成一次"),
        _ => T("Other purposes recorded by a newer version", "新版本记录的其他用途"),
    };

    // Activity
    public static string ActivitySection => T("Activity", "活动");
    public static string MilestonesSection => T("Milestones", "里程碑");
    public static string QuestionsLabel => T("Questions", "提问");
    public static string ConversationsLabel => T("Conversations", "会话");
    public static string BranchesLabel => T("Branches", "分支");
    public static string SummariesLabel => T("Summaries", "总结");
    public static string SummaryVersionsLabel => T("Summary versions", "总结版本");
    public static string ReadSummariesLabel => T("Summaries read", "已读的总结");
    public static string ArchivedConversationsLabel => T("Archived conversations", "归档的会话");
    public static string ActiveDaysLabel => T("Active days", "活跃天数");
    public static string NoQuestionsInWorkspace => T("No questions in this workspace yet.", "这个工作区还没有提问。");
    public static string NoQuestionsAtAll => T("No questions yet.", "还没有提问。");

    /// <summary>What the counts are and are not: top-level conversations, branches apart, deleted content not counted.</summary>
    public static string ActivityCaption => T(
        "Only top-level conversations count as conversations; branches are counted separately. A day with at least one question counts as an active day, by this computer’s time zone. Deleted content is not counted.",
        "会话只算顶层会话，分支另计；有一次提问的那一天算一个活跃天数，按本机时区计。已删除的内容不计入。");

    /// <summary>Why deleting lowers the usage (2026-11-12): request records go with what they were made for.</summary>
    public static string DeletedUsageCaption => T(
        "Usage from deleted conversations and workspaces is no longer counted: their request records are deleted with them.",
        "已删除的会话和工作区的用量不再计入：它们的请求记录随之删除。");

    public static string ActiveDaysText(int recent, int all) => T(
        string.Format(Culture, "{0} of the last {1} · {2} in all", recent, Plural(UsageStatisticsService.RecentDays, "day", "days"), Plural(all, "day", "days")),
        string.Format(CultureInfo.CurrentCulture, "最近 {0} 天中 {1} 天 · 全部 {2} 天", UsageStatisticsService.RecentDays, recent, all));

    public static string FirstActiveDay(DateOnly day) => T("First active day: " + FullDay(day), "第一次活跃：" + FullDay(day));

    public static string MilestoneText(Milestone milestone)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        var what = (milestone.Kind, milestone.Count) switch
        {
            (MilestoneKind.Question, 1) => T("First question", "第一次提问"),
            (MilestoneKind.Summary, 1) => T("First summary", "第一个总结"),
            (MilestoneKind.Question, var n) => T(string.Format(Culture, "Question {0:N0}", n), string.Format(CultureInfo.CurrentCulture, "第 {0:N0} 个提问", n)),
            (_, var n) => T(string.Format(Culture, "Summary {0:N0}", n), string.Format(CultureInfo.CurrentCulture, "第 {0:N0} 个总结", n)),
        };
        return T(what + ": " + FullDay(milestone.Day), what + "：" + FullDay(milestone.Day));
    }

    public static string ActivityItemName(string label, int value) => string.Format(CultureInfo.CurrentCulture, "{0} {1:N0}", label, value);
}
