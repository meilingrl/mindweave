using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Mindweave.Infrastructure.Tests.RunEval;

/// <summary>Prices per million tokens, input and output, in the provider's currency (<c>MINDWEAVE_RUN_EVAL_PRICES=in,out</c>).</summary>
internal sealed record EvalPrices(double Input, double Output)
{
    /// <summary>"1.5,6" → 1.5 and 6; null for nothing, or for anything else.</summary>
    public static EvalPrices? Parse(string? text)
    {
        var parts = (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var input)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var output)
            && input >= 0 && output >= 0
            ? new EvalPrices(input, output)
            : null;
    }

    public double Of(int input, int output) => ((input * Input) + (output * Output)) / 1_000_000;
}

/// <summary>One edition's results, in scenario order.</summary>
internal sealed record EvalRun(EvalEdition Edition, IReadOnlyList<EvalResult> Results);

/// <summary>What the judge itself cost, apart from the answers under test.</summary>
internal sealed record EvalJudgeCost(int Requests, int PromptTokens, int CompletionTokens);

/// <summary>
/// The side-by-side report of two prompt editions (docs/design/reply-quality.md Q10, acceptance 13): per scenario and in
/// total, each edition's checks, input and output tokens, model requests, tool calls, seconds to the first text and in all,
/// with the difference; the answers one after the other; and the judge's hints when it ran. The owner's reading decides.
/// </summary>
internal static class EvalComparison
{
    private const int AnswerExcerpt = 2_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <param name="label">What ran: "scripted" or the model and its host.</param>
    public static string Markdown(
        EvalRun first, EvalRun second, string label, DateTimeOffset at, EvalPrices? prices = null,
        IReadOnlyDictionary<string, EvalJudgement>? judged = null, EvalJudgeCost? judge = null)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var s = new StringBuilder();
        var pairs = Pairs(first, second);
        s.Append(CultureInfo.InvariantCulture, $"""
            # 提示词对照报告

            - 时间：{at:yyyy-MM-dd HH:mm} UTC
            - 模型：{label}
            - 对照：{first.Edition.Label} → {second.Edition.Label}
            - 场景：{pairs.Count} 个
            - 价格：{(prices is null ? "未给出，只列 token" : $"每百万 token 输入 {prices.Input}、输出 {prices.Output}")}
            - 依据：docs/design/reply-quality.md Q10；检查只报告，不判定好坏，由你阅读决定
            - 时间是一次的样本，随服务负载变动；输出 token 和请求数是稳定的信号，时间差只在很大或反复出现时才算数


            """);

        s.Append(CultureInfo.InvariantCulture, $"## 汇总\n\n| 指标 | {first.Edition.Name} | {second.Edition.Name} | 差 |\n| --- | --- | --- | --- |\n");
        Row(s, "检查通过", Passed(first.Results), Passed(second.Results), null);
        Compare(s, "输入 token（被测）", Sum(first, m => m.InputTokens), Sum(second, m => m.InputTokens));
        Compare(s, "输出 token（被测）", Sum(first, m => m.CompletionTokens ?? 0), Sum(second, m => m.CompletionTokens ?? 0));
        Compare(s, "模型请求（被测）", Sum(first, m => m.ModelRequests), Sum(second, m => m.ModelRequests));
        Compare(s, "工具调用", Sum(first, m => m.ToolCalls), Sum(second, m => m.ToolCalls));
        Compare(s, "到首字秒数（合计）", Seconds(first, m => m.FirstTextSeconds ?? 0), Seconds(second, m => m.FirstTextSeconds ?? 0), "0.0");
        Compare(s, "总秒数（合计）", Seconds(first, m => m.Seconds), Seconds(second, m => m.Seconds), "0.0");
        Compare(s, "全部模型请求（含标题、摘要与前面的提问）", Sum(first, m => m.ScenarioRequests), Sum(second, m => m.ScenarioRequests));
        Compare(s, "全部输入 token（服务报告）", Sum(first, m => m.ScenarioPromptTokens ?? 0), Sum(second, m => m.ScenarioPromptTokens ?? 0));
        Compare(s, "全部输出 token（服务报告）", Sum(first, m => m.ScenarioCompletionTokens ?? 0), Sum(second, m => m.ScenarioCompletionTokens ?? 0));
        Row(s, "其中读自缓存的输入 token（服务报告）", EvalText.CacheRead(Metrics(first)), EvalText.CacheRead(Metrics(second)), null);
        if (prices is not null)
        {
            Compare(s, "费用（全部）", Cost(first, prices), Cost(second, prices), "0.0000");
        }

