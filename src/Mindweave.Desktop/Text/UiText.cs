using System.Globalization;
using Mindweave.Core.Workspaces;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

/// <summary>
/// All user-facing text in one class, so wording stays consistent in both interface languages. The class is split by
/// area across UiText.*.cs; this file holds the shell and exit wording
/// (onboarding has its own, UiText.Onboarding.cs).
/// </summary>
public static partial class UiText
{
    public static string AppName => "Mindweave";
    public static string StartupOpening => T("Opening data…", "正在打开数据…");
    public static string CreateWorkspace => T("Create workspace", "创建工作区");
    public static string WorkspaceNameWatermark => T("For example: History of trains", "例如：火车历史");
    public static string WorkspaceNameLabel => T("Workspace name", "工作区名称");
    public static string Create => T("Create", "创建");
    public static string Cancel => T("Cancel", "取消");
    public static string SectionExplore => T("Explore", "探索");
    public static string SectionSettings => T("Settings", "设置");
    public static string ConversationsHeading => T("Conversations", "会话");
    public static string Back => T("Back", "后退");

    // Sidebar collapse and resize
    public static string CollapseSidebar => T("Collapse sidebar", "收起侧栏");
    public static string ExpandSidebar => T("Expand sidebar", "展开侧栏");
    public static string SidebarSplitter => T("Sidebar width", "侧栏宽度");

    /// <summary>The toggle's shortcut as the platform writes it (⌘ on macOS).</summary>
    public static string SidebarShortcut => OperatingSystem.IsMacOS() ? "⌘B" : "Ctrl+B";

    public static string CollapseSidebarTip =>
        T(CollapseSidebar + " (" + SidebarShortcut + ")", CollapseSidebar + "（" + SidebarShortcut + "）");

    public static string ExpandSidebarTip =>
        T(ExpandSidebar + " (" + SidebarShortcut + ")", ExpandSidebar + "（" + SidebarShortcut + "）");

    public static string SidebarNeedsAttention => T("Some conversations need your attention", "有会话需要处理");

    public static string ExpandSidebarAttention => ExpandSidebarTip + " · " + SidebarNeedsAttention;

    public static string ExpandSidebarUnread => ExpandSidebarTip + " · " + MarkerNewAnswer;

    /// <summary>The closed workspace picker's hint when other workspaces have a new answer (2026-11-10).</summary>
    public static string OtherWorkspacesNews(IEnumerable<string> names)
    {
        var all = names.ToList();
        return T("New answers in other workspaces: " + string.Join(", ", all), "其他工作区有新回答：" + string.Join("、", all));
    }

    /// <summary>A workspace in the picker's list with a new answer, for screen readers.</summary>
    public static string WorkspaceWithNews(string name) => T(name + ", " + MarkerNewAnswer, name + "，" + MarkerNewAnswer);

    /// <summary>The hint that pops up when an answer ends in a workspace that is not open (2026-11-10).</summary>
    public static string WorkspaceNewsToast(string name, int conversations) => T(
        conversations > 1 ? $"{name}: new answers in {Plural(conversations, "conversation", "conversations")}" : name + " has a new answer",
        conversations > 1 ? $"{name}：{conversations} 个会话有新回答" : name + " 有新回答");

    /// <summary>What clicking the hint does, for its tooltip and screen readers.</summary>
    public static string WorkspaceNewsToastAction => T("Click to switch to this workspace", "点击切换到这个工作区");

    public static string CloseWorkspaceNewsToast => T("Dismiss", "关闭提示");

    /// <summary>The longest conversation title a hint shows whole, in characters as the reader sees them.</summary>
    public const int ConversationNewsTitleLength = 16;

    /// <summary>
    /// The hint that pops up when an answer ends in a conversation of the open workspace that is not on screen
    /// (2026-11-10): its title as the sidebar shows it, cut with an ellipsis when long.
    /// </summary>
    public static string ConversationNewsToast(string title) => T(
        "“" + Shortened(title, ConversationNewsTitleLength) + "” has a new answer",
        "「" + Shortened(title, ConversationNewsTitleLength) + "」有新回答");

    /// <summary>What a screen reader hears for that hint: the whole title, then what clicking does.</summary>
    public static string ConversationNewsToastName(string title) => T(
        "“" + title + "” has a new answer. " + ConversationNewsToastAction,
        "「" + title + "」有新回答。" + ConversationNewsToastAction);

    /// <summary>What clicking a conversation's hint does.</summary>
    public static string ConversationNewsToastAction => T("Click to open this conversation", "点击打开这个会话");

    /// <summary>
    /// At most <paramref name="length"/> characters as the reader sees them (an emoji or a combined mark counts once and is
    /// never split): a longer text keeps its first ones and ends with "…".
    /// </summary>
    public static string Shortened(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        var info = new StringInfo(text);
        return info.LengthInTextElements <= length ? text : info.SubstringByTextElements(0, length - 1) + "…";
    }

