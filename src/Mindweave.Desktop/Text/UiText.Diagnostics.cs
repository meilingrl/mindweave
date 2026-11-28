using System.Globalization;
using Mindweave.Application.Diagnostics;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// 设置 › 诊断 (D2 §2.1, §2.2; requirements §10.8): recent requests, the log, and the diagnostic package with its dialog.
public static partial class UiText
{
    public static string DiagnosticsLink => T("Diagnostics", "诊断");
    public static string DiagnosticsTitle => T("Diagnostics", "诊断");
    public static string RecentRequestsSection => T("Recent requests", "最近的请求");
    public static string RequestsFailuresOnly => T("Failures only", "只看失败");
    public static string RequestsAll => T("All", "全部");
    public static string RequestsLoading => T("Reading the request records…", "正在读取请求记录…");
    public static string RequestsReadFailed => T("The request records couldn’t be read.", "无法读取请求记录。");
    public static string RequestsHoldNoContent => T("Only times, models and results are kept; no question or answer is stored here.", "这里只记录时间、模型和结果，不保存提问或回答。");
    public static string LogSection => T("Log", "日志");
    public static string DiagnosticPackageSection => T("Diagnostic package", "诊断包");
    public static string DiagnosticPackageIntro => T(
        "A zip of plain files to send to someone helping you: versions, sizes, recent request records, settings that are not secret, and the log. Mindweave sends nothing itself.",
        "一个由纯文本文件组成的压缩包，可以发给帮你排查问题的人：版本、大小、最近的请求记录、非机密设置和日志。Mindweave 自己不会发送任何内容。");
    public static string ExportDiagnosticPackage => T("Export diagnostic package…", "导出诊断包…");
    public static string ShowInRequestList => T("Show in request list", "在请求列表中查看");

    public static string RequestsCount(int count, bool failuresOnly, bool more) => (failuresOnly, count) switch
    {
        (true, 0) => T("No failed requests in the last 14 days.", "最近 14 天没有失败的请求。"),
        (false, 0) => T("No requests in the last 14 days.", "最近 14 天没有请求。"),
        (true, _) => T(
            string.Format(Culture, "{0}{1} failed {2} in the last 14 days", more ? "the latest " : string.Empty, count, count == 1 ? "request" : "requests"),
            string.Format(CultureInfo.CurrentCulture, "最近 14 天{0}失败 {1} 次", more ? "最新的" : string.Empty, count)),
        (false, _) => T(
            string.Format(Culture, "{0}{1} {2} in the last 14 days", more ? "the latest " : string.Empty, count, count == 1 ? "request" : "requests"),
            string.Format(CultureInfo.CurrentCulture, "最近 14 天{0}请求 {1} 次", more ? "最新的" : string.Empty, count)),
    };

    /// <summary>What a request was for, from its stored purpose; an unknown one is shown as stored.</summary>
    public static string RequestPurpose(string purpose) => purpose switch
    {
        "answer" => T("Answer", "回答"),
        "title" => T("Title", "标题"),
        "summary" => T("Summary", "总结"),
        "naming" => T("Naming", "命名"),
        "compression" => T("Compression", "压缩"),
        "resource_summary" => T("Attachment summary", "附件摘要"),
        "workspace-name" => T("Workspace names", "工作区名称"),
        "recommend-questions" => T("Suggested questions", "推荐问题"),
        "recommend-nodes" => T("Suggested directions", "推荐方向"),
        "suggest-links" => T("Link suggestions", "关联建议"),
        _ => purpose,
    };

    public static string RequestResult(RequestRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Outcome switch
        {
            AiRequestOutcome.Completed => T("Completed", "完成"),
            AiRequestOutcome.Truncated => T("Cut off", "截断"),
            AiRequestOutcome.Stopped => T("Stopped", "已停止"),
            _ => T("Failed: ", "失败：") + (row.ErrorKind is { } kind ? ErrorKind(kind) : T("unknown error", "未知错误")),
        };
    }

