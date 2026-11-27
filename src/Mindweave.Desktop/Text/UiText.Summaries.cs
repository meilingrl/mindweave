using System.Globalization;
using Mindweave.Application.Export;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Summaries (S4): generating, versions, candidates, editing and reading; exporting one (S6, §8.3).
public static partial class UiText
{
    public static string DraftConflict => T("This edit is based on an older version and can’t be saved as a new version. Copy what you need, then discard the changes.", "这份修改基于较早的版本，不能保存为新版本。请复制需要的内容，再放弃修改。");

    public static string DraftStale => T("This edit was already saved or discarded elsewhere, so changes made here won’t be saved. Copy what you need, then reopen it.", "这份修改已在别处保存或放弃，这里的改动不会写入。请复制需要的内容后重新打开。");

    // Summaries (S4)
    public static string Summarize => T("Summarize this conversation", "总结此会话");
    public static string Resummarize => T("Summarize again", "重新总结");
    public static string SummaryGenerating => T("Generating summary…", "正在生成总结…");
    public static string SummaryStop => T("Stop", "停止");
    public static string ReturnToConversation => T("Continue in the source conversation", "回到来源会话继续");

    /// <summary>The button's own words; it keeps <see cref="ReturnToConversation"/> as its accessible name.</summary>
    public static string ReturnToConversationButton => T("Continue the conversation", "回到来源会话继续");
    public static string MarkRead => T("Mark as read", "标记为已读");
    public static string MarkUnread => T("Mark as unread", "标记为未读");
    public static string MarkerUnread => T("Unread", "未读");
    public static string MarkerCandidate => T("Candidate update", "有候选更新");
    public static string MarkerLater => T("Newer content", "有后续内容");
    public static string CandidateHeading => T("Newly generated (candidate)", "新生成（候选）");
    public static string CandidateNote => T("This is a regenerated candidate version; the current version stays as it is. Adopt it to make it the current version, or discard it to delete it.", "这是重新生成的候选版本，当前版本保持不变。采用后它成为当前版本；丢弃后删除。");
    public static string ViewCandidate => T("View candidate", "查看候选");
    public static string ViewCurrent => T("Back to current version", "回到当前版本");
    public static string Adopt => T("Adopt", "采用");
    public static string Discard => T("Discard", "丢弃");
    public static string Edit => T("Edit", "编辑");
    public static string SaveEdit => T("Save as new version", "保存为新版本");
    public static string DiscardEdit => T("Discard changes", "放弃修改");
    public static string EditDraftSaved => T("Your changes are saved automatically as a draft; they become a version only when you click “Save as new version”.", "修改已自动保存为草稿；点击“保存为新版本”后才成为一个版本。");
    public static string EditTitleWatermark => T("Title", "标题");
    public static string HistoryHeading => T("Version history", "版本历史");
    public static string SetCurrent => T("Set as current summary", "设为当前总结");
    public static string View => T("View", "查看");
    public static string DeleteSelected => T("Delete selected versions", "删除所选版本");
    public static string Confirm => T("Confirm", "确认");
    public static string SummaryMissing => T("This summary no longer exists.", "这个总结已不存在。");
    public static string LaterContentNote => T("The source conversation has newer content since this version, not included in the summary.", "来源会话在这个版本之后还有新的内容，未纳入总结。");
    public static string ReadIsNotMastery => T("“Read” only means you’ve read this version, not that you’ve mastered it.", "已读只表示读过这个版本，不代表已经掌握。");
    public static string EditNote => T("Once saved, it becomes the new current version; the original version stays in version history.", "保存后成为新的当前版本，原版本保留在版本历史中。");
    public static string RenameWatermark => T("Summary name", "总结名称");

    // Export (S6, §8.3)
    public static string ExportMarkdown => T("Export as Markdown…", "导出为 Markdown…");

