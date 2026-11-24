using System.Globalization;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Sharing a thinking path (docs/design/sharing.md §6.3): the menu entries and the dialog. The words of the shared page
// itself are in it, in both languages (SharePage.html), because its reader chooses the language.
public static partial class UiText
{
    /// <summary>The entry in a conversation's menu and on a dot; it opens the dialog.</summary>
    public static string ShareEntry => T("Share…", "分享…");

    public static string ShareTip => T("Save this exploration as a page anyone can open", "把这个探索存成一个任何人都能打开的网页文件");

    /// <summary>The dialog's title, and the save dialog's.</summary>
    public static string ShareTitle => T("Share a thinking path", "分享思考路径");

    public static string ShareTitleField => T("Title", "标题");

    public static string ShareDotsName => T("Conversations to share", "要分享的会话");

    public static string ShareDotsHint => T(
        "Unticking a conversation also unticks its branches; ticking a branch also ticks the conversations above it.",
        "取消一个会话会同时取消它的分支；勾选一个分支会同时勾选它上面的会话。");

    /// <summary>A conversation without a title, in the dialog as on the shared page (§6.6, point 1).</summary>
    public static string UntitledConversation => T("Untitled conversation", "未命名会话");

    public static string ShareContentsHeading => T("What the file will hold", "文件里会有什么");

    public static string ShareCounts(int explorations, int conversations, int questions) => T(
        Plural(explorations, "exploration", "explorations") + " · " + Plural(conversations, "conversation", "conversations") + " · " + Plural(questions, "question", "questions"),
        string.Format(CultureInfo.InvariantCulture, "{0} 个探索 · {1} 次会话 · {2} 个问题", explorations, conversations, questions));

    public static string ShareHoldsConversations => T(
        "For each conversation: its title as shown here, when it started, how many questions were asked in it, whether it has a summary, and an internal identity, so that Mindweave can recognise it",
        "每个会话：这里显示的标题、开始时间、提问数量、有没有总结，以及一个供 Mindweave 识别的内部标识");

    public static string ShareHoldsLines(int links) => links == 0
        ? T("The lines: which conversation branched off from which", "连线：哪个会话从哪个会话分出")
        : T(
            "The lines: which conversation branched off from which, and " + Plural(links, "summary link", "summary links") + " without notes",
            string.Format(CultureInfo.InvariantCulture, "连线：哪个会话从哪个会话分出，以及 {0} 条内容关联（不含说明）", links));

    public static string ShareHoldsItself(string version) => T(
        "The title above, the time it is saved, and Mindweave’s version (" + version + ")",
        "上面的标题、保存时间和 Mindweave 的版本（" + version + "）");

    public static string ShareLeavesOut => T(
        "Not in it: your questions, the answers, the summaries’ text, and the workspace’s name.",
        "不包含：你的提问、回答、总结的正文，以及工作区名称。");

    public static string ShareSnapshot => T(
        "A saved file is a snapshot: later changes don’t reach it, and a file you have sent can’t be called back.",
        "存下的文件是当时的快照：之后的改动不会进入它，发出去的文件也无法收回。");

    /// <summary>§9: the share goes up to the last finished answer.</summary>
    public static string ShareAnswering(IReadOnlyList<string> names) => T(
        "An answer is still being generated in " + EnglishNames(names) + ". The share goes up to the last finished answer, so the question being answered isn’t counted.",
        "还有回答正在生成：" + ChineseNames(names) + "。分享只到最后一个已完成的回答，正在回答的问题不计入。");

    public static string ShareSave => T("Save…", "保存…");

    public static string ShareClose => T("Close", "关闭");

    public static string ShareSaving => T("Saving…", "正在保存……");

    public static string ShareSaved(string fileName) => T("Saved to " + fileName + ".", "已保存到 " + fileName + "。");

    /// <summary>§9: deleted while the dialog was open.</summary>
    public static string ShareLeftOut(IReadOnlyList<string> names) => T(
        EnglishNames(names) + (names.Count == 1 ? " was" : " were") + " deleted meanwhile and left out.",
        ChineseNames(names) + "在此期间被删除，没有包含在内。");

    public static string ShareChanged => T(
        "Some conversations changed while the save dialog was open, so nothing was saved. The list above shows them as they are now; save again if that is what you want to share.",
        "选择保存位置时有会话发生了变化，因此没有保存。上面已显示现在的内容；确认后请再次保存。");

    public static string ShareNothingLeft => T("Nothing is left to share, so nothing was saved.", "已没有可以分享的会话，没有保存。");

    public static string ShareReadFailed => T("Couldn’t read the conversations. Close this and try again.", "无法读取会话。请关闭后重试。");

    /// <summary>The file type in the save dialog.</summary>
    public static string HtmlFileType => T("Web page", "网页");

    private static string EnglishNames(IReadOnlyList<string> names) => string.Join(", ", names.Select(n => "“" + n + "”"));

    private static string ChineseNames(IReadOnlyList<string> names) => string.Join("、", names.Select(n => "“" + n + "”"));
}
