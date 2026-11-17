using System.Globalization;
using Mindweave.Core.Runs;

namespace Mindweave.Desktop.Text;

// Runs and tools (requirements §10.4.4, agent platform phase 2): the quiet activity line inside an answer, its steps, the note
// when a limit was reached, and 设置 › 工具.
public static partial class UiText
{
    public const string SearchAttachmentsName = "检索附件";
    public const string SearchSummariesName = "检索总结网络";
    public const string RunStepsExpand = "展开步骤";
    public const string RunStepsCollapse = "收起步骤";
    public const string OpenToolResult = "查看结果";
    public const string ToolResultType = "工具结果";
    public const string RunStepRunning = "进行中";
    public const string RunStepDone = "完成";
    public const string RunStepFailed = "出错";
    public const string RunStepStopped = "已停止";
    public const string RunStepSkipped = "未执行";
    public const string RunStepUnknown = "结果未知，请自行核对";

    public const string SettingsTools = "工具";
    public const string ToolsIntro = "模型可以在回答时检索本会话的附件与本工作区的总结网络，只读本机数据，不需要新的密钥。";
    public const string UseTools = "使用工具";
    public const string UseToolsHint = "关闭后，回答只发送一次请求，不调用工具。";
    public const string ToolsUnavailable = "当前模型不能调用工具，可在设置 › 模型连接 › 高级设置中声明";
    public const string RunLimitsTitle = "上限";
    public const string RunLimitsHint = "每次回答的上限。达到任一项时，模型根据已经取得的结果作答。";
    public const string LimitModelRequests = "模型请求";
    public const string LimitToolCalls = "工具调用";
    public const string LimitMinutes = "时长（分钟）";
    public const string LimitTokens = "累计估算 token";
    public const string LimitTimesUnit = "次";
    public const string RestoreDefaultLimits = "恢复默认";
    public const string LimitsSaved = "已保存，对之后的提问生效";
    public const string ToolSettingsSaveFailed = "工具设置未能保存，请稍后再试。";

    public const string ToolUsageSection = "工具";

    /// <summary>The statistics page's line about runs: "使用了工具的回答 3 次 · 平均每次 2.7 次模型请求".</summary>
    public static string ToolRuns(int runs, double requestsPerRun) =>
        string.Format(CultureInfo.InvariantCulture, "使用了工具的回答 {0} 次 · 平均每次 {1:0.#} 次模型请求", runs, requestsPerRun);

    public static string ToolCallCount(int calls) => string.Format(CultureInfo.InvariantCulture, "调用 {0} 次", calls);

    /// <summary>A tool's name as a person sees it.</summary>
    public static string ToolName(string tool) => tool switch
    {
        "search_attachments" => SearchAttachmentsName,
        "search_summaries" => SearchSummariesName,
        "web_search" => WebSearchName,
        "fetch_page" => FetchPageName,
        _ => tool,
    };

    /// <summary>The activity line of a run that has finished its tools: "检索了 2 次附件 · 检索了 1 次总结网络".</summary>
    public static string RunActivity(IEnumerable<string> calledTools)
    {
        ArgumentNullException.ThrowIfNull(calledTools);
        return string.Join(" · ", calledTools.GroupBy(t => t, StringComparer.Ordinal).Select(g => g.Key switch
        {
            "search_attachments" => string.Format(CultureInfo.InvariantCulture, "检索了 {0} 次附件", g.Count()),
            "search_summaries" => string.Format(CultureInfo.InvariantCulture, "检索了 {0} 次总结网络", g.Count()),
            "web_search" => string.Format(CultureInfo.InvariantCulture, "搜索了 {0} 次", g.Count()),
            "fetch_page" => string.Format(CultureInfo.InvariantCulture, "读了 {0} 个网页", g.Count()),
            _ => string.Format(CultureInfo.InvariantCulture, "调用了 {0} 次 {1}", g.Count(), g.Key),
        }));
    }

    /// <summary>While a tool runs: "正在检索附件…".</summary>
    public static string ToolRunning(string tool) => tool switch
    {
        "web_search" => "正在搜索…",
        "fetch_page" => "正在读取网页…",
        _ => "正在" + ToolName(tool) + "…",
    };

    /// <summary>The note under an answer whose run reached a limit.</summary>
    public static string RunLimitNote(RunLimit limit) => "达到了本次回答的" + limit switch
    {
        RunLimit.ModelRequests => "模型请求次数",
        RunLimit.ToolCalls => "工具调用次数",
        RunLimit.Duration => "时长",
        _ => "token 用量",
    } + "上限，根据已经取得的结果作答。";

    /// <summary>A step's time: "0.4 秒", "12 秒".</summary>
    public static string StepDuration(TimeSpan span) => span < TimeSpan.FromSeconds(10)
        ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} 秒", Math.Max(0, span.TotalSeconds))
        : Elapsed(span);
}