    /// <summary>The summary page's button; its accessible name and its options' heading say <see cref="ExportMarkdown"/>.</summary>
    public static string ExportMarkdownButton => T("Export…", "导出为 Markdown…");
    public static string ExportOmitSource => T("Leave out the source note", "不附来源说明");
    public static string ExportDraftNote => T("This exports the saved version shown on the page; unsaved edit drafts aren’t included.", "导出的是页面上显示的已保存版本；未保存的编辑草稿不包含在内。");
    public static string ExportCandidateHint => T("A candidate version can’t be exported. Adopt it first.", "候选版本不能导出，请先采用。");
    public static string ExportOptionsHeading => T("Export as Markdown", "导出为 Markdown");
    public static string ExportRun => T("Choose a location and export…", "选择位置并导出…");
    public static string MarkdownFileType => T("Markdown file", "Markdown 文件");

    public static string SummarizeRejected(SummarizeRejection rejection) => rejection switch
    {
        SummarizeRejection.NotConfigured => T("No model is set up yet, so a summary can’t be generated.", "还没有配置模型，无法生成总结。"),
        SummarizeRejection.StorageUnavailable => T("Data can’t be saved right now, so the summary didn’t start.", "数据暂时无法保存，总结没有开始。"),
        SummarizeRejection.NothingToSummarize => T("The conversation has no finished answer yet, so there’s nothing to summarize.", "会话里还没有完成的回答，暂时没有可总结的内容。"),
        SummarizeRejection.AnswerInProgress => T("You can summarize once the answer finishes or is stopped.", "回答完成或停止后才能总结。"),
        SummarizeRejection.SummaryInProgress => T("A summary of this conversation is being generated.", "这个会话的总结正在生成。"),
        SummarizeRejection.HasCandidate => T("There’s already a candidate version. Adopt or discard it on the summary page first.", "已有一个候选版本，请先在总结页采用或丢弃。"),
        SummarizeRejection.TooLongForModel => T("The conversation is too long: even after compressing earlier content it exceeds the model’s context window. You can switch to a model with a larger context window in Settings.", "会话太长，压缩较早内容后也超出模型容量；可以在设置中换用容量更大的模型。"),
        SummarizeRejection.ConversationArchived => T("This conversation is archived. Restore it before summarizing.", "这个会话已归档，请先恢复再总结。"),
        SummarizeRejection.ConversationMissing => ConversationDeleted,
        _ => T("Couldn’t generate the summary.", "无法生成总结。"),
    };

    public static string SummaryRefused(SummaryRefusal refusal) => refusal switch
    {
        SummaryRefusal.HasDraft => T("There are unsaved changes. Save them as a new version or discard them first.", "有未保存的修改。请先保存为新版本或放弃修改。"),
        SummaryRefusal.TaskRunning => T("A summary of this conversation is being generated. You can delete versions once it finishes or is stopped.", "这个会话的总结正在生成，完成或停止后才能删除版本。"),
        SummaryRefusal.HasCandidate => T("There’s still a candidate version. Adopt or discard it before deleting all saved versions.", "还有一个候选版本。删除全部已保存版本前，请先采用或丢弃它。"),
        SummaryRefusal.IsCandidate => T("A candidate version can’t be set as current or deleted as a version. Adopt or discard it.", "候选版本不能设为当前或作为版本删除，请采用或丢弃它。"),
        SummaryRefusal.Empty => T("The title and the body can’t be empty.", "标题和正文都不能为空。"),
        _ => T("The summary has changed. Check the latest content, then try again.", "总结已经变化，请查看最新内容后再操作。"),
    };

