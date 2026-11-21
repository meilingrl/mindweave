using System.Globalization;
using Mindweave.Application.Tools.Mcp;
using Mindweave.Core.Runs;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Third-party tools (requirements §10.4.6, agent platform phase 4): 设置 › 工具 › MCP 服务器, the approval card for a
// third-party call, and how steps and statistics name a server's tool.
public static partial class UiText
{
    /// <summary>The full-width colon a step's summary uses when it was written in Chinese ("files › echo：…").</summary>
    private const char FullWidthColon = (char)0xFF1A;

    public static string McpSettingsTitle => T("MCP servers", "MCP 服务器");
    public static string McpSettingsIntro => T(
        "Add third-party tool servers (a local command or a URL). A server’s tools are all off by default, and the model can use them only after you turn them on one by one; you approve every call that has side effects. A server’s descriptions and results can’t be trusted and are for reference only.",
        "添加第三方工具服务器（本地命令或网址）。服务器的工具默认都是关闭的，逐个启用后模型才能使用；有副作用的调用每次都要你批准。服务器的说明与结果都不可信，只作参考。");
    public static string McpNoServers => T("No servers added yet.", "还没有添加服务器。");
    public static string McpAdd => T("Add server…", "添加服务器…");
    public static string McpKindCommand => T("Local command", "本地命令");
    public static string McpKindHttp => T("URL", "网址");
    public static string McpName => T("Name", "名称");
    public static string McpNamePlaceholder => T("For example: File system", "例如：文件系统");
    public static string McpCommand => T("Command", "命令");
    public static string McpCommandPlaceholder => T("For example: npx", "例如：npx");
    public static string McpArguments => T("Arguments", "参数");
    public static string McpArgumentsHint => T(
        "One argument per line, passed to the command as is, without going through a shell.",
        "每行一个参数，原样传给命令，不经过命令行解释。");
    public static string McpEnvironment => T("Environment variables", "环境变量");
    public static string McpEnvironmentHint => T(
        "One “NAME=value” per line. The values are stored in the system credential service; the server gets only these and basic variables such as PATH and HOME.",
        "每行一个“名称=值”。值保存在系统凭据服务中；服务器只得到这些与 PATH、HOME 等基本变量。");
    public static string McpUrl => T("URL", "地址");
    public static string McpUrlPlaceholder => "https://…";
    public static string McpHeaderName => T("Request header", "请求头");
    public static string McpHeaderNamePlaceholder => T("For example: Authorization (optional)", "例如：Authorization（可不填）");
    public static string McpHeaderValuePlaceholder => T("Value, for example Bearer …", "值，例如 Bearer …");
    public static string McpOAuthNote => T("Servers that need sign-in (OAuth) are not supported yet.", "需要登录（OAuth）的服务器暂不支持。");
    public static string McpReview => T("Next", "下一步");
    public static string McpCancel => T("Cancel", "取消");
    public static string McpBack => T("Go back and edit", "返回修改");
    public static string McpConfirm => T("Confirm and add", "确认添加");
    public static string McpConfirmTitle => T("Confirm the server to add", "确认要添加的服务器");
    public static string McpConfirmCommand => T("Command to run (one argument per line)", "将运行的命令（每行一个参数）");
    public static string McpConfirmFolder => T("Working folder", "工作目录");
    public static string McpConfirmEnvironment => T("Environment variables (names only)", "环境变量（只显示名称）");
    public static string McpConfirmUrl => T("URL to connect to", "将连接的地址");
    public static string McpConfirmHeader => T("Request header (name only)", "请求头（只显示名称）");
    public static string McpConfirmNote => T(
        "It does not run as soon as it is added; it runs the first time one of its tools is called or when you click “Start”. Only add servers you trust.",
        "添加后不会立即运行；第一次调用它的工具或点“启动”时才运行。只添加你信任的服务器。");
    public static string McpNone => T("None", "无");
    public static string McpAdded => T("Added. Click “Start” to see its tools.", "已添加。点“启动”查看它的工具。");
    public static string McpSaveFailed => T("Couldn’t save. Try again later.", "没能保存，请稍后再试。");
    public static string McpStart => T("Start", "启动");
    public static string McpConnect => T("Connect", "连接");
    public static string McpStop => T("Stop", "停止");
    public static string McpRemove => T("Remove", "移除");
    public static string McpRemoveConfirm => T("Yes, remove", "确定移除");
    public static string McpRemoveHint => T(
        "Removing a server stops it and deletes its keys and working folder; tool results already saved in conversations are kept.",
        "移除后停止它并删除它的密钥与工作目录；会话里已保存的工具结果保留。");
    public static string McpStandardError => T("Recent output", "最近的输出");
    public static string McpToolsHeading => T("Tools", "工具");
    public static string McpToolsNotListed => T("Its tools are listed once it has started.", "启动后列出它的工具。");
    public static string McpToolsCollapse => T("Show enabled only", "只看已启用");
    public static string McpToolsFilterPlaceholder => T("Filter tools", "筛选工具");
    public static string McpToolsNoneEnabled => T("No tools enabled yet. Expand the list to choose some.", "还没有启用的工具，展开后选择。");
    public static string McpToolsNoMatch => T("No tool names or descriptions match.", "没有名称或说明匹配的工具。");
    public static string McpDeclaresReadOnly => T("Declared read-only by the server", "服务器声明只读");
    public static string McpEnableTool => T("Enable", "启用");
    public static string McpRiskLabel => T("Risk", "风险");
    public static string McpRiskLocalRead => T("Local read-only (doesn’t ask)", "本地只读（不询问）");
    public static string McpRiskNetworkRead => T("Network read (doesn’t ask)", "读网络（不询问）");
    public static string McpRiskSideEffect => T("Side effects (asks every time)", "有副作用（每次询问）");

