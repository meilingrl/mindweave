using System.Globalization;
using Mindweave.Application.SummaryLinks;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Content links between two summaries (requirements §10.3.3, 2026-11-16): the requirement's words 关联、关联到…、显示关联、内容关联.
public static partial class UiText
{
    /// <summary>The action on the network's details and on the summary page.</summary>
    public static string LinkTo => T("Link to…", "关联到…");

    public static string LinkToTip => T("Link this summary to another summary in the same workspace, with an optional short note", "把这个总结与同一工作区的另一个总结关联起来，可附一个简短说明");

    /// <summary>The heading of a summary's links in the details and on the summary page.</summary>
    public static string LinksHeading => T("Links", "关联");

    public static string ShowLinks => T("Show links", "显示关联");

    public static string HideLinks => T("Hide links", "隐藏关联");

    public static string HideLinksTip => T("Only changes what the summary network shows; links aren’t deleted", "只影响网络中的显示，不删除关联");


    /// <summary>Legend: the line drawn, in words (requirements §10.3: never colour alone).</summary>
    public static string LegendLink => T("Dashed line: summary link", "虚线：内容关联");

    // Picking the other summary on the network (pick mode), with Esc or 取消 to leave it.
    public static string LinkPickHint(string name) => T($"Click another summary in the summary network to link it to “{name}”. Press Esc to cancel.", $"点选网络中的另一个总结，与“{name}”关联。按 Esc 取消。");

    public static string LinkPickFromList => T("Choose from list…", "从列表选择…");

    public static string LinkPickFromListName => T("Choose the summary to link from a list", "从列表选择要关联的总结");

    public static string LinkPickCancelName => T("Cancel linking", "取消关联");

    public static string LinkPickRefusedSelf => T("A summary can’t be linked to itself. Click another summary.", "不能把总结关联到它自己，请点选另一个总结。");

    public static string LinkPickRefusedRecommended => T("A recommended node isn’t a summary yet, so it can’t be linked. Click a summary.", "推荐节点还不是总结，不能关联。请点选一个总结。");

    public static string LinkPickRefusedNoSummary(string name) => T($"“{name}” has no summary yet, so it can’t be linked. Click a summary.", $"“{name}”还没有总结，不能关联。请点选一个总结。");

    public static string LinkPickRefusedLinked(string name) => T($"“{name}” is already linked to it. Click another summary.", $"“{name}”已经与它关联，请点选另一个总结。");

    // The keyboard picker: a searchable list of the workspace's other summaries.
    public static string LinkPickerHeading(string name) => T($"Link “{name}” to…", $"把“{name}”关联到…");

    public static string LinkPickerSearch => T("Find summary", "查找总结");

    public static string LinkPickerSearchName => T("Find a summary to link", "查找要关联的总结");

    public static string LinkPickerListName => T("Summaries you can link", "可以关联的总结");

    public static string LinkPickerNone => T("This workspace has no other summaries to link yet.", "这个工作区还没有别的总结可以关联。");

    public static string LinkPickerNoMatch => T("No matching summaries found.", "没有找到匹配的总结。");

    public static string AlreadyLinked => T("Linked", "已关联");

    /// <summary>A row of the picker as a screen reader hears it.</summary>
    public static string LinkCandidateName(string name, bool linked) =>
        T(linked ? $"{name}, already linked, can’t be chosen again" : $"Link to “{name}”", linked ? $"{name}，已关联，不能再选" : $"关联到“{name}”");

    // The note: optional, at most 16 characters (requirements §10.3.3).
    public static string LinkNoteWatermark => T("Note (optional, e.g. “contrast”)", "说明（可不填，如“对比”）");

    public static string LinkNoteEditorName(string other) => T($"Note for the link with “{other}”", $"与“{other}”关联的说明");

