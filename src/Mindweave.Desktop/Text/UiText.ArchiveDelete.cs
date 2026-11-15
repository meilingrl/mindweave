using System.Globalization;

namespace Mindweave.Desktop.Text;

// Archiving, restoring and deleting conversations and workspaces (S6, §9.5, D-021; requirements v0.12 §8.2).
public static partial class UiText
{
    /// <summary>Archiving, restoring or deleting could not be written; the transaction left everything as it was.</summary>
    public const string ArchiveStorageFailed = "本地数据暂时无法保存，没有做任何改动。请稍后再试。";

    // Archive and delete (S6, §9.5, D-021)
    public const string Archive = "归档";
    public const string ArchiveInstead = "改为归档";
    public const string Delete = "删除";
    public const string Restore = "恢复";
    public const string RestoreAndContinue = "恢复并继续";
    public const string RestoreAndSummarize = "恢复并总结";
    public const string ArchivedTitle = "已归档";
    public const string ArchivedMarker = "已归档";
    public const string SourceArchived = "来源已归档";
    public const string ArchivedReadOnly = "这个会话已归档，只能阅读。恢复后可以继续提问、创建分支或总结；已有总结照常可用。";
    public const string ArchivedEmpty = "没有已归档的会话。";
    public const string ArchivedNote = "归档的会话连同分支不在侧栏显示；它们的总结仍在总结网络中。";
    public const string DeleteConfirm = "将永久删除这个会话：全部消息、生成记录和未发送的草稿都会一起删除，无法撤销。";
    public static string DeleteConfirmFor(string title) => $"永久删除“{title}”？全部消息、生成记录和未发送的草稿都会一起删除，无法撤销。";
    public const string ConversationDeleted = "这个会话已被删除。";

    /// <summary>The sidebar entry to the archived page, with how many conversations the user archived.</summary>
    public static string ArchivedEntry(int count) => string.Format(CultureInfo.CurrentCulture, "已归档（{0}）", count);

    /// <summary>The read-only note of a conversation hidden because conversations above it are archived.</summary>
    public static string HiddenByAncestors(IReadOnlyList<string> ancestors)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        return "所在的上级会话已归档：" + string.Join(" › ", ancestors) + "。这个会话只能阅读；恢复它会同时恢复这些上级会话。";
    }

    /// <summary>Deep restore (D-021): which archived ancestors come back with it and how many other branches reappear.</summary>
    public static string RestoreConfirm(IReadOnlyList<string> ancestors, int othersReappearing)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        var text = "恢复这个会话会同时恢复它路径上已归档的上级会话：" + string.Join(" › ", ancestors) + "。";
        if (othersReappearing > 0)
        {
            text += string.Format(CultureInfo.CurrentCulture, "另有 {0} 个分支会因此重新出现在侧栏。", othersReappearing);
        }

        return text + "不在这条路径上、单独归档的会话保持归档。";
    }

    public static string ArchiveRefused(IReadOnlyList<string> busy)
    {
        ArgumentNullException.ThrowIfNull(busy);
        return "以下会话还有回答或总结在生成或排队，完成或停止后才能归档：" + string.Join("、", busy) + "。";
    }

    // Deleting a whole workspace (user decision of 2026-11-02).
    public const string DeleteWorkspace = "删除工作区…";
    public const string WorkspaceSection = "工作区";
    public const string WorkspaceDeleting = "正在备份并删除…";
    public const string WorkspaceDeleteBackupFailed = "删除前的备份没有成功，所以没有删除任何内容。请检查磁盘空间与权限后重试。";
    public const string WorkspaceDeleteCountFailed = "没能读取这个工作区的内容，暂时不能删除。请关闭后重试。";

    public static string DeleteWorkspaceTitle(string name) => $"删除工作区“{name}”？";

    public static string DeleteWorkspaceContents(int conversations, int summaries) => string.Format(
        CultureInfo.CurrentCulture, "{0} 个会话（含分支与已归档）、{1} 个总结将被永久删除，应用内无法撤销。删除前的备份会列在“数据与存储”中。", conversations, summaries);

    public static string DeleteWorkspaceTypeName(string name) => $"输入工作区名称“{name}”以确认";

    /// <summary>Each busy conversation with what runs in it: "“甲”（回答）、“乙”（总结）".</summary>
    public static string DeleteWorkspaceBusy(IReadOnlyList<(string Title, bool Answer, bool Summary)> busy)
    {
        ArgumentNullException.ThrowIfNull(busy);
        var items = busy.Select(b => $"“{b.Title}”（{(b.Answer && b.Summary ? "回答、总结" : b.Answer ? "回答" : "总结")}）");
        return "还有回答或总结在生成或排队，完成或停止后才能删除：" + string.Join("、", items) + "。";
    }

    // Multi-select and deleting with a stated scope (requirements v0.12 §8.2; D-023: short, no restating the controls).
    public const string SelectConversations = "选择";
    public const string ClearSelection = "取消选择";
    public const string ConversationDeleting = "正在删除…";
    public const string ConversationDeleteScopeChanged = "要删除的内容有变化，数量已更新，请再确认。";
    public const string ConversationDeleteCountFailed = "没能读取要删除的内容，暂时不能删除。请关闭后重试。";

    public static string CheckedRowName(string title) => $"{title}，已选";

    public static string SelectionCount(int count) => string.Format(CultureInfo.CurrentCulture, "已选 {0}", count);

    public static string ConversationDeleteTitle(string title) => $"删除“{title}”？";

    /// <summary>A selection's title counts the rows the user picked; the sentence below counts what goes with them.</summary>
    public static string ConversationDeleteTitle(int selected) => string.Format(CultureInfo.CurrentCulture, "删除所选 {0} 个会话？", selected);

    /// <summary>
    /// The one sentence a delete confirmation says. With branches or summaries along: every conversation that goes,
    /// how many of them are branches, the summaries (a part that is zero is left out), that it cannot be undone in the
    /// app, and where the backup made first is listed. Without: no count, the title already names what goes.
    /// </summary>
    public static string ConversationDeleteContents(int conversations, int branches, int summaries)
    {
        if (branches == 0 && summaries == 0)
        {
            return "全部消息和未发送的草稿将被永久删除，无法撤销。";
        }

        var parts = new List<string>
        {
            branches > 0
                ? string.Format(CultureInfo.CurrentCulture, "{0} 个会话（含 {1} 个分支）", conversations, branches)
                : string.Format(CultureInfo.CurrentCulture, "{0} 个会话", conversations),
        };
        if (summaries > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} 个总结", summaries));
        }

        return string.Join("、", parts) + "将被永久删除，应用内无法撤销。删除前的备份会列在“数据与存储”中。";
    }
}
