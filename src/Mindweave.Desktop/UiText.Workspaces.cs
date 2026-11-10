using System.Globalization;

namespace Mindweave.Desktop;

// Workspaces in one place (2026-11-10): the sidebar's switcher and its menu, the inline new-workspace form, and the
// 设置 › 工作区 page that lists, switches, renames and deletes any of them.
public static partial class UiText
{
    /// <summary>The switcher button's accessible name: what it is and which workspace is open.</summary>
    public static string WorkspaceSwitcherName(string? current) => string.IsNullOrEmpty(current) ? WorkspaceSection : $"工作区：{current}";

    /// <summary>The switcher's menu, as a screen reader names it.</summary>
    public const string WorkspaceMenuName = "工作区列表";

    /// <summary>The current workspace's row in the menu: the check mark, for a screen reader.</summary>
    public const string CurrentWorkspaceHint = "当前工作区";

    /// <summary>The menu's and the page's entry to the new-workspace form ("…": a name is asked for next).</summary>
    public const string NewWorkspaceEntry = "新建工作区…";

    /// <summary>The menu's entry to 设置 › 工作区 ("…": it opens a page, it does not act at once).</summary>
    public const string ManageWorkspaces = "管理工作区…";

    public const string CancelNewWorkspace = "取消新建工作区";

    // ── 设置 › 工作区 ────────────────────────────────────────────────────────

    public const string WorkspacesTitle = "工作区";

    /// <summary>The settings home's entry to the page.</summary>
    public const string ManageWorkspacesLink = "管理工作区";

    /// <summary>How many workspaces there are, beside that entry.</summary>
    public static string WorkspacesCount(int count) => string.Format(CultureInfo.CurrentCulture, "{0} 个", count);

    /// <summary>The entry's accessible name, with the count it shows.</summary>
    public static string ManageWorkspacesName(int count) => $"{ManageWorkspacesLink}，{WorkspacesCount(count)}";

    public const string WorkspacesIntro = "每个工作区有自己的会话、总结和知识网络。改名不影响其中的内容；删除前会先备份。";

    public const string CurrentWorkspaceTag = "当前";

    /// <summary>"10 个会话，包含 3 个归档对话 · 8 个总结" (2026-11-10: say how many are archived); without archived ones, "10 个会话 · 8 个总结".</summary>
    public static string WorkspaceCounts(int conversations, int summaries, int archived = 0) =>
        archived > 0
            ? string.Format(CultureInfo.CurrentCulture, "{0} 个会话，包含 {1} 个归档对话 · {2} 个总结", conversations, archived, summaries)
            : string.Format(CultureInfo.CurrentCulture, "{0} 个会话 · {1} 个总结", conversations, summaries);

    public const string WorkspaceCountsLoading = "正在统计…";

    public const string WorkspaceCountsFailed = "没能读取数量";

    public const string SwitchToWorkspace = "切换到此工作区";

    public static string SwitchToWorkspaceName(string name) => $"切换到工作区“{name}”";

    public static string RenameWorkspaceName(string name) => $"给工作区“{name}”改名";

    public static string DeleteWorkspaceName(string name) => $"删除工作区“{name}”…";

    /// <summary>A row's accessible name: the workspace, whether it is the open one, and what it holds.</summary>
    public static string WorkspaceRowName(string name, bool current, string? counts) =>
        string.Join("，", new[] { name, current ? CurrentWorkspaceHint : null, counts }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Renaming was refused because storage is paused, or the write failed: nothing changed.</summary>
    public const string WorkspaceRenameStorageFailed = "本地数据暂时无法保存，名称没有改动。请稍后再试。";

    /// <summary>The workspace was deleted elsewhere while its name was being edited.</summary>
    public const string WorkspaceRenameMissing = "这个工作区已不存在。";
}
