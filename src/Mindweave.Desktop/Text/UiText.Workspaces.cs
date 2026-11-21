using System.Globalization;
using Mindweave.Core.Conversations;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Workspaces in one place (2026-11-10): the sidebar's switcher and its menu, the inline new-workspace form, and the
// 设置 › 工作区 page that lists, switches, renames and deletes any of them.
public static partial class UiText
{
    /// <summary>The switcher button's accessible name: what it is and which workspace is open.</summary>
    public static string WorkspaceSwitcherName(string? current) => string.IsNullOrEmpty(current) ? WorkspaceSection : T($"Workspace: {current}", $"工作区：{current}");

    /// <summary>The switcher's menu, as a screen reader names it.</summary>
    public static string WorkspaceMenuName => T("Workspace list", "工作区列表");

    /// <summary>The current workspace's row in the menu: the check mark, for a screen reader.</summary>
    public static string CurrentWorkspaceHint => T("Current workspace", "当前工作区");

    /// <summary>The menu's and the page's entry to the new-workspace form ("…": a name is asked for next).</summary>
    public static string NewWorkspaceEntry => T("New workspace…", "新建工作区…");

    /// <summary>The menu's entry to 设置 › 工作区 ("…": it opens a page, it does not act at once).</summary>
    public static string ManageWorkspaces => T("Manage workspaces…", "管理工作区…");

    public static string CancelNewWorkspace => T("Cancel new workspace", "取消新建工作区");

    // ── 设置 › 工作区 ────────────────────────────────────────────────────────

    public static string WorkspacesTitle => T("Workspaces", "工作区");

    /// <summary>The settings home's entry to the page.</summary>
    public static string ManageWorkspacesLink => T("Manage workspaces", "管理工作区");

    /// <summary>How many workspaces there are, beside that entry.</summary>
    public static string WorkspacesCount(int count) => T(string.Format(Culture, "{0}", count), string.Format(CultureInfo.CurrentCulture, "{0} 个", count));

    /// <summary>The entry's accessible name, with the count it shows.</summary>
    public static string ManageWorkspacesName(int count) => T($"{ManageWorkspacesLink}, {WorkspacesCount(count)}", $"{ManageWorkspacesLink}，{WorkspacesCount(count)}");

    public static string WorkspacesIntro => T(
        "Each workspace has its own conversations, summaries and summary network. Renaming one doesn’t change its contents; it is backed up before it is deleted.",
        "每个工作区有自己的会话、总结和总结网络。改名不影响其中的内容；删除前会先备份。");

    public static string CurrentWorkspaceTag => T("Current", "当前");

    /// <summary>"10 个会话，包含 3 个归档对话 · 8 个总结" (2026-11-10: say how many are archived); without archived ones, "10 个会话 · 8 个总结".</summary>
    public static string WorkspaceCounts(int conversations, int summaries, int archived = 0) =>
        archived > 0
            ? T(
                string.Format(Culture, "{0}, including {1} archived · {2}", Plural(conversations, "conversation", "conversations"), archived, Plural(summaries, "summary", "summaries")),
                string.Format(CultureInfo.CurrentCulture, "{0} 个会话，包含 {1} 个归档对话 · {2} 个总结", conversations, archived, summaries))
            : T(
                Plural(conversations, "conversation", "conversations") + " · " + Plural(summaries, "summary", "summaries"),
                string.Format(CultureInfo.CurrentCulture, "{0} 个会话 · {1} 个总结", conversations, summaries));

    public static string WorkspaceCountsLoading => T("Counting…", "正在统计…");

    public static string WorkspaceCountsFailed => T("Couldn’t read the counts", "没能读取数量");

    public static string SwitchToWorkspace => T("Switch to this workspace", "切换到此工作区");

    public static string SwitchToWorkspaceName(string name) => T($"Switch to workspace “{name}”", $"切换到工作区“{name}”");

    public static string RenameWorkspaceName(string name) => T($"Rename workspace “{name}”", $"给工作区“{name}”改名");

    public static string DeleteWorkspaceName(string name) => T($"Delete workspace “{name}”…", $"删除工作区“{name}”…");

    /// <summary>A row's accessible name: the workspace, whether it is the open one, and what it holds.</summary>
    public static string WorkspaceRowName(string name, bool current, string? counts) => T(
        string.Join(", ", new[] { name, current ? CurrentWorkspaceHint : null, counts }.Where(p => !string.IsNullOrEmpty(p))),
        string.Join("，", new[] { name, current ? CurrentWorkspaceHint : null, counts }.Where(p => !string.IsNullOrEmpty(p))));

    /// <summary>Renaming was refused because storage is paused, or the write failed: nothing changed.</summary>
    public static string WorkspaceRenameStorageFailed => T(
        "Local data can’t be saved right now, so the name was not changed. Try again later.",
        "本地数据暂时无法保存，名称没有改动。请稍后再试。");

    /// <summary>The workspace was deleted elsewhere while its name was being edited.</summary>
    public static string WorkspaceRenameMissing => T("This workspace no longer exists.", "这个工作区已不存在。");