    public static string LinkNoteTooLong(int length) =>
        T(
            "A note can have at most " + Plural(SummaryLinkNote.MaxLength, "character", "characters") + "; this one has " + length.ToString(EnglishCulture) + ".",
            string.Format(CultureInfo.CurrentCulture, "说明最多 {0} 字，现在是 {1} 字。", SummaryLinkNote.MaxLength, length));

    public static string LinkNoteCount(int length) => string.Format(CultureInfo.CurrentCulture, "{0}/{1}", length, SummaryLinkNote.MaxLength);

    public static string LinkNoteSkip => T("Skip", "跳过");

    public static string LinkCreated(string other) => T($"Linked to “{other}”. You can add a short note.", $"已关联到“{other}”，可以加一个简短说明。");

    public static string EditLinkNote => T("Edit note", "改说明");

    public static string EditLinkNoteName(string other) => T($"Edit the note for the link with “{other}”", $"修改与“{other}”关联的说明");

    public static string DeleteLink => T("Delete link", "删除关联");

    public static string DeleteLinkName(string other) => T($"Delete the link with “{other}”", $"删除与“{other}”的关联");

    public static string DeleteLinkConfirm(string other) => T($"Delete the link with “{other}”? Only this link is deleted; both summaries stay.", $"删除与“{other}”的关联？只删除这条关系，两个总结都保留。");

    public static string JumpToLinkedName(string other) => T($"Go to “{other}”", $"转到“{other}”");

    public static string OpenLinkedName(string other) => T($"Open “{other}”", $"打开“{other}”");

    /// <summary>A link as listed: "关联：<other>，说明：<note>", and "，AI 建议，经你确认" for an accepted suggestion (§10.3.4).</summary>
    public static string LinkItemName(string other, string? note, bool confirmed = false) =>
        T(
            (note is null ? $"Link: {other}" : $"Link: {other}, note: {note}") + (confirmed ? $", {LinkConfirmedMarker}" : string.Empty),
            (note is null ? $"关联：{other}" : $"关联：{other}，说明：{note}") + (confirmed ? $"，{LinkConfirmedMarker}" : string.Empty));

    /// <summary>The list view's line under a summary that has links; an accepted suggestion says so in words.</summary>
    public static string LinksLine(IReadOnlyList<(string Other, string? Note, bool Confirmed)> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        return T(
            "Links: " + string.Join(", ", links.Select(l =>
            {
                var said = string.Join("; ", new[] { l.Note, l.Confirmed ? LinkConfirmedMarker : null }.Where(s => s is not null));
                return said.Length == 0 ? l.Other : $"{l.Other} ({said})";
            })),
            "关联：" + string.Join("、", links.Select(l =>
            {
                var said = string.Join("；", new[] { l.Note, l.Confirmed ? LinkConfirmedMarker : null }.Where(s => s is not null));
                return said.Length == 0 ? l.Other : $"{l.Other}（{said}）";
            })));
    }

    public static string SummaryLinkRefused(SummaryLinkRefusal reason) => reason switch
    {
        SummaryLinkRefusal.Self => LinkPickRefusedSelf,
        SummaryLinkRefusal.Duplicate => T("These two summaries are already linked.", "这两个总结已经关联过了。"),
        SummaryLinkRefusal.OtherWorkspace => T("Only summaries in the same workspace can be linked.", "只能关联同一工作区的总结。"),
        SummaryLinkRefusal.NoteTooLong => T(
            "A note can have at most " + Plural(SummaryLinkNote.MaxLength, "character", "characters") + ".",
            string.Format(CultureInfo.CurrentCulture, "说明最多 {0} 字。", SummaryLinkNote.MaxLength)),
        _ => T("This summary or link no longer exists.", "这个总结或关联已不存在。"),
    };

    public static string LinkStoragePaused => T("Local data can’t be saved right now, so the link wasn’t changed. Try again once saving resumes.", "本地数据暂时无法保存，关联没有改动。恢复保存后可以再试。");
}
