using System.Globalization;
using Mindweave.Application.SummaryLinks;

namespace Mindweave.Desktop.Text;

// Content links between two summaries (requirements §10.3.3, 2026-11-16): the requirement's words 关联、关联到…、显示关联、内容关联.
public static partial class UiText
{
    /// <summary>The action on the network's details and on the summary page.</summary>
    public const string LinkTo = "关联到…";

    public const string LinkToTip = "把这个总结与同一工作区的另一个总结关联起来，可附一个简短说明";

    /// <summary>The heading of a summary's links in the details and on the summary page.</summary>
    public const string LinksHeading = "关联";

    public const string ShowLinks = "显示关联";

    public const string HideLinks = "隐藏关联";

    public const string HideLinksTip = "只影响网络中的显示，不删除关联";

    /// <summary>The word on a link line that has no note, so every link line carries a word (never colour alone).</summary>
    public const string LinkTag = "关联";

    /// <summary>Legend: the line drawn, in words (requirements §10.3: never colour alone).</summary>
    public const string LegendLink = "彩色实线：内容关联";

    // Picking the other summary on the network (pick mode), with Esc or 取消 to leave it.
    public static string LinkPickHint(string name) => $"点选网络中的另一个总结，与“{name}”关联。按 Esc 取消。";

    public const string LinkPickFromList = "从列表选择…";

    public const string LinkPickFromListName = "从列表选择要关联的总结";

    public const string LinkPickCancelName = "取消关联";

    public const string LinkPickRefusedSelf = "不能把总结关联到它自己，请点选另一个总结。";

    public const string LinkPickRefusedRecommended = "推荐节点还不是总结，不能关联。请点选一个总结。";

    public static string LinkPickRefusedLinked(string name) => $"“{name}”已经与它关联，请点选另一个总结。";

    // The keyboard picker: a searchable list of the workspace's other summaries.
    public static string LinkPickerHeading(string name) => $"把“{name}”关联到…";

    public const string LinkPickerSearch = "查找总结";

    public const string LinkPickerSearchName = "查找要关联的总结";

    public const string LinkPickerListName = "可以关联的总结";

    public const string LinkPickerNone = "这个工作区还没有别的总结可以关联。";

    public const string LinkPickerNoMatch = "没有找到匹配的总结。";

    public const string AlreadyLinked = "已关联";

    /// <summary>A row of the picker as a screen reader hears it.</summary>
    public static string LinkCandidateName(string name, bool linked) => linked ? $"{name}，已关联，不能再选" : $"关联到“{name}”";

    // The note: optional, at most 16 characters (requirements §10.3.3).
    public const string LinkNoteWatermark = "说明（可不填，如“对比”）";

    public static string LinkNoteEditorName(string other) => $"与“{other}”关联的说明";

    public static string LinkNoteTooLong(int length) =>
        string.Format(CultureInfo.CurrentCulture, "说明最多 {0} 字，现在是 {1} 字。", SummaryLinkNote.MaxLength, length);

    public static string LinkNoteCount(int length) => string.Format(CultureInfo.CurrentCulture, "{0}/{1}", length, SummaryLinkNote.MaxLength);

    public const string LinkNoteSkip = "跳过";

    public static string LinkCreated(string other) => $"已关联到“{other}”，可以加一个简短说明。";

    public const string EditLinkNote = "改说明";

    public static string EditLinkNoteName(string other) => $"修改与“{other}”关联的说明";

    public const string DeleteLink = "删除关联";

    public static string DeleteLinkName(string other) => $"删除与“{other}”的关联";

    public static string DeleteLinkConfirm(string other) => $"删除与“{other}”的关联？只删除这条关系，两个总结都保留。";

    public static string JumpToLinkedName(string other) => $"转到“{other}”";

    public static string OpenLinkedName(string other) => $"打开“{other}”";

    /// <summary>A link as listed: "关联：<other>，说明：<note>", and "，AI 建议，经你确认" for an accepted suggestion (§10.3.4).</summary>
    public static string LinkItemName(string other, string? note, bool confirmed = false) =>
        (note is null ? $"关联：{other}" : $"关联：{other}，说明：{note}") + (confirmed ? $"，{LinkConfirmedMarker}" : string.Empty);

    /// <summary>The list view's line under a summary that has links; an accepted suggestion says so in words.</summary>
    public static string LinksLine(IReadOnlyList<(string Other, string? Note, bool Confirmed)> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        return "关联：" + string.Join("、", links.Select(l =>
        {
            var said = string.Join("；", new[] { l.Note, l.Confirmed ? LinkConfirmedMarker : null }.Where(s => s is not null));
            return said.Length == 0 ? l.Other : $"{l.Other}（{said}）";
        }));
    }

    public static string SummaryLinkRefused(SummaryLinkRefusal reason) => reason switch
    {
        SummaryLinkRefusal.Self => LinkPickRefusedSelf,
        SummaryLinkRefusal.Duplicate => "这两个总结已经关联过了。",
        SummaryLinkRefusal.OtherWorkspace => "只能关联同一工作区的总结。",
        SummaryLinkRefusal.NoteTooLong => string.Format(CultureInfo.CurrentCulture, "说明最多 {0} 字。", SummaryLinkNote.MaxLength),
        _ => "这个总结或关联已不存在。",
    };

    public const string LinkStoragePaused = "本地数据暂时无法保存，关联没有改动。恢复保存后可以再试。";
}
