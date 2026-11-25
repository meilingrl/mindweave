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

    // What a reader can read, one row each: a label and how much of it (the owner, 2026-11-25: the risky part stands out).
    public static string ShareReadableHeading => T("Anyone who has the file can read", "拿到文件的人可以读到");

    public static string ShareAlsoLabel => T("Also in it", "还包含");

    public static string ShareLeavesOutLabel => T("Not in it", "不包含");

    public static string ShareMessagesLabel => T("Questions and answers", "提问和回答");

    public static string ShareMessagesDetail(int questions, int earlier) => T(
        Plural(questions, "question", "questions") + " in full, with the answers that stand" + (earlier == 0 ? "" : " and " + Plural(earlier, "earlier answer", "earlier answers") + " a branch left from"),
        string.Format(CultureInfo.InvariantCulture, "{0} 个提问的全文和最终的回答", questions)
            + (earlier == 0 ? "" : string.Format(CultureInfo.InvariantCulture, "，以及 {0} 条有分支分出的较早回答", earlier)));

    public static string ShareSummariesLabel => T("Summaries", "总结");

    public static string ShareSummariesDetail(int count) => T(
        Plural(count, "summary", "summaries") + " in full, current version",
        string.Format(CultureInfo.InvariantCulture, "{0} 篇的全文（当前版本）", count));

    /// <summary>S7: the names of the attachments, never what is in them.</summary>
    public static string ShareAttachmentsLabel => T("Attachment names", "附件名称");

    public static string ShareAttachmentsDetail(IReadOnlyList<string> names, int pasted)
    {
        var parts = names.ToList();
        if (pasted > 0)
        {
            parts.Add(T(Plural(pasted, "pasted text", "pasted texts") + " (no name)", string.Format(CultureInfo.InvariantCulture, "{0} 段粘贴的文字（无名称）", pasted)));
        }

        return T(string.Join(", ", parts), string.Join("、", parts));
    }

    public static string ShareWebCitationsLabel => T("Web citations", "网页出处");

    public static string ShareWebCitationsDetail(int count) => T(
        Plural(count, "page title and address", "page titles and addresses"),
        string.Format(CultureInfo.InvariantCulture, "{0} 个网页的标题和网址", count));

    public static string ShareNotesLabel => T("Link notes", "关联说明");

    public static string ShareNotesDetail(int count) => T(
        Plural(count, "summary link’s note", "summary links’ notes"),
        string.Format(CultureInfo.InvariantCulture, "{0} 条内容关联的说明", count));

    public static string ShareTitlesLabel => T("Titles", "标题");

    public static string ShareTitlesDetail(int count) => T(
        Plural(count, "conversation title", "conversation titles") + " as shown above, and the share’s own",
        string.Format(CultureInfo.InvariantCulture, "上面显示的 {0} 个会话标题，以及分享自己的标题", count));

    /// <summary>The rest of what is in the file: nothing anyone wrote.</summary>
    public static string ShareAlsoHolds(int links, string version) => T(
        "When each conversation started, how many questions were asked in it and whether it has a summary; " + ShareLines(links)
            + "; an internal identity for each conversation, so that Mindweave can recognise it; the time it is saved and Mindweave’s version (" + version + ").",
        "每个会话的开始时间、提问数量、有没有总结；" + ShareLines(links) + "；供 Mindweave 识别每个会话的内部标识；保存时间和 Mindweave 的版本（" + version + "）。");

    public static string ShareContentAlsoHolds(int links, int ownCitations, string version) => T(
        "When each conversation started, how many questions were asked in it and whether it has a summary; the time of each message, how often an answer used tools, and whether it is incomplete; " + ShareLines(links)
            + ", and the sentence each branch left from; "
            + (ownCitations == 0 ? "" : Plural(ownCitations, "citation", "citations") + " of your own material, only its kind (a title only for a summary that is itself shared); ")
            + ShareIdentitiesWithContent + "; the time it is saved and Mindweave’s version (" + version + ").",
        "每个会话的开始时间、提问数量、有没有总结；每条消息的时间，回答用了几次工具、是否未完成；" + ShareLines(links) + "，以及每个分支从哪句话分出；"
            + (ownCitations == 0 ? "" : string.Format(CultureInfo.InvariantCulture, "{0} 条引用自己材料的出处，只有种类（同在分享里的总结才有标题）；", ownCitations))
            + ShareIdentitiesWithContent + "；保存时间和 Mindweave 的版本（" + version + "）。");

    /// <summary>What an importing app needs and the page does not show (the owner, 2026-11-25).</summary>
    public static string ShareIdentitiesWithContent => T(
        "an internal identity for each conversation and message, each message’s number in the conversation, and for a summary how far it covers, so that Mindweave can import it",
        "供 Mindweave 导入的每个会话和每条消息的内部标识、消息在会话中的序号，以及总结覆盖到哪里");

    private static string ShareLines(int links) => T(
        "which conversation branched off from which" + (links == 0 ? "" : ", and " + Plural(links, "summary link", "summary links")),
        "哪个会话从哪个会话分出" + (links == 0 ? "" : string.Format(CultureInfo.InvariantCulture, "、{0} 条内容关联", links)));

    public static string ShareLeavesOut => T(
        "Your questions, the answers, the summaries’ text, the notes of summary links, and the workspace’s name.",
        "你的提问、回答、总结的正文、内容关联的说明，以及工作区名称。");

    /// <summary>S6: off unless chosen.</summary>
    public static string ShareIncludeContent => T("Include conversation content", "包含会话内容");

    public static string ShareIncludeContentHint => T(
        "Adds the questions, answers and summaries. Without it, only the structure is shared: no message text leaves.",
        "加入提问、回答和总结。不选时只分享结构，不包含任何对话文字。");

    /// <summary>While what would leave is being read.</summary>
    public static string ShareReading => T("Reading the conversations…", "正在读取会话……");

    public static string ShareContentLeavesOut => T(
        "What is in the attachments, the saved text of web pages and search results, what tools were asked and returned, the model and its service, earlier versions of summaries, and the workspace’s name.",
        "附件的内容、网页和搜索结果的保存文本、工具的参数和结果、模型和服务、总结的其他版本，以及工作区名称。");

    public static string ShareContentAnswering(IReadOnlyList<string> names) => T(
        "An answer is still being generated in " + Names(names) + ". The share goes up to the last finished answer, so that answer isn’t in it, nor its question if it has no other answer.",
        "还有回答正在生成：" + Names(names) + "。分享只到最后一个已完成的回答，正在生成的回答不包含在内；还没有其他回答的提问也不包含。");

    public static string ShareSnapshot => T(
        "A saved file is a snapshot: later changes don’t reach it, and a file you have sent can’t be called back.",
        "存下的文件是当时的快照：之后的改动不会进入它，发出去的文件也无法收回。");

    /// <summary>§9: the share goes up to the last finished answer.</summary>
    public static string ShareAnswering(IReadOnlyList<string> names) => T(
        "An answer is still being generated in " + Names(names) + ". The share goes up to the last finished answer, so the question being answered isn’t counted.",
        "还有回答正在生成：" + Names(names) + "。分享只到最后一个已完成的回答，正在回答的问题不计入。");

    public static string ShareSave => T("Save…", "保存…");

    public static string ShareClose => T("Close", "关闭");

    public static string ShareSaving => T("Saving…", "正在保存……");

    public static string ShareSaved(string fileName) => T("Saved to " + fileName + ".", "已保存到 " + fileName + "。");

    /// <summary>§9: deleted while the dialog was open.</summary>
    public static string ShareLeftOut(IReadOnlyList<string> names) => T(
        Names(names) + (names.Count == 1 ? " was" : " were") + " deleted meanwhile and left out.",
        Names(names) + "在此期间被删除，没有包含在内。");

    public static string ShareChanged => T(
        "Some conversations changed while the save dialog was open, so nothing was saved. The list above shows them as they are now; save again if that is what you want to share.",
        "选择保存位置时有会话发生了变化，因此没有保存。上面已显示现在的内容；确认后请再次保存。");

    public static string ShareNothingLeft => T("Nothing is left to share, so nothing was saved.", "已没有可以分享的会话，没有保存。");

    public static string ShareReadFailed => T("Couldn’t read the conversations. Close this and try again.", "无法读取会话。请关闭后重试。");

    /// <summary>The file type in the save dialog.</summary>
    public static string HtmlFileType => T("Web page", "网页");

    private static string Names(IReadOnlyList<string> names)
    {
        var quoted = names.Select(n => "“" + n + "”");
        return T(string.Join(", ", quoted), string.Join("、", quoted));
    }
}
