using System.Globalization;
using Mindweave.Application.Export;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop;

// Summaries (S4): generating, versions, candidates, editing and reading; exporting one (S6, §8.3).
public static partial class UiText
{
    public const string DraftConflict = "这份修改基于较早的版本，不能保存为新版本。请复制需要的内容，再放弃修改。";

    public const string DraftStale = "这份修改已在别处保存或放弃，这里的改动不会写入。请复制需要的内容后重新打开。";

    // Summaries (S4)
    public const string Summarize = "总结此会话";
    public const string Resummarize = "重新总结";
    public const string SummaryGenerating = "正在生成总结…";
    public const string SummaryStop = "停止";
    public const string SummaryRetry = "重试";
    public const string ReturnToConversation = "回到来源会话继续";
    public const string MarkRead = "标记为已读";
    public const string MarkUnread = "标记为未读";
    public const string MarkerUnread = "未读";
    public const string MarkerCandidate = "有候选更新";
    public const string MarkerLater = "有后续内容";
    public const string CandidateHeading = "新生成（候选）";
    public const string CandidateNote = "这是重新生成的候选版本，当前版本保持不变。采用后它成为当前版本；丢弃后删除。";
    public const string ViewCandidate = "查看候选";
    public const string ViewCurrent = "回到当前版本";
    public const string Adopt = "采用";
    public const string Discard = "丢弃";
    public const string Edit = "编辑";
    public const string SaveEdit = "保存为新版本";
    public const string DiscardEdit = "放弃修改";
    public const string EditDraftSaved = "修改已自动保存为草稿；点击“保存为新版本”后才成为一个版本。";
    public const string EditTitleWatermark = "标题";
    public const string HistoryHeading = "版本历史";
    public const string SetCurrent = "设为当前总结";
    public const string View = "查看";
    public const string DeleteSelected = "删除所选版本";
    public const string Confirm = "确认";
    public const string SummaryMissing = "这个总结已不存在。";
    public const string LaterContentNote = "来源会话在这个版本之后还有新的内容，未纳入总结。";
    public const string ReadIsNotMastery = "已读只表示读过这个版本，不代表已经掌握。";
    public const string EditNote = "保存后成为新的当前版本，原版本保留在版本历史中。";
    public const string RenameWatermark = "总结名称";

    // Export (S6, §8.3)
    public const string ExportMarkdown = "导出为 Markdown…";
    public const string ExportOmitSource = "不附来源说明";
    public const string ExportDraftNote = "导出的是页面上显示的已保存版本；未保存的编辑草稿不包含在内。";
    public const string ExportCandidateHint = "候选版本不能导出，请先采用。";
    public const string ExportOptionsHeading = "导出为 Markdown";
    public const string ExportRun = "选择位置并导出…";
    public const string MarkdownFileType = "Markdown 文件";

    public static string SummarizeRejected(SummarizeRejection rejection) => rejection switch
    {
        SummarizeRejection.NotConfigured => "还没有配置模型，无法生成总结。",
        SummarizeRejection.StorageUnavailable => "数据暂时无法保存，总结没有开始。",
        SummarizeRejection.NothingToSummarize => "会话里还没有完成的回答，暂时没有可总结的内容。",
        SummarizeRejection.AnswerInProgress => "回答完成或停止后才能总结。",
        SummarizeRejection.SummaryInProgress => "这个会话的总结正在生成。",
        SummarizeRejection.HasCandidate => "已有一个候选版本，请先在总结页采用或丢弃。",
        SummarizeRejection.TooLongForModel => "会话太长，压缩较早内容后也超出模型容量；可以在设置中换用容量更大的模型。",
        SummarizeRejection.ConversationArchived => "这个会话已归档，请先恢复再总结。",
        SummarizeRejection.ConversationMissing => ConversationDeleted,
        _ => "无法生成总结。",
    };

    public static string SummaryRefused(SummaryRefusal refusal) => refusal switch
    {
        SummaryRefusal.HasDraft => "有未保存的修改。请先保存为新版本或放弃修改。",
        SummaryRefusal.TaskRunning => "这个会话的总结正在生成，完成或停止后才能删除版本。",
        SummaryRefusal.HasCandidate => "还有一个候选版本。删除全部已保存版本前，请先采用或丢弃它。",
        SummaryRefusal.IsCandidate => "候选版本不能设为当前或作为版本删除，请采用或丢弃它。",
        _ => "总结已经变化，请查看最新内容后再操作。",
    };

