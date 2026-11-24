using System.Globalization;
using Mindweave.Application.Conversations;
using Mindweave.Core.Runs;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Runs and tools (requirements §10.4.4, agent platform phase 2): the quiet activity line inside an answer, its steps, the note
// when a limit was reached, and 设置 › 工具.
public static partial class UiText
{
    public static string SearchAttachmentsName => T("Search attachments", "检索附件");
    public static string SearchSummariesName => T("Search summaries", "检索总结");
    public static string RunStepsExpand => T("Show steps", "展开步骤");
    public static string RunStepsCollapse => T("Hide steps", "收起步骤");
    public static string OpenToolResult => T("View result", "查看结果");
    public static string ToolResultType => T("Tool result", "工具结果");
    public static string RunStepRunning => T("In progress", "进行中");
    public static string RunStepDone => T("Done", "完成");
    public static string RunStepFailed => T("Failed", "出错");
    public static string RunStepStopped => T("Stopped", "已停止");
    public static string RunStepSkipped => T("Not run", "未执行");
    public static string RunStepUnknown => T("Result unknown; please check it yourself", "结果未知，请自行核对");

    public static string SettingsTools => T("Tools", "工具");
    public static string ToolsIntro => T(
        "When answering, the model can search this conversation’s attachments and this workspace’s summaries (this reads only data on this computer). Once you set up web search below, it can also search the web and read web pages. Once you add an MCP server and turn on its tools, it can also use third-party tools.",
        "模型可以在回答时检索本会话的附件与本工作区的总结（只读本机数据）；设置下面的网络搜索后，还可以搜索网络、读取网页；添加 MCP 服务器并启用它的工具后，还可以使用第三方工具。");
    public static string UseTools => T("Use tools", "使用工具");
    public static string UseToolsHint => T("When this is off, each answer sends a single request and calls no tools.", "关闭后，回答只发送一次请求，不调用工具。");
    public static string ToolsUnavailable => T(
        "The current model can’t call tools. You can declare tool support in Settings › Model connection › Advanced settings",
        "当前模型不能调用工具，可在设置 › 模型连接 › 高级设置中声明");
    public static string RunLimitsTitle => T("Limits", "上限");
    public static string RunLimitsHint => T(
        "Limits for each answer. When any of them is reached, the model answers from the results it already has.",
        "每次回答的上限。达到任一项时，模型根据已经取得的结果作答。");
    public static string LimitModelRequests => T("Model requests", "模型请求");
    public static string LimitToolCalls => T("Tool calls", "工具调用");
    public static string LimitMinutes => T("Duration (minutes)", "时长（分钟）");
    public static string LimitTokens => T("Estimated total tokens", "累计估算 token");
    public static string RestoreDefaultLimits => T("Restore defaults", "恢复默认");
    public static string LimitsSaved => T("Saved; applies to questions you ask from now on", "已保存，对之后的提问生效");
    public static string ToolSettingsSaveFailed => T("Tool settings couldn’t be saved. Please try again later.", "工具设置未能保存，请稍后再试。");

    public static string ToolUsageSection => T("Tools", "工具");

    /// <summary>The statistics page's line about runs: "使用了工具的回答 3 次 · 平均每次 2.7 次模型请求".</summary>
    public static string ToolRuns(int runs, double requestsPerRun) => T(
        string.Format(CultureInfo.InvariantCulture, "Answers that used tools: {0} · Average model requests per answer: {1:0.#}", runs, requestsPerRun),
        string.Format(CultureInfo.InvariantCulture, "使用了工具的回答 {0} 次 · 平均每次 {1:0.#} 次模型请求", runs, requestsPerRun));

    public static string ToolCallCount(int calls) => T(Plural(calls, "call", "calls"), string.Format(CultureInfo.InvariantCulture, "调用 {0} 次", calls));