        if (judged is { Count: > 0 })
        {
            var clarity = judged.Values.Select(j => j.Clarity).OfType<EvalClarity>().ToList();
            Row(s, "评判（提示）：哪一版更清楚", $"{first.Edition.Name} {clarity.Count(c => c == EvalClarity.First)}", $"{second.Edition.Name} {clarity.Count(c => c == EvalClarity.Second)}",
                $"差不多 {clarity.Count(c => c == EvalClarity.Same)}，两次不一致 {clarity.Count(c => c == EvalClarity.Inconsistent)}，读不出 {clarity.Count(c => c == EvalClarity.Unreadable)}");
            Row(s, "评判（提示）：有依据的引用句", Support(judged.Values.Select(j => j.Previous)), Support(judged.Values.Select(j => j.Current)), null);
        }

        if (judge is not null)
        {
            Row(s, "评判自身（不计入上面）", $"{judge.Requests} 次请求", $"输入 {judge.PromptTokens:N0} / 输出 {judge.CompletionTokens:N0} token",
                prices is null ? null : $"费用 {prices.Of(judge.PromptTokens, judge.CompletionTokens):0.0000}");
        }

        s.Append(CultureInfo.InvariantCulture, $"\n## 场景\n\n| 场景 | 类别 | 检查 | 输入 token | 输出 token | 请求 | 工具 | 首字秒 | 总秒 |{(judged is { Count: > 0 } ? " 评判 |" : string.Empty)}\n| --- | --- | --- | --- | --- | --- | --- | --- | --- |{(judged is { Count: > 0 } ? " --- |" : string.Empty)}\n");
        foreach (var (scenario, a, b) in pairs)
        {
            string Pair(Func<EvalMetrics, double?> pick, string format = "0") =>
                $"{Num(a.Metrics is { } x ? pick(x) : null, format)} → {Num(b.Metrics is { } y ? pick(y) : null, format)}";
            s.Append(CultureInfo.InvariantCulture, $"| {scenario.Id} | {scenario.Category} | {a.Passed}/{a.Checks.Count} → {b.Passed}/{b.Checks.Count} | {Pair(m => m.InputTokens)} | {Pair(m => m.CompletionTokens)} | {Pair(m => m.ModelRequests)} | {Pair(m => m.ToolCalls)} | {Pair(m => m.FirstTextSeconds, "0.0")} | {Pair(m => m.Seconds, "0.0")} |");
            if (judged is { Count: > 0 })
            {
                s.Append(' ').Append(judged.TryGetValue(scenario.Id, out var j) && j.Clarity is { } c ? Clarity(c, first, second) : "—").Append(" |");
            }

            s.Append('\n');
        }