    public static string RequestUsageLabel => T("Usage", "用量");
    public static string RequestCacheLabel => T("Cache", "缓存");
    public static string RequestTimingLabel => T("First response", "首个响应");
    public static string NotReported => T("not reported", "未返回");

    /// <summary>"input 1,234 tokens · output 567 tokens"; a number the service did not send reads "not reported".</summary>
    public static string RequestUsage(int? input, int? output) => T(
        $"input {TokensOrNot(input)} · output {TokensOrNot(output)}",
        $"输入 {TokensOrNot(input)} · 输出 {TokensOrNot(output)}");

    /// <summary>The prompt caching design's rule (D-045): a number the provider did not send is "not reported", never 0.</summary>
    public static string RequestCache(int? read, int? written) => T(
        $"read {TokensOrNot(read)} · written {TokensOrNot(written)}",
        $"读取 {TokensOrNot(read)} · 写入 {TokensOrNot(written)}");

    public static string RequestTiming(TimeSpan? response, TimeSpan? firstText) => T(
        $"{(response is { } r ? FailureDuration(r) : NotReported)} · first text {(firstText is { } f ? FailureDuration(f) : "none")}",
        $"{(response is { } r2 ? FailureDuration(r2) : NotReported)} · 首段文字 {(firstText is { } f2 ? FailureDuration(f2) : "无")}");

    private static string TokensOrNot(int? count) => count is { } n ? T(Tokens(n) + " tokens", Tokens(n) + " token") : NotReported;

    // The export dialog.
    public static string DiagnosticDialogTitle => T("Export diagnostic package", "导出诊断包");
    public static string DiagnosticWhatLeaves => T("What will be in it", "包里会有");
    public static string DiagnosticSummaryFile => T("summary.txt: versions, system, language, sizes and counts, the backups", "summary.txt：版本、系统、语言、大小与数量、备份列表");
    public static string DiagnosticSettingsFile => T("settings.txt: settings that are not secret", "settings.txt：非机密设置");
    public static string DiagnosticModelsAndHosts => T("Model names and service hosts", "模型名称与服务地址");
    public static string DiagnosticModelsAndHostsHint => T("Left out, they read “model 1”, “host 1”.", "不勾选时显示为“model 1”“host 1”。");
    public static string DiagnosticLogNote => T(
        "The log can hold error text from services and tools. The package is plain files: you can open and read it before sending.",
        "日志可能包含服务和工具返回的错误文字。诊断包是纯文本文件，发送前可以打开查看。");
    public static string DiagnosticNeverIn => T(
        "Never in it: conversation text, titles, workspace names, attachments, keys, or an MCP server’s command, arguments, environment or headers.",
        "绝不包含：对话内容、标题、工作区名称、附件、密钥，以及 MCP 服务器的命令、参数、环境变量或请求头。");
    public static string DiagnosticWithoutData => T(
        "The data couldn’t be opened, so the package holds the summary and the log only.",
        "数据无法打开，诊断包只包含摘要和日志。");
    public static string DiagnosticReading => T("Reading what will be in it…", "正在读取包里的内容…");
    public static string DiagnosticSave => T("Save…", "保存…");
    public static string DiagnosticSaving => T("Saving…", "正在保存…");
    public static string DiagnosticSaved(string file) => T("Saved as " + file + ".", "已保存为 " + file + "。");
    public static string DiagnosticFileType => T("Diagnostic package", "诊断包");

    public static string DiagnosticRequestsFile(int count) => T(
        string.Format(Culture, "requests.csv: {0:N0} request records of the last 14 days", count),
        string.Format(CultureInfo.CurrentCulture, "requests.csv：最近 14 天的 {0:N0} 条请求记录", count));

    public static string DiagnosticLogs(int files, long bytes) => files == 0 ? T("Logs: none yet", "日志：暂无") : T(
        string.Format(Culture, "Logs: {0} {1}, {2}", files, files == 1 ? "file" : "files", FormatSize(bytes)),
        string.Format(CultureInfo.CurrentCulture, "日志：{0} 个文件，{1}", files, FormatSize(bytes)));
}