    /// <summary>A tool's name as a person sees it.</summary>
    public static string ToolName(string tool) => tool switch
    {
        "search_attachments" => SearchAttachmentsName,
        "search_summaries" => SearchSummariesName,
        "web_search" => WebSearchName,
        "fetch_page" => FetchPageName,
        _ => tool,
    };

    /// <summary>The activity line of a run that has finished its tools: "检索了 2 次附件 · 检索了 1 次总结".</summary>
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
            "search_attachments" => T(
                Plural(g.Count(), "attachment search", "attachment searches"),
                string.Format(CultureInfo.InvariantCulture, "检索了 {0} 次附件", g.Count())),
            "search_summaries" => T(
                Plural(g.Count(), "summary search", "summary searches"),
                string.Format(CultureInfo.InvariantCulture, "检索了 {0} 次总结", g.Count())),
            "web_search" => T(
                Plural(g.Count(), "web search", "web searches"),
                string.Format(CultureInfo.InvariantCulture, "搜索了 {0} 次", g.Count())),
            "fetch_page" => T(
                Plural(g.Count(), "web page read", "web pages read"),
                string.Format(CultureInfo.InvariantCulture, "读了 {0} 个网页", g.Count())),
            _ => T(
                Plural(g.Count(), "call", "calls") + " to " + g.Key,
                string.Format(CultureInfo.InvariantCulture, "调用了 {0} 次 {1}", g.Count(), g.Key)),
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
        // A step summary written in English separates its object with ": " instead of "："; "：" wins whenever it is
        // there, since a server's name may contain ": " (as in ToolLabel).
        var fullWidth = summary?.IndexOf('：', StringComparison.Ordinal) ?? -1;
        var ascii = summary?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
        var useAscii = fullWidth < 0 && ascii >= 0;
        var cut = useAscii ? ascii : fullWidth;
        var width = useAscii ? 2 : 1;

        var subject = cut >= 0 ? summary![(cut + width)..].Trim() : string.Empty;
        if (tool == "fetch_page" && Uri.TryCreate(subject, UriKind.Absolute, out var page))
        {
            subject = page.Host;
        }

        var named = subject.Length == 0 ? string.Empty
            : T(": ", "：") + (subject.Length > StatusObjectLength ? subject[..StatusObjectLength] + "…" : subject);
        return tool switch
        {
            "web_search" => T("Searching" + named + "…", "正在搜索" + named + "…"),
            "fetch_page" => T("Reading web page" + named + "…", "正在读取网页" + named + "…"),
            "search_attachments" => T("Searching attachments" + named + "…", "正在检索附件" + named + "…"),
            "search_summaries" => T("Searching summaries" + named + "…", "正在检索总结" + named + "…"),
            _ => T("Calling " + ToolLabel(tool, summary) + "…", "正在调用 " + ToolLabel(tool, summary) + "…"),
        };
    }

    /// <summary>The note under an answer whose run reached a limit.</summary>
    public static string RunLimitNote(RunLimit limit) => T(
        "This answer reached its " + limit switch
        {
            RunLimit.ModelRequests => "model request",
            RunLimit.ToolCalls => "tool call",
            RunLimit.Duration => "time",
            _ => "token",
        } + " limit, so the model answered from the results it already had.",
        "达到了本次回答的" + limit switch
        {
            RunLimit.ModelRequests => "模型请求次数",
            RunLimit.ToolCalls => "工具调用次数",
            RunLimit.Duration => "时长",
            _ => "token 用量",
        } + "上限，根据已经取得的结果作答。");

    /// <summary>A step's time: "0.4 秒", "12 秒".</summary>
    public static string StepDuration(TimeSpan span) => span < TimeSpan.FromSeconds(10)
        ? T(
            string.Format(CultureInfo.InvariantCulture, "{0:0.0} s", Math.Max(0, span.TotalSeconds)),
            string.Format(CultureInfo.InvariantCulture, "{0:0.0} 秒", Math.Max(0, span.TotalSeconds)))
        : Elapsed(span);
}
