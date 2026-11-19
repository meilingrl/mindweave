using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Mindweave.Infrastructure.Tests.RunEval;

/// <summary>
/// The evaluation's report (docs/design/agent-loop.md §4.3): a Markdown page for reading (totals, one row per scenario, then
/// each scenario's checks, tool calls and answer) and a JSON file of the same numbers for comparing runs.
/// </summary>
internal static class EvalReport
{
    private const int AnswerExcerpt = 1_200;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <param name="label">What ran: "scripted" or the model and its host.</param>
    public static string Markdown(IReadOnlyList<EvalResult> results, string label, DateTimeOffset at)
    {
        var s = new StringBuilder();
        var ran = results.Where(r => r.Metrics is not null).ToList();
        var metrics = ran.Select(r => r.Metrics!).ToList();
        s.Append(CultureInfo.InvariantCulture, $"# 运行评测报告\n\n- 时间：{at:yyyy-MM-dd HH:mm} UTC\n- 模型：{label}\n- 场景：{results.Count} 个（{ran.Count} 个运行完成）\n- 运行说明：版本 {Application.Conversations.RunPrompt.Version}\n- 依据：docs/design/agent-loop.md §4\n\n");

        s.Append("## 汇总\n\n| 指标 | 值 |\n| --- | --- |\n");
        Row(s, "检查通过", $"{results.Sum(r => r.Passed)} / {results.Sum(r => r.Checks.Count)}");
        Row(s, "全部检查通过的场景", $"{results.Count(r => r.Failure is null && r.Checks.All(c => c.Passed))} / {results.Count}");
        if (metrics.Count > 0)
        {
            Row(s, "回答完成", $"{metrics.Count(m => m.State == "Complete")} / {metrics.Count}");
            Row(s, "平均模型请求／回答", $"{metrics.Average(m => m.ModelRequests):0.0}");
            Row(s, "平均工具调用／回答", $"{metrics.Average(m => m.ToolCalls):0.0}");
            Row(s, "重复调用（合计）", $"{metrics.Sum(m => m.Repeated)}");
            Row(s, "出错的工具调用（合计）", $"{metrics.Sum(m => m.FailedCalls)}");
            Row(s, "批准卡片（被拒绝）", $"{metrics.Sum(m => m.Approvals)}");
            Row(s, "达到上限", $"{metrics.Count(m => m.LimitReached is not null)}");
            Row(s, "有效出处／写出的编号", $"{metrics.Sum(m => m.ValidCitations)} / {metrics.Sum(m => m.CitedNumbers)}");
            Row(s, "估算输入 token（被测回答，合计）", $"{metrics.Sum(m => m.EstimatedTokens):N0}");
            Row(s, "服务报告的输入／输出 token", metrics.Any(m => m.PromptTokens is not null) ? $"{metrics.Sum(m => m.PromptTokens ?? 0):N0} / {metrics.Sum(m => m.CompletionTokens ?? 0):N0}" : "服务未报告");
            Row(s, "全部模型请求（含标题、摘要与前面的提问）", $"{ran.Sum(r => r.Observation!.ScenarioRequests)}");
            Row(s, "被测回答耗时（合计）", $"{metrics.Sum(m => m.Seconds):0} 秒");
        }

        s.Append("\n## 场景\n\n| 场景 | 类别 | 状态 | 检查 | 请求 | 工具 | 重复 | 出处 | 上限 | 秒 |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var r in results)
        {
            var m = r.Metrics;
            var row = m is null
                ? $"| {r.Scenario.Id} | {r.Scenario.Category} | 未运行 | — | — | — | — | — | — | — |\n"
                : string.Create(CultureInfo.InvariantCulture, $"| {r.Scenario.Id} | {r.Scenario.Category} | {m.State} | {r.Passed}/{r.Checks.Count} | {m.ModelRequests} | {m.ToolCalls} | {m.Repeated} | {m.ValidCitations}/{m.CitedNumbers} | {m.LimitReached ?? "—"} | {m.Seconds:0} |\n");
            s.Append(row);
        }

        foreach (var r in results)
        {
            s.Append(CultureInfo.InvariantCulture, $"\n## {r.Scenario.Id}：{r.Scenario.Purpose}\n\n");
            foreach (var (turn, i) in r.Scenario.Turns.Select((t, i) => (t, i + 1)))
            {
                s.Append(CultureInfo.InvariantCulture, $"- 提问 {i}（{turn.Mode}{(turn.WithAttachments ? "，带附件" : string.Empty)}）：{turn.Question}\n");
            }

            if (r.Scenario.KnownGap is { } gap)
            {
                s.Append(CultureInfo.InvariantCulture, $"- 已知缺口：{gap}\n");
            }

            if (r.Failure is { } failure)
            {
                s.Append(CultureInfo.InvariantCulture, $"\n**未能运行：** {failure}\n");
                continue;
            }

            s.Append('\n');
            foreach (var c in r.Checks)
            {
                s.Append(CultureInfo.InvariantCulture, $"- {(c.Passed ? "✓" : "✗")} {c.Name}{(c.Detail.Length > 0 ? "：" + Escape(c.Detail) : string.Empty)}\n");
            }

            var o = r.Observation!;
            if (o.Calls.Count > 0)
            {
                s.Append("\n工具调用（最后一个提问）：\n\n");
                foreach (var (call, i) in o.Calls.Select((c, i) => (c, i + 1)))
                {
                    s.Append(CultureInfo.InvariantCulture, $"{i}. `{call.Tool}` {Escape(call.Input ?? call.Arguments)} — {call.State}{(call.Approval is { } a ? "，" + a : string.Empty)}\n");
                }
            }

            if (o.ApprovalsAsked.Count > 0)
            {
                s.Append(CultureInfo.InvariantCulture, $"\n批准卡片（已拒绝）：{Escape(string.Join("；", o.ApprovalsAsked))}\n");
            }

            var text = o.Answer.Text.Length > AnswerExcerpt ? o.Answer.Text[..AnswerExcerpt] + "…" : o.Answer.Text;
            s.Append("\n回答：\n\n");
            s.Append(text.Length == 0 ? "> （空）\n" : string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => "> " + l)) + "\n");
        }

        return s.ToString();
    }

    /// <summary>The numbers of each scenario, for comparing one run with another.</summary>
    public static string JsonOf(IReadOnlyList<EvalResult> results, string label, DateTimeOffset at) =>
        JsonSerializer.Serialize(
            new
            {
                at,
                model = label,
                runPrompt = Application.Conversations.RunPrompt.Version,
                scenarios = results.Select(r => new
                {
                    id = r.Scenario.Id,
                    category = r.Scenario.Category,
                    failure = r.Failure,
                    passed = r.Passed,
                    checks = r.Checks.Select(c => new { name = c.Name, passed = c.Passed, detail = c.Detail }),
                    metrics = r.Metrics,
                    answer = r.Observation?.Answer.Text,
                }),
            },
            Json);

    /// <summary>Writes both files next to each other; returns the Markdown file's path.</summary>
    public static async Task<string> WriteAsync(IReadOnlyList<EvalResult> results, string label, string path, CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, Markdown(results, label, at), ct);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".json"), JsonOf(results, label, at), ct);
        return path;
    }

    private static void Row(StringBuilder s, string name, string value) => s.Append(CultureInfo.InvariantCulture, $"| {name} | {value} |\n");

    private static string Escape(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);
}
