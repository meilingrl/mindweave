using System.Globalization;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Archiving, restoring and deleting conversations and workspaces (S6, §9.5, D-021; requirements v0.12 §8.2).
public static partial class UiText
{
    /// <summary>Archiving, restoring or deleting could not be written; the transaction left everything as it was.</summary>
    public static string ArchiveStorageFailed => T(
        "Local data can’t be saved right now, so nothing was changed. Please try again later.",
        "本地数据暂时无法保存，没有做任何改动。请稍后再试。");

    // Archive and delete (S6, §9.5, D-021)
    public static string Archive => T("Archive", "归档");
    public static string ArchiveInstead => T("Archive instead", "改为归档");
    public static string Delete => T("Delete", "删除");
    public static string Restore => T("Restore", "恢复");
    public static string RestoreAndContinue => T("Restore and continue", "恢复并继续");
    public static string RestoreAndSummarize => T("Restore and summarize", "恢复并总结");
    public static string ArchivedTitle => T("Archived", "已归档");
    public static string ArchivedMarker => T("Archived", "已归档");
    public static string SourceArchived => T("Conversation archived", "来源已归档");
    public static string ArchivedReadOnly => T(
        "This conversation is archived and can only be read. Restore it to keep asking questions, create branches or summarize; its existing summaries stay available.",
        "这个会话已归档，只能阅读。恢复后可以继续提问、创建分支或总结；已有总结照常可用。");
    public static string ArchivedEmpty => T("No archived conversations.", "没有已归档的会话。");
    public static string ArchivedNote => T(
        "Archived conversations and their branches are not shown in the sidebar; they and their summaries remain in Mindweave.",
        "归档的会话连同分支不在侧栏显示；它们和它们的总结仍在 Mindweave 中。");
    public static string DeleteConfirm => T(
        "This conversation will be permanently deleted: all its messages, generation records and unsent drafts will be deleted with it. This can’t be undone.",
        "将永久删除这个会话：全部消息、生成记录和未发送的草稿都会一起删除，无法撤销。");
    public static string DeleteConfirmFor(string title) => T(
        $"Permanently delete “{title}”? All its messages, generation records and unsent drafts will be deleted with it. This can’t be undone.",
        $"永久删除“{title}”？全部消息、生成记录和未发送的草稿都会一起删除，无法撤销。");
    public static string ConversationDeleted => T("This conversation has been deleted.", "这个会话已被删除。");

    /// <summary>The sidebar entry to the archived page, with how many conversations the user archived.</summary>
    public static string ArchivedEntry(int count) => T(
        string.Format(Culture, "Archived ({0})", count),
        string.Format(CultureInfo.CurrentCulture, "已归档（{0}）", count));