        foreach (var (scenario, a, b) in pairs)
        {
            s.Append(CultureInfo.InvariantCulture, $"\n## {scenario.Id}：{scenario.Purpose}\n\n");
            foreach (var (turn, i) in scenario.Turns.Select((t, i) => (t, i + 1)))
            {
                s.Append(CultureInfo.InvariantCulture, $"- 提问 {i}（{turn.Mode}{(turn.WithAttachments ? "，带附件" : string.Empty)}{(turn.BranchOn is null ? string.Empty : "，开分支")}）：{turn.Question}\n");
            }

            if (scenario.Summarize)
            {
                s.Append("- 之后总结这段对话，测的是总结\n");
            }

            s.Append(CultureInfo.InvariantCulture, $"\n| | {first.Edition.Name} | {second.Edition.Name} | 差 |\n| --- | --- | --- | --- |\n");
            MetricRow(s, "输入 token", a, b, m => m.InputTokens);
            MetricRow(s, "输出 token", a, b, m => m.CompletionTokens);
            MetricRow(s, "模型请求", a, b, m => m.ModelRequests);
            MetricRow(s, "工具调用", a, b, m => m.ToolCalls);
            MetricRow(s, "到首字秒数", a, b, m => m.FirstTextSeconds, "0.0");
            MetricRow(s, "总秒数", a, b, m => m.Seconds, "0.0");
            if (prices is not null)
            {
                MetricRow(s, "费用（被测）", a, b, m => prices.Of(m.InputTokens, m.CompletionTokens ?? 0), "0.0000");
            }

            s.Append(CultureInfo.InvariantCulture, $"\n| 检查 | {first.Edition.Name} | {second.Edition.Name} |\n| --- | --- | --- |\n");
            var names = a.Checks.Select(c => c.Name).Concat(b.Checks.Select(c => c.Name)).Distinct(StringComparer.Ordinal);
            foreach (var name in names)
            {
                s.Append(CultureInfo.InvariantCulture, $"| {Escape(name)} | {Mark(a, name)} | {Mark(b, name)} |\n");
            }

            if (judged is not null && judged.TryGetValue(scenario.Id, out var judgement))
            {
                s.Append("\n评判（只是提示）：");
                s.Append(judgement.Clarity is { } c ? Clarity(c, first, second) : "没有比较");
                s.Append(CultureInfo.InvariantCulture, $"；引用有依据：{first.Edition.Name} {Support([judgement.Previous])}，{second.Edition.Name} {Support([judgement.Current])}\n");
            }

            Answer(s, first.Edition, a);
            Answer(s, second.Edition, b);
        }

