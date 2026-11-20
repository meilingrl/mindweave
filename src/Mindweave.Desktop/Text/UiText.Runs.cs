using System.Globalization;
using Mindweave.Application.Conversations;
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
    public const string ToolsIntro = "模型可以在回答时检索本会话的附件与本工作区的总结网络（只读本机数据）；设置下面的网络搜索后，还可以搜索网络、读取网页；添加 MCP 服务器并启用它的工具后，还可以使用第三方工具。";
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
        return RunActivity(calledTools.Select(t => (t, (string?)null)));
    }

    /// <summary>The activity line from each call's tool and its step summary (a third-party tool is named "server › tool").</summary>
    public static string RunActivity(IEnumerable<(string Tool, string? Summary)> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        return string.Join(" · ", calls.Select(c => (c.Tool, Label: ToolLabel(c.Tool, c.Summary))).GroupBy(c => c.Label, StringComparer.Ordinal).Select(g => g.First().Tool switch
        {
            "search_attachments" => string.Format(CultureInfo.InvariantCulture, "检索了 {0} 次附件", g.Count()),
            "search_summaries" => string.Format(CultureInfo.InvariantCulture, "检索了 {0} 次总结网络", g.Count()),
            "web_search" => string.Format(CultureInfo.InvariantCulture, "搜索了 {0} 次", g.Count()),
            "fetch_page" => string.Format(CultureInfo.InvariantCulture, "读了 {0} 个网页", g.Count()),
            _ => string.Format(CultureInfo.InvariantCulture, "调用了 {0} 次 {1}", g.Count(), g.Key),
        }));
    }

    /// <summary>
    /// The status line of a running answer (docs/design/reasoning-and-status.md §2). <paramref name="tool"/> and its step
    /// <paramref name="summary"/> name the running tool; <paramref name="approval"/> the call waiting for the user.
    /// </summary>
    public static string AnswerStatus(AnswerStage stage, string? tool = null, string? summary = null, PendingApproval? approval = null) => stage switch
    {
        AnswerStage.Approval when approval is { IsThirdParty: true } card => McpApprovalWaiting(card.Server!, card.ToolTitle ?? card.Tool),
        AnswerStage.Approval => ApprovalWaiting,
        AnswerStage.Tool when tool is not null => ToolStatus(tool, summary),
        AnswerStage.Compressing => CompressingContext,
        AnswerStage.Thinking => ModelThinking,
        AnswerStage.Writing => AnswerWriting,
        AnswerStage.Retrying => AnswerRetrying,
        AnswerStage.Finishing => AnswerFinishing,
        _ => WaitingForAnswer,
    };

    /// <summary>The longest object (search terms, a host) a status names before it is cut.</summary>
    private const int StatusObjectLength = 40;

    /// <summary>"正在搜索：蒸汽机…", "正在读取网页：zh.wikipedia.org…", "正在调用 files › echo…".</summary>
    private static string ToolStatus(string tool, string? summary)
    {
        var cut = summary?.IndexOf('：', StringComparison.Ordinal) ?? -1;
        var subject = cut >= 0 ? summary![(cut + 1)..].Trim() : string.Empty;
        if (tool == "fetch_page" && Uri.TryCreate(subject, UriKind.Absolute, out var page))
        {
            subject = page.Host;
        }

        var named = subject.Length == 0 ? string.Empty
            : "：" + (subject.Length > StatusObjectLength ? subject[..StatusObjectLength] + "…" : subject);
        return tool switch
        {
            "web_search" => "正在搜索" + named + "…",
            "fetch_page" => "正在读取网页" + named + "…",
            "search_attachments" => "正在检索附件" + named + "…",
            "search_summaries" => "正在检索总结网络" + named + "…",
            _ => "正在调用 " + ToolLabel(tool, summary) + "…",
        };
    }

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