    // ── Suggested names while renaming (2026-11-11) ────────────────────────────

    /// <summary>The rename row's button: asks the model for names drawn from the workspace's conversations.</summary>
    public static string SuggestNames => T("Suggest names", "建议名称");

    public static string SuggestNamesName(string name) => T($"Suggest names for workspace “{name}” from its conversations", $"根据会话为工作区“{name}”建议名称");

    /// <summary>The button's tooltip and help text: what it does, and that it never renames by itself.</summary>
    public static string SuggestNamesTip => T(
        "The model offers 2–3 names to choose from, based on the titles of this workspace’s conversations and summaries. The titles are sent to the model, which may cost a little. Nothing is renamed automatically.",
        "根据这个工作区的会话与总结的标题，由模型给出 2–3 个名称供选择；会把标题发给模型，可能产生少量费用；不会自动改名。");

    /// <summary>Why the button is unavailable: nothing to name the workspace after.</summary>
    public static string SuggestNamesEmptyTip => T(
        "This workspace has no conversations yet, so no names can be suggested from them.",
        "这个工作区还没有会话，无法根据会话建议名称。");

    /// <summary>Under the rename box (in place of the counts) while the workspace has something to name it after.</summary>
    public static string SuggestNamesHint => T(
        "Suggesting names sends the titles of conversations and summaries to the model, which may cost a little.",
        "建议名称会把会话与总结的标题发给模型，可能产生少量费用");

    public static string SuggestingNames => T("Thinking of names from the conversations…", "正在根据会话想名称…");

    public static string StopSuggesting => T("Stop", "停止");

    public static string StopSuggestingName => T("Stop suggesting names", "停止建议名称");

    /// <summary>Above the candidates: what picking one does, and what it does not.</summary>
    public static string SuggestionsCaption => T(
        "Pick one to fill in the name box; you can still edit it. The name changes only when you save.",
        "选一个填入名称框，仍可修改；保存后才会改名");

    /// <summary>The candidates, as a screen reader names the group.</summary>
    public static string SuggestionsListName => T("Suggested names", "建议的名称");

    public static string SuggestionChipName(string name) => T($"Use the name “{name}” in the name box", $"使用名称“{name}”，填入名称框");

    public static string RetrySuggestName => T("Suggest names again", "重新建议名称");

    public static string SuggestionsNone => T(
        "No usable names came back. You can try again or type your own.",
        "没有得到可用的名称，可以重试或自己输入。");

    public static string SuggestionsEmpty => SuggestNamesEmptyTip;

    public static string SuggestionsNotConfigured => T("No model is set up yet, so names can’t be suggested.", "还没有配置模型，无法建议名称。");

    public static string SuggestionsStoragePaused => T(
        "Local data can’t be saved right now, so AI requests are paused. You can try again once saving works again.",
        "本地数据暂时无法保存，已暂停 AI 请求；恢复保存后可以重试。");

    public static string SuggestionsUnavailable => T(
        "The app is quitting, so suggestions can’t be requested right now.",
        "正在退出，暂时不能请求建议。");

    // ── Ghost names in the rename box (2026-11-11, user decision) ─────────────

    /// <summary>The rename button once names are there: asks for different ones.</summary>
    public static string SuggestOtherNames => T("Suggest others", "换一批");

    public static string SuggestOtherNamesName(string name) => T($"Suggest other names for workspace “{name}”", $"为工作区“{name}”换一批建议名称");

    /// <summary>The 换一批 button's tooltip and help text.</summary>
    public static string SuggestOtherNamesTip => T(
        "The model offers 2–3 new names, based on the titles of conversations and summaries. The titles are sent to the model, which may cost a little. Nothing is renamed automatically.",
        "根据会话与总结的标题重新给出 2–3 个名称；会把标题发给模型，可能产生少量费用；不会自动改名。");

    /// <summary>The rename box's help text while a name is offered after the text (screen readers; it changes as Tab cycles).</summary>
    public static string GhostNameHelpFormat => T("Suggested name: {0}. Press Tab to use it.", "建议名称：{0}，按 Tab 使用");

    /// <summary>Under the rename box (in place of the counts) once names are there.</summary>
    public static string GhostNamesHint => T(
        "Press Tab to fill in a suggested name, and Tab again for another. The name changes only when you save.",
        "按 Tab 填入建议的名称，再按 Tab 换一个；保存后才会改名");

    /// <summary>The small toggle after that line: every name as a chip to click.</summary>
    public static string ShowNameChips => T("More suggestions", "更多建议");

    public static string HideNameChips => T("Hide", "收起");

    public static string ShowNameChipsName => T("Show all suggested names", "显示全部建议名称");

    public static string HideNameChipsName => T("Hide suggested names", "收起建议名称");

    /// <summary>The service failed (or the workspace could not be read: <paramref name="kind"/> null).</summary>
    public static string SuggestionsFailed(GenerationErrorKind? kind) =>
        kind is { } k ? T($"Couldn’t get suggestions: {ErrorKind(k)}.", $"没能得到建议：{ErrorKind(k)}。") : T("Couldn’t get suggestions. Try again.", "没能得到建议，请重试。");
}