        return s.ToString();
    }

    /// <summary>The numbers of both editions per scenario, for comparing runs later.</summary>
    public static string JsonOf(EvalRun first, EvalRun second, string label, DateTimeOffset at, IReadOnlyDictionary<string, EvalJudgement>? judged = null, EvalJudgeCost? judge = null) =>
        JsonSerializer.Serialize(
            new
            {
                at,
                model = label,
                editions = new[] { first, second }.Select(r => new { name = r.Edition.Name, answer = r.Edition.Answer, run = r.Edition.Run, summary = r.Edition.Summary }),
                judge,
                scenarios = Pairs(first, second).Select(p => new
                {
                    id = p.Scenario.Id,
                    category = p.Scenario.Category,
                    judgement = judged is not null && judged.TryGetValue(p.Scenario.Id, out var j) ? j : null,
                    results = new[] { p.First, p.Second }.Select(r => new
                    {
                        failure = r.Failure,
                        passed = r.Passed,
                        checks = r.Checks.Select(c => new { name = c.Name, passed = c.Passed, detail = c.Detail }),
                        metrics = r.Metrics,
                        quote = r.Observation?.Quote,
                        text = r.Observation?.Text,
                    }),
                }),
            },
            Json);

    /// <summary>Writes the Markdown and the JSON next to each other; returns the Markdown file's path.</summary>
    public static async Task<string> WriteAsync(
        EvalRun first, EvalRun second, string label, string path, EvalPrices? prices, IReadOnlyDictionary<string, EvalJudgement>? judged, EvalJudgeCost? judge, CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, Markdown(first, second, label, at, prices, judged, judge), ct);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".json"), JsonOf(first, second, label, at, judged, judge), ct);
        return path;
    }

    /// <summary>The scenarios both editions have, in the first one's order.</summary>
    private static List<(EvalScenario Scenario, EvalResult First, EvalResult Second)> Pairs(EvalRun first, EvalRun second) =>
        [.. first.Results.Join(second.Results, r => r.Scenario.Id, r => r.Scenario.Id, (a, b) => (a.Scenario, a, b), StringComparer.Ordinal)];

    private static string Passed(IReadOnlyList<EvalResult> results) => $"{results.Sum(r => r.Passed)} / {results.Sum(r => r.Checks.Count)}";

    private static List<EvalMetrics> Metrics(EvalRun run) => [.. run.Results.Select(r => r.Metrics).OfType<EvalMetrics>()];

    private static double Sum(EvalRun run, Func<EvalMetrics, int> pick) => run.Results.Select(r => r.Metrics).OfType<EvalMetrics>().Sum(pick);

    private static double Seconds(EvalRun run, Func<EvalMetrics, double> pick) => run.Results.Select(r => r.Metrics).OfType<EvalMetrics>().Sum(pick);

    private static double Cost(EvalRun run, EvalPrices prices) =>
        run.Results.Select(r => r.Metrics).OfType<EvalMetrics>().Sum(m => prices.Of(m.ScenarioPromptTokens ?? m.InputTokens, m.ScenarioCompletionTokens ?? m.CompletionTokens ?? 0));

    private static void Compare(StringBuilder s, string name, double a, double b, string format = "N0") =>
        Row(s, name, a.ToString(format, CultureInfo.InvariantCulture), b.ToString(format, CultureInfo.InvariantCulture), Difference(a, b, format));

    private static void MetricRow(StringBuilder s, string name, EvalResult a, EvalResult b, Func<EvalMetrics, double?> pick, string format = "0")
    {
        var x = a.Metrics is { } ma ? pick(ma) : null;
        var y = b.Metrics is { } mb ? pick(mb) : null;
        Row(s, name, Num(x, format), Num(y, format), x is { } u && y is { } v ? Difference(u, v, format) : "—");
    }

    private static string Difference(double a, double b, string format)
    {
        var d = b - a;
        var sign = d > 0 ? "+" : string.Empty;
        var share = a > 0 ? string.Create(CultureInfo.InvariantCulture, $"（{sign}{d / a:P0}）") : string.Empty;
        return sign + d.ToString(format, CultureInfo.InvariantCulture) + share;
    }

    private static string Num(double? value, string format) => value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "—";

    private static void Row(StringBuilder s, string name, string a, string b, string? difference) =>
        s.Append(CultureInfo.InvariantCulture, $"| {name} | {a} | {b} | {difference ?? string.Empty} |\n");

    private static string Mark(EvalResult result, string name) =>
        result.Failure is not null ? "未运行"
        : result.Checks.FirstOrDefault(c => c.Name == name) is { } c ? (c.Passed ? "✓" : "✗") + (c.Detail.Length > 0 ? " " + Escape(c.Detail) : string.Empty)
        : "—";

    private static string Clarity(EvalClarity clarity, EvalRun first, EvalRun second) => clarity switch
    {
        EvalClarity.First => first.Edition.Name + "更清楚",
        EvalClarity.Second => second.Edition.Name + "更清楚",
        EvalClarity.Same => "差不多",
        EvalClarity.Inconsistent => "两次不一致",
        _ => "读不出评判",
    };

    private static string Support(IEnumerable<EvalSupport?> supports)
    {
        var list = supports.OfType<EvalSupport>().ToList();
        var total = list.Sum(x => x.Total);
        return total == 0 ? "没有引用句" : $"{list.Sum(x => x.Yes)} / {total}（部分 {list.Sum(x => x.Partly)}，无 {list.Sum(x => x.No)}）";
    }

    private static void Answer(StringBuilder s, EvalEdition edition, EvalResult result)
    {
        s.Append(CultureInfo.InvariantCulture, $"\n### {edition.Name}{(result.Scenario.Summarize ? "的总结" : "的回答")}\n\n");
        if (result.Failure is { } failure)
        {
            s.Append(CultureInfo.InvariantCulture, $"**未能运行：** {failure}\n");
            return;
        }

        var o = result.Observation!;
        if (o.Quote is { } quote)
        {
            s.Append(CultureInfo.InvariantCulture, $"所选的段落：{Escape(quote)}\n\n");
        }

        if (o.Calls.Count > 0)
        {
            s.Append(CultureInfo.InvariantCulture, $"工具调用：{Escape(string.Join("；", o.Calls.Select(c => $"{c.Tool} {c.Input ?? c.Arguments}")))}\n\n");
        }

        var text = o.Summary is { } summary ? $"标题：{summary.Title}\n短名：{summary.ShortTitle}\n\n{summary.Body}" : o.Text;
        text = text.Length > AnswerExcerpt ? text[..AnswerExcerpt] + "…" : text;
        s.Append(text.Length == 0 ? "> （空）\n" : string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => "> " + l)) + "\n");
    }

    private static string Escape(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);
}
