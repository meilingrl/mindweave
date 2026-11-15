using System.Globalization;
using Mindweave.Core.Conversations;

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

    public const string WorkspacesIntro = "每个工作区有自己的会话、总结和总结网络。改名不影响其中的内容；删除前会先备份。";

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

    // ── Suggested names while renaming (2026-11-11) ────────────────────────────

    /// <summary>The rename row's button: asks the model for names drawn from the workspace's conversations.</summary>
    public const string SuggestNames = "建议名称";

    public static string SuggestNamesName(string name) => $"根据会话为工作区“{name}”建议名称";

    /// <summary>The button's tooltip and help text: what it does, and that it never renames by itself.</summary>
    public const string SuggestNamesTip = "根据这个工作区的会话与总结的标题，由模型给出 2–3 个名称供选择；会把标题发给模型，可能产生少量费用；不会自动改名。";

    /// <summary>Why the button is unavailable: nothing to name the workspace after.</summary>
    public const string SuggestNamesEmptyTip = "这个工作区还没有会话，无法根据会话建议名称。";

    /// <summary>Under the rename box (in place of the counts) while the workspace has something to name it after.</summary>
    public const string SuggestNamesHint = "建议名称会把会话与总结的标题发给模型，可能产生少量费用";

    public const string SuggestingNames = "正在根据会话想名称…";

    public const string StopSuggesting = "停止";

    public const string StopSuggestingName = "停止建议名称";

    /// <summary>Above the candidates: what picking one does, and what it does not.</summary>
    public const string SuggestionsCaption = "选一个填入名称框，仍可修改；保存后才会改名";

    /// <summary>The candidates, as a screen reader names the group.</summary>
    public const string SuggestionsListName = "建议的名称";

    public static string SuggestionChipName(string name) => $"使用名称“{name}”，填入名称框";

    public const string RetrySuggestName = "重新建议名称";

    public const string SuggestionsNone = "没有得到可用的名称，可以重试或自己输入。";

    public const string SuggestionsEmpty = SuggestNamesEmptyTip;

    public const string SuggestionsNotConfigured = "还没有配置模型，无法建议名称。";

    public const string SuggestionsStoragePaused = "本地数据暂时无法保存，已暂停 AI 请求；恢复保存后可以重试。";

    public const string SuggestionsUnavailable = "正在退出，暂时不能请求建议。";

    // ── Ghost names in the rename box (2026-11-11, user decision) ─────────────

    /// <summary>The rename button once names are there: asks for different ones.</summary>
    public const string SuggestOtherNames = "换一批";

    public static string SuggestOtherNamesName(string name) => $"为工作区“{name}”换一批建议名称";

    /// <summary>The 换一批 button's tooltip and help text.</summary>
    public const string SuggestOtherNamesTip = "根据会话与总结的标题重新给出 2–3 个名称；会把标题发给模型，可能产生少量费用；不会自动改名。";

    /// <summary>The rename box's help text while a name is offered after the text (screen readers; it changes as Tab cycles).</summary>
    public const string GhostNameHelpFormat = "建议名称：{0}，按 Tab 使用";

    /// <summary>Under the rename box (in place of the counts) once names are there.</summary>
    public const string GhostNamesHint = "按 Tab 填入建议的名称，再按 Tab 换一个；保存后才会改名";

    /// <summary>The small toggle after that line: every name as a chip to click.</summary>
    public const string ShowNameChips = "更多建议";

    public const string HideNameChips = "收起";

    public const string ShowNameChipsName = "显示全部建议名称";

    public const string HideNameChipsName = "收起建议名称";

    /// <summary>The service failed (or the workspace could not be read: <paramref name="kind"/> null).</summary>
    public static string SuggestionsFailed(GenerationErrorKind? kind) =>
        kind is { } k ? $"没能得到建议：{ErrorKind(k)}。" : "没能得到建议，请重试。";
}
