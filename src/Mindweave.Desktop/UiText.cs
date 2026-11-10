using System.Globalization;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop;

/// <summary>
/// All user-facing text in one class, so wording stays consistent and can be localized later. The class is split by
/// area across UiText.*.cs; this file holds the shell, onboarding and exit wording.
/// </summary>
public static partial class UiText
{
    public const string AppName = "Mindweave";
    public const string StartupOpening = "正在打开数据…";
    public const string OnboardingHeading = "欢迎使用 Mindweave";
    public const string OnboardingBody = "先建一个主题工作区，这个主题下的会话与总结都放在这里。";
    public const string CreateWorkspace = "创建工作区";
    public const string WorkspaceNameWatermark = "例如：火车历史";
    public const string WorkspaceNameLabel = "工作区名称";
    public const string Create = "创建";
    public const string Cancel = "取消";
    public const string NewWorkspace = "新建工作区";
    public const string SectionExplore = "探索";
    public const string SectionNetwork = "总结网络";
    public const string SectionSettings = "设置";
    public const string ConversationsHeading = "会话";
    public const string Back = "后退";

    // Sidebar collapse and resize
    public const string CollapseSidebar = "收起侧栏";
    public const string ExpandSidebar = "展开侧栏";
    public const string SidebarSplitter = "侧栏宽度";

    /// <summary>The toggle's shortcut as the platform writes it (⌘ on macOS).</summary>
    public static string SidebarShortcut { get; } = OperatingSystem.IsMacOS() ? "⌘B" : "Ctrl+B";

    public static string CollapseSidebarTip { get; } = CollapseSidebar + "（" + SidebarShortcut + "）";

    public static string ExpandSidebarTip { get; } = ExpandSidebar + "（" + SidebarShortcut + "）";

    public const string SidebarNeedsAttention = "有会话需要处理";

    public static string ExpandSidebarAttention { get; } = ExpandSidebarTip + " · " + SidebarNeedsAttention;

    public static string ExpandSidebarUnread { get; } = ExpandSidebarTip + " · " + MarkerNewAnswer;

    /// <summary>The closed workspace picker's hint when other workspaces have a new answer (2026-11-10).</summary>
    public static string OtherWorkspacesNews(IEnumerable<string> names) => "其他工作区有新回答：" + string.Join("、", names);

    /// <summary>A workspace in the picker's list with a new answer, for screen readers.</summary>
    public static string WorkspaceWithNews(string name) => name + "，" + MarkerNewAnswer;

    /// <summary>The hint that pops up when an answer ends in a workspace that is not open (2026-11-10).</summary>
    public static string WorkspaceNewsToast(string name, int conversations) =>
        conversations > 1 ? $"{name}：{conversations} 个会话有新回答" : name + " 有新回答";

    /// <summary>What clicking the hint does, for its tooltip and screen readers.</summary>
    public const string WorkspaceNewsToastAction = "点击切换到这个工作区";

    public const string CloseWorkspaceNewsToast = "关闭提示";

    /// <summary>The closed picker's badge: how many conversations in other workspaces have a new answer.</summary>
    public static string NewsBadge(int count) => count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);

    public const string ExploreEmpty = "这个工作区还没有会话。";

    public const string ExitTitle = "仍有任务在生成";
    public const string ExitConfirm = "停止并退出";
    public const string ExitCancel = "继续生成";
    public const string ExitSaving = "正在停止任务并保存……";
    public const string ExitSaveFailedTitle = "部分内容未能保存";
    public const string ExitSaveFailedBody = "草稿或回答的结束状态没能写入，内容仍保留在程序里，写入成功前不会退出。已停止的任务不会自动恢复。";
    public const string ExitRetry = "重试退出";
    public const string ExitReturn = "返回";

    /// <summary>The exit prompt's body, naming user answers and summaries separately ("1 个回答、1 个总结").</summary>
    public static string ExitWithUnfinished(int answers, int summaries)
    {
        var parts = new List<string>(2);
        if (answers > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} 个回答", answers));
        }

        if (summaries > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} 个总结", summaries));
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0}还没有完成。退出会停止它们：已收到的回答会保留并标为“已停止”，总结不留新版本，排队的任务会取消，下次启动也不会重发。",
            string.Join('、', parts));
    }

    public static string WorkspaceNameError(WorkspaceNameError error) => error switch
    {
        Core.Workspaces.WorkspaceNameError.Empty => "名称不能为空。",
        Core.Workspaces.WorkspaceNameError.TooLong => string.Format(CultureInfo.CurrentCulture, "名称最多 {0} 个字符。", WorkspaceName.MaxLength),
        _ => "名称无效。",
    };

    public static string SaveFailed(string detail) => $"保存失败，输入内容已保留：{detail}";
}
