using System.Globalization;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// The summary network (S7): layout, the graph and list views, markers and source paths.
public static partial class UiText
{
    public static string SummaryNetworkTitle => T("Summary network", "总结网络");
    public static string SummaryNetworkEmpty => T(
        "No summaries yet. Click “Summarize this conversation” in a conversation to create one.",
        "还没有总结。在会话中点“总结此会话”即可生成。");
    public static string LayoutUnsavedTitle => T("Layout not saved", "布局还没有保存");
    public static string LayoutUnsavedBody => T(
        "If you don’t save, no conversations or summaries are lost. Next time, nodes appear where you last saved them.",
        "不保存不会丢失会话或总结，下次按上次保存的位置显示。");
    public static string LayoutSaveAndContinue => T("Save and continue", "保存并继续");
    public static string LayoutDontSave => T("Don’t save", "不保存");
    public static string LayoutSaveFailed => T(
        "Couldn’t save the layout. Your changes are still in this window.",
        "布局保存失败，改动仍保留在当前窗口。");

    public static string SaveLayout => T("Save layout", "保存布局");
    public static string ArrangeLayout => T("Arrange layout", "整理布局");

    /// <summary>The toolbar button's own words; its name stays <see cref="ArrangeLayout"/>.</summary>
    public static string ArrangeLayoutButton => T("Arrange", "整理布局");
    public static string ShowAll => T("Show all", "显示全部");
    public static string ZoomIn => T("Zoom in", "放大");
    public static string ZoomOut => T("Zoom out", "缩小");
    public static string FindWatermark => T("Find a title", "查找标题");
    public static string ListView => T("List", "列表");
    public static string GraphView => T("Graph", "网络图");
    public static string WheelZooms => T("Zoom with the mouse wheel", "鼠标滚轮直接缩放");
    public static string OpenSummary => T("Open summary", "打开总结");
    public static string SummaryNetworkSelectHint => T(
        "Click to see details, double-click to open the summary.",
        "单击查看详情，双击打开总结。");

    // Summary network page and markers (S7)
    public static string SaveLayoutUnsaved => T("Save layout (unsaved)", "保存布局（未保存）");
    public static string LayoutSaved => T("Layout saved", "布局已保存");
    public static string MarkerRead => T("Read", "已读");
    public static string SourcePathHeading => T("Branch path", "来源路径");
    public static string Exploration => T("Exploration", "探索");
    public static string LegendUnread => T("Unread", "未读");
    public static string LegendRead => T("Read", "已读");
    public static string LegendCandidate => T("↻ Candidate update", "↻ 有候选更新");
    public static string LegendIndirect => T("Dashed line: through unsummarized conversations", "虚线：经过未总结的会话");

    public static string ViaUnsummarized(IReadOnlyList<string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return T(
            "Through " + Plural(titles.Count, "unsummarized conversation", "unsummarized conversations") + ": " + string.Join(" › ", titles),
            string.Format(CultureInfo.CurrentCulture, "经 {0} 个未总结会话：{1}", titles.Count, string.Join(" › ", titles)));
    }

    /// <summary>The caption above an exploration on the network: "Exploration · root conversation".</summary>
    public static string ExplorationCaption(string? rootTitle) =>
        string.IsNullOrWhiteSpace(rootTitle) ? Exploration : Exploration + " · " + rootTitle;

    /// <summary>Where a summary came from, root first: "火车的发明 → 雨山试验 → 缆索牵引".</summary>
    public static string SourcePath(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return string.Join(" → ", names);
    }

    /// <summary>An unsummarized conversation on a source path: "雨山试验 (unsummarized)" / "雨山试验（未总结）".</summary>
    public static string Unsummarized(string title) => T(title + " (unsummarized)", title + "（未总结）");

    public static string ViaCount(int count) => T(
        "Through " + Plural(count, "unsummarized conversation", "unsummarized conversations"),
        string.Format(CultureInfo.CurrentCulture, "经 {0} 个未总结会话", count));

    public static string NodeSource(string conversationTitle, bool archived = false) => T(
        "From conversation: " + conversationTitle + (archived ? " (" + SourceArchived + ")" : string.Empty),
        "来源会话：" + conversationTitle + (archived ? "（" + SourceArchived + "）" : string.Empty));

    public static string NodeCreated(DateTimeOffset at) => T("Created " + FormatTime(at), "生成于 " + FormatTime(at));

    // The session network's details (gated).
    public static string DetailQuestion => T("Question", "问题");
    public static string ShowWholeQuestion => T("Show all", "展开");
    public static string HideWholeQuestion => T("Show less", "收起");
    public static string SessionNetworkHint => T(
        "Click for details; double-click to open the conversation or summary.",
        "单击查看详情，双击打开会话或总结。");
    public static string SessionNetworkEmpty => T(
        "No conversations yet. Ask a question in “New exploration” and it appears here.",
        "还没有会话。在“新探索”提一个问题就会出现在这里。");

    /// <summary>
    /// When a conversation started, in local time, dated as <see cref="DayLabel"/> dates a day: the year only when it is
    /// not this year ("Started Nov 22 01:07", "Started Nov 22, 2025 01:07"). <paramref name="today"/> defaults to the local day.
    /// </summary>
    public static string SessionStarted(DateTimeOffset at, DateOnly? today = null)
    {
        var local = at.ToLocalTime();
        var when = DayLabel(DateOnly.FromDateTime(local.DateTime), today ?? DateOnly.FromDateTime(DateTime.Now)) + " " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return T("Started " + when, "开始于 " + when);
    }

    // The session network (behind its gate): every conversation a dot.
    public static string SessionNetworkTitle => AppName;
    public static string ViewSpatial => T("Spatial", "空间");
    public static string ViewTimeline => T("Timeline", "时间线");
    public static string LegendDotSize => T("Dot size: how much was asked, not how well it is known", "圆点大小：问了多少，不代表掌握程度");
    public static string LegendSummaryRing => T("Outer ring: has a summary", "外圈：有总结");
    public static string LegendTime => T("Dotted arrow: order in time", "虚线箭头：时间先后");
    public static string LegendBranch => T("Solid line: branch", "实线：分支");

    /// <summary>
    /// The label over the first exploration of a local day: "Nov 20" / "11月20日", with the year ("Nov 20, 2025" /
    /// "2025年11月20日") when not this year. English month names whatever the system's culture.
    /// </summary>
    public static string DayLabel(DateOnly day, DateOnly today) => day.Year == today.Year
        ? T(day.ToString("MMM d", EnglishCulture), day.ToString("M'月'd'日'", CultureInfo.InvariantCulture))
        : T(day.ToString("MMM d, yyyy", EnglishCulture), day.ToString("yyyy'年'M'月'd'日'", CultureInfo.InvariantCulture));

    /// <summary>A hovered dot's label with when its conversation started, in local time: "蒸汽机 · Nov 20 21:38" / "蒸汽机 · 11月20日 21:38".</summary>
    public static string DotHoverLabel(string label, DateTimeOffset at) => label + " · " + DotHoverDate(at);

    /// <summary>When a session started, as a hovered dot says it, in local time: "Nov 20 21:38" / "11月20日 21:38".</summary>
    public static string DotHoverDate(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return T(local.ToString("MMM d HH:mm", EnglishCulture), local.ToString("M'月'd'日' HH:mm", CultureInfo.InvariantCulture));
    }
}