    public static string SummaryFailed(SummaryTaskStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            GenerationTaskState.Interrupted => T("The last summary was interrupted by quitting or restarting; nothing was saved.", "上次的总结在退出或重启时中断，没有保存任何内容。"),
            GenerationTaskState.Stopped or Application.Storage.GenerationTaskState.Cancelled => T("The last summary was stopped; nothing was saved.", "上次的总结已停止，没有保存任何内容。"),
            // The service's own text is in the block's details (D2 §2.1).
            _ => T(
                "The last summary failed and nothing was saved: " + (status.Error is { } englishKind ? ErrorKind(englishKind) : "reason unknown"),
                "上次的总结失败，没有保存任何内容：" + (status.Error is { } kind ? ErrorKind(kind) : "原因未知")),
        };
    }

    public static string SummaryEntry(string displayName) => T("Summary: " + displayName, "总结：" + displayName);

    public static string Markers(SummaryMarkers markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var parts = new List<string>();
        if (markers.Unread)
        {
            parts.Add(MarkerUnread);
        }

        if (markers.HasCandidate)
        {
            parts.Add(MarkerCandidate);
        }

        if (markers.HasLaterContent)
        {
            parts.Add(MarkerLater);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The read tag of a version other than the current one: "此版本已读" / "此版本未读".</summary>
    public static string ThisVersionRead(bool read) => T(read ? "This version: read" : "This version: unread", read ? "此版本已读" : "此版本未读");

    /// <summary>The current version's read state in words, for screen readers: "当前版本已读" / "当前版本未读".</summary>
    public static string CurrentVersionRead(bool read) => T(read ? "Current version: read" : "Current version: unread", read ? "当前版本已读" : "当前版本未读");

    /// <summary>
    /// The summary page's tags for the version on screen. On the current version the read tag is the node's own
    /// marker (未读 / 已读, as on the network and in the list); on any other version it says "此版本…".
    /// </summary>
    public static string ShownReadMarker(bool read, bool isCurrent) => isCurrent ? (read ? MarkerRead : MarkerUnread) : ThisVersionRead(read);

    /// <summary>The same tags in one line for screen readers, saying which version the read state belongs to.</summary>
    public static string ShownMarkers(bool read, bool isCurrent, bool hasCandidate, bool hasLaterContent)
    {
        var parts = new List<string> { isCurrent ? CurrentVersionRead(read) : ThisVersionRead(read) };
        if (hasCandidate)
        {
            parts.Add(MarkerCandidate);
        }

        if (hasLaterContent)
        {
            parts.Add(MarkerLater);
        }

        return string.Join(" · ", parts);
    }

    public static string VersionNumber(int seq) => T(string.Format(Culture, "Version {0}", seq), string.Format(CultureInfo.CurrentCulture, "第 {0} 版", seq));

    public static string VersionRole(bool isCurrent, bool isCandidate) => T(isCandidate ? "Candidate" : isCurrent ? "Current" : "Earlier", isCandidate ? "候选" : isCurrent ? "当前" : "历史");

    public static string VersionOrigin(SummaryOrigin origin) => T(origin == SummaryOrigin.Edited ? "Edited" : "Generated", origin == SummaryOrigin.Edited ? "手改" : "生成");

    /// <summary>How far a version covers its conversation and when it was made: "覆盖到第 2 条消息 · 2026年3月14日 09:30".</summary>
    public static string VersionCoverage(SummaryVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return T(
            string.Format(Culture, "Covers up to message {0} · {1}", version.ToSeq + 1, FormatTime(version.CreatedAt)),
            string.Format(CultureInfo.CurrentCulture, "覆盖到第 {0} 条消息 · {1}", version.ToSeq + 1, FormatTime(version.CreatedAt)));
    }

    public static string VersionLabel(SummaryVersion version, bool isCurrent, bool isCandidate)
    {
        ArgumentNullException.ThrowIfNull(version);
        return string.Join(" · ", VersionNumber(version.Seq), VersionRole(isCurrent, isCandidate), VersionOrigin(version.Origin), VersionCoverage(version));
    }

    /// <summary>The heading above the editor: which version the edit starts from.</summary>
    public static string EditingVersion(int seq) => T(string.Format(Culture, "Editing version {0}", seq), string.Format(CultureInfo.CurrentCulture, "编辑第 {0} 版", seq));

    /// <summary>The history's header with the number of versions it lists, the candidate included.</summary>
    public static string HistoryHeadingCount(int count) => T(string.Format(Culture, "Version history ({0})", count), string.Format(CultureInfo.CurrentCulture, "版本历史（{0}）", count));

    public static string AdoptReplaces(SummaryVersion current) => T(
        string.Format(Culture, "Since the candidate was generated, the current version has become version {0} ({1}). Adopting the candidate replaces the current version; version {0} stays in version history.", current.Seq, current.Origin == SummaryOrigin.Edited ? "edited" : "generated"),
        string.Format(CultureInfo.CurrentCulture, "候选生成之后，当前版本已变为第 {0} 版（{1}）。采用候选会替换当前版本；第 {0} 版仍保留在历史中。", current.Seq, current.Origin == SummaryOrigin.Edited ? "手改" : "生成"));

    public static string DiscardCandidateConfirm => T("Discarding the candidate deletes its text and read status. This can’t be undone.", "丢弃候选会删除它的正文和阅读记录，无法恢复。");

    /// <summary>Deleting versions; when the last one goes the summary goes, and its content links with it (§8.2, §10.3.3).</summary>
    public static string DeleteVersionsConfirm(int count, SummaryVersion? nextCurrent, int links = 0) => T(
        nextCurrent is null
            ? string.Format(
                Culture,
                "This deletes {0} and the summary itself{1}. The original conversation and branches stay, and you can summarize it again later. This can’t be undone.",
                count == 1 ? "the only version" : "all " + Plural(count, "version", "versions"),
                links > 0 ? ", along with its " + Plural(links, "summary link", "summary links") : string.Empty)
            : string.Format(Culture, "{0} will be deleted. This can’t be undone. The current summary will be version {1}.", Plural(count, "version", "versions"), nextCurrent.Seq),
        nextCurrent is null
            ? string.Format(
                CultureInfo.CurrentCulture,
                "将删除全部 {0} 个版本，总结本身也会被删除{1}；原会话和分支保留，以后可以重新总结。删除后无法恢复。",
                count,
                links > 0 ? string.Format(CultureInfo.CurrentCulture, "，它的 {0} 条内容关联随之移除", links) : string.Empty)
            : string.Format(CultureInfo.CurrentCulture, "将删除 {0} 个版本，删除后无法恢复。当前总结将是第 {1} 版。", count, nextCurrent.Seq));

    public static string Exported(string fileName) => T("Exported to " + fileName + ".", "已导出到 " + fileName + "。");

    public static string ExportRefused(ExportRefusal refusal) => refusal switch
    {
        ExportRefusal.VersionMissing => T("This version has been deleted and wasn’t exported.", "这个版本已被删除，没有导出。"),
        ExportRefusal.IsCandidate => ExportCandidateHint,
        _ => T("Couldn’t export this version.", "无法导出这个版本。"),
    };

    /// <summary>Exporting stopped for a reason other than writing the file (the save dialog, data closed by exit).</summary>
    public static string ExportFailedUnexpectedly => T("Couldn’t export; the original file is unchanged. Try again.", "导出失败，原文件未改动。请重试。");

    public static string ExportFailed(ExportFailure failure, string detail) => failure switch
    {
        ExportFailure.InUse => T("The file is in use by another program and wasn’t exported. Close the program using it and try again; the original file is unchanged.", "文件正被其他程序使用，没有导出。请关闭占用它的程序后重试；原文件未改动。"),
        ExportFailure.NotWritable => T("Can’t write: the file or folder is read-only, or you don’t have permission to write to it. The original file is unchanged.", "无法写入：文件或文件夹是只读的，或没有写入权限。原文件未改动。"),
        _ => T("Export failed; the original file is unchanged: " + detail, "导出失败，原文件未改动：" + detail),
    };
}