    public static string SummaryFailed(SummaryTaskStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            GenerationTaskState.Interrupted => "上次的总结在退出或重启时中断，没有保存任何内容。",
            GenerationTaskState.Stopped or Application.Storage.GenerationTaskState.Cancelled => "上次的总结已停止，没有保存任何内容。",
            _ => "上次的总结失败，没有保存任何内容：" + (status.Error is { } kind ? ErrorKind(kind) : "原因未知") + (status.Detail is { Length: > 0 } detail ? "（" + detail + "）" : string.Empty),
        };
    }

    public static string SummaryEntry(string displayName) => "总结：" + displayName;

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
    public static string ThisVersionRead(bool read) => read ? "此版本已读" : "此版本未读";

    /// <summary>The current version's read state in words, for screen readers: "当前版本已读" / "当前版本未读".</summary>
    public static string CurrentVersionRead(bool read) => read ? "当前版本已读" : "当前版本未读";

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

    public static string VersionNumber(int seq) => string.Format(CultureInfo.CurrentCulture, "第 {0} 版", seq);

    public static string VersionRole(bool isCurrent, bool isCandidate) => isCandidate ? "候选" : isCurrent ? "当前" : "历史";

    public static string VersionOrigin(SummaryOrigin origin) => origin == SummaryOrigin.Edited ? "手改" : "生成";

    /// <summary>How far a version covers its conversation and when it was made: "覆盖到第 2 条消息 · 2026年3月14日 09:30".</summary>
    public static string VersionCoverage(SummaryVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return string.Format(CultureInfo.CurrentCulture, "覆盖到第 {0} 条消息 · {1}", version.ToSeq + 1, FormatTime(version.CreatedAt));
    }

    public static string VersionLabel(SummaryVersion version, bool isCurrent, bool isCandidate)
    {
        ArgumentNullException.ThrowIfNull(version);
        return string.Join(" · ", VersionNumber(version.Seq), VersionRole(isCurrent, isCandidate), VersionOrigin(version.Origin), VersionCoverage(version));
    }

    /// <summary>The heading above the editor: which version the edit starts from.</summary>
    public static string EditingVersion(int seq) => string.Format(CultureInfo.CurrentCulture, "编辑第 {0} 版", seq);

    /// <summary>The history's header with the number of versions it lists, the candidate included.</summary>
    public static string HistoryHeadingCount(int count) => string.Format(CultureInfo.CurrentCulture, "版本历史（{0}）", count);

    public static string AdoptReplaces(SummaryVersion current) =>
        string.Format(CultureInfo.CurrentCulture, "候选生成之后，当前版本已变为第 {0} 版（{1}）。采用候选会替换当前版本；第 {0} 版仍保留在历史中。", current.Seq, current.Origin == SummaryOrigin.Edited ? "手改" : "生成");

    public const string DiscardCandidateConfirm = "丢弃候选会删除它的正文和阅读记录，无法恢复。";

    public static string DeleteVersionsConfirm(int count, SummaryVersion? nextCurrent) => nextCurrent is null
        ? string.Format(CultureInfo.CurrentCulture, "将删除全部 {0} 个版本，总结本身也会被删除；原会话和分支保留，以后可以重新总结。删除后无法恢复。", count)
        : string.Format(CultureInfo.CurrentCulture, "将删除 {0} 个版本，删除后无法恢复。当前总结将是第 {1} 版。", count, nextCurrent.Seq);

    public static string Exported(string fileName) => "已导出到 " + fileName + "。";

    public static string ExportRefused(ExportRefusal refusal) => refusal switch
    {
        ExportRefusal.VersionMissing => "这个版本已被删除，没有导出。",
        ExportRefusal.IsCandidate => ExportCandidateHint,
        _ => "无法导出这个版本。",
    };

    public static string ExportFailed(ExportFailure failure, string detail) => failure switch
    {
        ExportFailure.InUse => "文件正被其他程序使用，没有导出。请关闭占用它的程序后重试；原文件未改动。",
        ExportFailure.NotWritable => "无法写入：文件或文件夹是只读的，或没有写入权限。原文件未改动。",
        _ => "导出失败，原文件未改动：" + detail,
    };
}