    /// <summary>The closed picker's badge: how many conversations in other workspaces have a new answer.</summary>
    public static string NewsBadge(int count) => count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);

    public static string ExitTitle => T("Some tasks are still in progress", "仍有任务在生成");
    public static string ExitConfirm => T("Stop and quit", "停止并退出");
    public static string ExitCancel => T("Keep generating", "继续生成");
    public static string ExitSaving => T("Stopping tasks and saving…", "正在停止任务并保存……");
    public static string ExitSaveFailedTitle => T("Some content couldn’t be saved", "部分内容未能保存");
    /// <summary>Added to the save-failed message when summary edits can never be saved as they are (low finding gap-summary-3).</summary>
    public static string UnsaveableSummaryEdits(IReadOnlyList<string> names) => names is null || names.Count == 0
        ? string.Empty
        : T(
            $"\n\nThe edits in these summaries were made on an older version and can’t be saved: {string.Join(", ", names.Select(n => $"“{n}”"))}. Go back and open each one, copy the text you need, then click “{DiscardEdit}”.",
            $"\n\n这些总结里的编辑是在旧版本上改的，无法保存：{string.Join("、", names.Select(n => $"《{n}》"))}。请返回打开它，复制需要的文字后点“{DiscardEdit}”。");

    public static string ExitSaveFailedBody => T(
        "A draft or an answer’s end state couldn’t be written. The content is still kept in the app, and Mindweave won’t quit until it is written. Stopped tasks won’t resume on their own.",
        "草稿或回答的结束状态没能写入，内容仍保留在程序里，写入成功前不会退出。已停止的任务不会自动恢复。");
    public static string ExitRetry => T("Try quitting again", "重试退出");
    public static string ExitReturn => T("Go back", "返回");
    public static string QuitWithoutSaving => T("Quit without saving", "不保存并退出");

    /// <summary>
    /// The failed-save message after a failed retry (errors-9): what quitting without saving loses. Without a save to go
    /// by (stopping the tasks failed), everything that may be unsaved is named.
    /// </summary>
    public static string QuitWithoutSavingLoses(Application.Storage.UnsavedWork? unsaved)
    {
        var all = unsaved is null;
        var lost = new List<string>(3);
        if (all || unsaved!.Drafts)
        {
            lost.Add(T("unsent question drafts and reading positions", "未发送的提问草稿和阅读位置"));
        }

        if (all || unsaved!.SummaryEdits)
        {
            lost.Add(T("summary edits not yet saved", "尚未保存的总结编辑"));
        }

        if (all || unsaved!.Answers)
        {
            lost.Add(T(
                "the last part of stopped answers (each keeps what was saved before; they show as interrupted next time)",
                "已停止回答的最后一段（各自保留之前已保存的部分，下次打开时标为中断）"));
        }

        return T(
            "\n\nIf you quit without saving, this is lost: " + string.Join("; ", lost) + ".",
            "\n\n若不保存并退出，会丢失：" + string.Join("；", lost) + "。");
    }

    /// <summary>The exit prompt's body, naming user answers and summaries separately ("1 answer and 1 summary" / "1 个回答、1 个总结").</summary>
    public static string ExitWithUnfinished(int answers, int summaries)
    {
        var parts = new List<string>(2);
        if (answers > 0)
        {
            parts.Add(T(Plural(answers, "answer", "answers"), string.Format(CultureInfo.CurrentCulture, "{0} 个回答", answers)));
        }

        if (summaries > 0)
        {
            parts.Add(T(Plural(summaries, "summary", "summaries"), string.Format(CultureInfo.CurrentCulture, "{0} 个总结", summaries)));
        }

        return T(
            "Not finished yet: " + string.Join(" and ", parts) + ". Quitting stops them: answers received so far are kept and marked “Stopped”, summaries get no new version, queued tasks are cancelled, and nothing is resent at the next start.",
            string.Format(
                CultureInfo.CurrentCulture,
                "{0}还没有完成。退出会停止它们：已收到的回答会保留并标为“已停止”，总结不留新版本，排队的任务会取消，下次启动也不会重发。",
                string.Join('、', parts)));
    }

    public static string WorkspaceNameError(WorkspaceNameError error) => error switch
    {
        Core.Workspaces.WorkspaceNameError.Empty => T("The name can’t be empty.", "名称不能为空。"),
        Core.Workspaces.WorkspaceNameError.TooLong => T(
            string.Format(EnglishCulture, "The name can be at most {0} characters.", WorkspaceName.MaxLength),
            string.Format(CultureInfo.CurrentCulture, "名称最多 {0} 个字符。", WorkspaceName.MaxLength)),
        _ => T("The name isn’t valid.", "名称无效。"),
    };

    /// <summary>An unexpected failure while saving a typed name; the details go to the log, not the page.</summary>
    public static string SaveFailed => T("Couldn’t save, so nothing changed. Your input is kept; try again.", "保存失败，没有改动。输入内容已保留，请重试。");
}
