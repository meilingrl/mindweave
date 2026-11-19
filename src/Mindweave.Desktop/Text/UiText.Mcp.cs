using System.Globalization;
using Mindweave.Application.Tools.Mcp;
using Mindweave.Core.Runs;

namespace Mindweave.Desktop.Text;

// Third-party tools (requirements §10.4.6, agent platform phase 4): 设置 › 工具 › MCP 服务器, the approval card for a
// third-party call, and how steps and statistics name a server's tool.
public static partial class UiText
{
    public const string McpSettingsTitle = "MCP 服务器";
    public const string McpSettingsIntro = "添加第三方工具服务器（本地命令或网址）。服务器的工具默认都是关闭的，逐个启用后模型才能使用；有副作用的调用每次都要你批准。服务器的说明与结果都不可信，只作参考。";
    public const string McpNoServers = "还没有添加服务器。";
    public const string McpAdd = "添加服务器…";
    public const string McpKindCommand = "本地命令";
    public const string McpKindHttp = "网址";
    public const string McpName = "名称";
    public const string McpNamePlaceholder = "例如：文件系统";
    public const string McpCommand = "命令";
    public const string McpCommandPlaceholder = "例如：npx";
    public const string McpArguments = "参数";
    public const string McpArgumentsHint = "每行一个参数，原样传给命令，不经过命令行解释。";
    public const string McpEnvironment = "环境变量";
    public const string McpEnvironmentHint = "每行一个“名称=值”。值保存在系统凭据服务中；服务器只得到这些与 PATH、HOME 等基本变量。";
    public const string McpUrl = "地址";
    public const string McpUrlPlaceholder = "https://…";
    public const string McpHeaderName = "请求头";
    public const string McpHeaderNamePlaceholder = "例如：Authorization（可不填）";
    public const string McpHeaderValuePlaceholder = "值，例如 Bearer …";
    public const string McpOAuthNote = "需要登录（OAuth）的服务器暂不支持。";
    public const string McpReview = "下一步";
    public const string McpCancel = "取消";
    public const string McpBack = "返回修改";
    public const string McpConfirm = "确认添加";
    public const string McpConfirmTitle = "确认要添加的服务器";
    public const string McpConfirmCommand = "将运行的命令（每行一个参数）";
    public const string McpConfirmFolder = "工作目录";
    public const string McpConfirmEnvironment = "环境变量（只显示名称）";
    public const string McpConfirmUrl = "将连接的地址";
    public const string McpConfirmHeader = "请求头（只显示名称）";
    public const string McpConfirmNote = "添加后不会立即运行；第一次调用它的工具或点“启动”时才运行。只添加你信任的服务器。";
    public const string McpNone = "无";
    public const string McpAdded = "已添加。点“启动”查看它的工具。";
    public const string McpSaveFailed = "没能保存，请稍后再试。";
    public const string McpStart = "启动";
    public const string McpConnect = "连接";
    public const string McpStop = "停止";
    public const string McpRemove = "移除";
    public const string McpRemoveConfirm = "确定移除";
    public const string McpRemoveHint = "移除后停止它并删除它的密钥与工作目录；会话里已保存的工具结果保留。";
    public const string McpStandardError = "最近的输出";
    public const string McpToolsHeading = "工具";
    public const string McpToolsNotListed = "启动后列出它的工具。";
    public const string McpToolsCollapse = "只看已启用";
    public const string McpToolsFilterPlaceholder = "筛选工具";
    public const string McpToolsNoneEnabled = "还没有启用的工具，展开后选择。";
    public const string McpToolsNoMatch = "没有名称或说明匹配的工具。";
    public const string McpDeclaresReadOnly = "服务器声明只读";
    public const string McpEnableTool = "启用";
    public const string McpRiskLabel = "风险";
    public const string McpRiskLocalRead = "本地只读（不询问）";
    public const string McpRiskNetworkRead = "读网络（不询问）";
    public const string McpRiskSideEffect = "有副作用（每次询问）";

    public const string McpApprovalTitle = "模型想调用第三方工具";
    public const string RunStepApproved = "你批准了";
    public const string McpApprovalServer = "服务器";
    public const string McpApprovalTool = "工具";
    public const string McpApprovalArguments = "将传入的参数";

    public static IReadOnlyList<string> McpRisks { get; } = [McpRiskLocalRead, McpRiskNetworkRead, McpRiskSideEffect];

    public static string McpRisk(ToolRisk risk) => McpRisks[(int)risk];

    public static string McpState(McpServerState state) => state switch
    {
        McpServerState.Starting => "正在启动",
        McpServerState.Connected => "已连接",
        McpServerState.Failed => "出错",
        McpServerState.Halted => "反复退出，已停止",
        _ => "未启动",
    };

    public static string McpToolCount(int enabled, int listed) => listed > 0
        ? string.Format(CultureInfo.InvariantCulture, "已启用 {0} 个，共 {1} 个", enabled, listed)
        : string.Format(CultureInfo.InvariantCulture, "已启用 {0} 个", enabled);

    /// <summary>The button that shows every tool of a server: "展开全部 13 个".</summary>
    public static string McpToolsExpand(int count) => string.Format(CultureInfo.InvariantCulture, "展开全部 {0} 个", count);

    /// <summary>While a third-party call waits for the card: "等待你批准调用 files › write_note…".</summary>
    public static string McpApprovalWaiting(string server, string tool) => $"等待你批准调用 {server} › {tool}…";

    /// <summary>
    /// How a tool is named in the activity line: a third-party tool (its name has "__", decision M9) by its step's summary
    /// ("files › echo：…" → "files › echo"); a built-in one by its 需求 name.
    /// </summary>
    public static string ToolLabel(string tool, string? summary)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!tool.Contains("__", StringComparison.Ordinal) || string.IsNullOrEmpty(summary))
        {
            return ToolName(tool);
        }

        var cut = summary.IndexOf('：', StringComparison.Ordinal);
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