    /// <summary>The read-only note of a conversation hidden because conversations above it are archived.</summary>
    public static string HiddenByAncestors(IReadOnlyList<string> ancestors)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        return T(
            "Conversations above this one are archived: " + string.Join(" › ", ancestors) + ". This conversation can only be read; restoring it also restores those conversations.",
            "所在的上级会话已归档：" + string.Join(" › ", ancestors) + "。这个会话只能阅读；恢复它会同时恢复这些上级会话。");
    }

    /// <summary>Deep restore (D-021): which archived ancestors come back with it and how many other branches reappear.</summary>
    public static string RestoreConfirm(IReadOnlyList<string> ancestors, int othersReappearing)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        var text = T(
            "Restoring this conversation also restores the archived conversations above it on its path: " + string.Join(" › ", ancestors) + ".",
            "恢复这个会话会同时恢复它路径上已归档的上级会话：" + string.Join(" › ", ancestors) + "。");
        if (othersReappearing > 0)
        {
            text += T(
                " As a result, " + Plural(othersReappearing, "other branch", "other branches") + " will reappear in the sidebar.",
                string.Format(CultureInfo.CurrentCulture, "另有 {0} 个分支会因此重新出现在侧栏。", othersReappearing));
        }

        return text + T(
            " Conversations archived on their own and not on this path stay archived.",
            "不在这条路径上、单独归档的会话保持归档。");
    }

    public static string ArchiveRefused(IReadOnlyList<string> busy)
    {
        ArgumentNullException.ThrowIfNull(busy);
        return T(
            "These conversations still have answers or summaries generating or queued; you can archive them once those finish or are stopped: " + string.Join(", ", busy) + ".",
            "以下会话还有回答或总结在生成或排队，完成或停止后才能归档：" + string.Join("、", busy) + "。");
    }

    // Deleting a whole workspace (user decision of 2026-11-02).
    public static string DeleteWorkspace => T("Delete workspace…", "删除工作区…");
    public static string WorkspaceSection => T("Workspace", "工作区");
    public static string WorkspaceDeleting => T("Backing up and deleting…", "正在备份并删除…");
    public static string WorkspaceDeleteBackupFailed => T(
        "The backup before deleting didn’t succeed, so nothing was deleted. Check the disk space and permissions, then try again.",
        "删除前的备份没有成功，所以没有删除任何内容。请检查磁盘空间与权限后重试。");
    public static string WorkspaceDeleteCountFailed => T(
        "Couldn’t read this workspace’s contents, so it can’t be deleted right now. Close this and try again.",
        "没能读取这个工作区的内容，暂时不能删除。请关闭后重试。");

    public static string DeleteWorkspaceTitle(string name) => T($"Delete workspace “{name}”?", $"删除工作区“{name}”？");

    /// <summary>What goes with a workspace; its content links (requirements §10.3.3, §8.2) are named only when there are any.</summary>
    public static string DeleteWorkspaceContents(int conversations, int summaries, int links = 0, int attachments = 0) => T(
        Plural(conversations, "conversation", "conversations") + " (including branches and archived ones)"
            + (links > 0 || attachments > 0 ? ", " : " and ") + Plural(summaries, "summary", "summaries")
            + (links > 0 ? (attachments > 0 ? ", " : " and ") + Plural(links, "summary link", "summary links") : string.Empty)
            + (attachments > 0 ? " and " + Plural(attachments, "attachment", "attachments") : string.Empty)
            + " will be permanently deleted. This can’t be undone in the app. The backup made before deleting will be listed in “Data and storage”"
            + (attachments > 0 ? " and includes the original attachment files" : string.Empty) + ".",
        string.Format(
            CultureInfo.CurrentCulture,
            "{0} 个会话（含分支与已归档）、{1} 个总结{2}{3}将被永久删除，应用内无法撤销。删除前的备份会列在“数据与存储”中{4}。",
            conversations,
            summaries,
            links > 0 ? string.Format(CultureInfo.CurrentCulture, "、{0} 条内容关联", links) : string.Empty,
            attachments > 0 ? string.Format(CultureInfo.CurrentCulture, "、{0} 个附件", attachments) : string.Empty,
            attachments > 0 ? "，附件原件也在其中" : string.Empty));

    public static string DeleteWorkspaceTypeName(string name) => T($"Type the workspace name “{name}” to confirm", $"输入工作区名称“{name}”以确认");

    /// <summary>Each busy conversation with what runs in it: "“甲”（回答）、“乙”（总结）".</summary>
    public static string DeleteWorkspaceBusy(IReadOnlyList<(string Title, bool Answer, bool Summary)> busy)
    {
        ArgumentNullException.ThrowIfNull(busy);
        var items = busy.Select(b => T(
            $"“{b.Title}” ({(b.Answer && b.Summary ? "answer, summary" : b.Answer ? "answer" : "summary")})",
            $"“{b.Title}”（{(b.Answer && b.Summary ? "回答、总结" : b.Answer ? "回答" : "总结")}）"));
        return T(
            "Answers or summaries are still generating or queued; you can delete once they finish or are stopped: " + string.Join(", ", items) + ".",
            "还有回答或总结在生成或排队，完成或停止后才能删除：" + string.Join("、", items) + "。");
    }

    // Multi-select and deleting with a stated scope (requirements v0.12 §8.2; D-023: short, no restating the controls).
    public static string SelectConversations => T("Select", "选择");
    public static string ClearSelection => T("Clear selection", "取消选择");
    public static string ConversationDeleting => T("Deleting…", "正在删除…");
    public static string ConversationDeleteScopeChanged => T(
        "What will be deleted has changed and the counts are updated. Please confirm again.",
        "要删除的内容有变化，数量已更新，请再确认。");
    public static string ConversationDeleteCountFailed => T(
        "Couldn’t read what will be deleted, so nothing can be deleted right now. Close this and try again.",
        "没能读取要删除的内容，暂时不能删除。请关闭后重试。");

    public static string CheckedRowName(string title) => T($"{title}, selected", $"{title}，已选");

    public static string SelectionCount(int count) => T(
        string.Format(Culture, "{0} selected", count),
        string.Format(CultureInfo.CurrentCulture, "已选 {0}", count));

    public static string ConversationDeleteTitle(string title) => T($"Delete “{title}”?", $"删除“{title}”？");

    /// <summary>A selection's title counts the rows the user picked; the sentence below counts what goes with them.</summary>
    public static string ConversationDeleteTitle(int selected) => T(
        "Delete the " + Plural(selected, "selected conversation", "selected conversations") + "?",
        string.Format(CultureInfo.CurrentCulture, "删除所选 {0} 个会话？", selected));

    /// <summary>
    /// The one sentence a delete confirmation says. With branches or summaries along: every conversation that goes,
    /// how many of them are branches, the summaries (a part that is zero is left out), that it cannot be undone in the
    /// app, and where the backup made first is listed. Without: no count, the title already names what goes. The content
    /// links of those summaries (requirements §8.2, §10.3.3) are named when there are any.
    /// </summary>
    public static string ConversationDeleteContents(int conversations, int branches, int summaries, int links = 0, int attachments = 0)
    {
        if (branches == 0 && summaries == 0 && attachments == 0)
        {
            return T(
                "All messages and unsent drafts will be permanently deleted. This can’t be undone.",
                "全部消息和未发送的草稿将被永久删除，无法撤销。");
        }

        var parts = new List<string>
        {
            branches > 0
                ? T(
                    Plural(conversations, "conversation", "conversations") + " (including " + Plural(branches, "branch", "branches") + ")",
                    string.Format(CultureInfo.CurrentCulture, "{0} 个会话（含 {1} 个分支）", conversations, branches))
                : T(
                    Plural(conversations, "conversation", "conversations"),
                    string.Format(CultureInfo.CurrentCulture, "{0} 个会话", conversations)),
        };
        if (summaries > 0)
        {
            parts.Add(T(Plural(summaries, "summary", "summaries"), string.Format(CultureInfo.CurrentCulture, "{0} 个总结", summaries)));
        }

        if (attachments > 0)
        {
            // Requirements §10.4.3: the confirmation counts the attachments; the backup keeps their originals (Q4).
            parts.Add(T(Plural(attachments, "attachment", "attachments"), string.Format(CultureInfo.CurrentCulture, "{0} 个附件", attachments)));
        }

        var along = links > 0
            ? T(
                ", and " + Plural(links, "summary link", "summary links") + " will be removed with them",
                string.Format(CultureInfo.CurrentCulture, "，{0} 条内容关联随之移除", links))
            : string.Empty;
        var originals = attachments > 0 ? T(" and includes the original attachment files", "，附件原件也在其中") : string.Empty;
        var englishList = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return T(
            englishList + " will be permanently deleted" + along + ". This can’t be undone in the app. The backup made before deleting will be listed in “Data and storage”" + originals + ".",
            string.Join("、", parts) + "将被永久删除" + along + "，应用内无法撤销。删除前的备份会列在“数据与存储”中" + originals + "。");
    }
}