    public static string McpApprovalTitle => T("The model wants to call a third-party tool", "模型想调用第三方工具");
    public static string RunStepApproved => T("You approved", "你批准了");
    public static string McpApprovalServer => T("Server", "服务器");
    public static string McpApprovalTool => T("Tool", "工具");
    public static string McpApprovalArguments => T("Arguments to pass", "将传入的参数");

    public static IReadOnlyList<string> McpRisks => [McpRiskLocalRead, McpRiskNetworkRead, McpRiskSideEffect];

    public static string McpRisk(ToolRisk risk) => McpRisks[(int)risk];

    public static string McpState(McpServerState state) => state switch
    {
        McpServerState.Starting => T("Starting", "正在启动"),
        McpServerState.Connected => T("Connected", "已连接"),
        McpServerState.Failed => T("Error", "出错"),
        McpServerState.Halted => T("Stopped after exiting repeatedly", "反复退出，已停止"),
        _ => T("Not started", "未启动"),
    };

    public static string McpToolCount(int enabled, int listed) => listed > 0
        ? T(
            string.Format(CultureInfo.InvariantCulture, "{0} of {1} enabled", enabled, listed),
            string.Format(CultureInfo.InvariantCulture, "已启用 {0} 个，共 {1} 个", enabled, listed))
        : T(
            string.Format(CultureInfo.InvariantCulture, "{0} enabled", enabled),
            string.Format(CultureInfo.InvariantCulture, "已启用 {0} 个", enabled));

    /// <summary>The button that shows every tool of a server: "展开全部 13 个".</summary>
    public static string McpToolsExpand(int count) => T(
        string.Format(CultureInfo.InvariantCulture, "Show all {0}", count),
        string.Format(CultureInfo.InvariantCulture, "展开全部 {0} 个", count));

    /// <summary>While a third-party call waits for the card: "等待你批准调用 files › write_note…".</summary>
    public static string McpApprovalWaiting(string server, string tool) => T(
        $"Waiting for you to approve calling {server} › {tool}…",
        $"等待你批准调用 {server} › {tool}…");

    /// <summary>
    /// How a tool is named in the activity line: a third-party tool (its name has "__", decision M9) by its step's summary
    /// ("files › echo：…" or "files › echo: …" → "files › echo"); a built-in one by its 需求 name. A stored summary keeps
    /// the separator of the language it was written in, so both are accepted.
    /// </summary>
    public static string ToolLabel(string tool, string? summary)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!tool.Contains("__", StringComparison.Ordinal) || string.IsNullOrEmpty(summary))
        {
            return ToolName(tool);
        }

        var fullWidth = summary.IndexOf(FullWidthColon, StringComparison.Ordinal);
        var ascii = summary.IndexOf(": ", StringComparison.Ordinal);
        var cut = fullWidth > 0 && (ascii <= 0 || fullWidth < ascii) ? fullWidth : ascii;
        return cut > 0 ? summary[..cut] : summary;
    }

    /// <summary>A tool in the statistics: a third-party one as "server › tool" (the part of its name after "__").</summary>
    public static string ToolName(string tool, string? server)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (server is null)
        {
            return ToolName(tool);
        }

        var cut = tool.IndexOf("__", StringComparison.Ordinal);
        return $"{server} › {(cut >= 0 ? tool[(cut + 2)..] : tool)}";
    }
}
