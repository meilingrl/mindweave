using System.Globalization;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// The summary network (S7): layout, the graph and list views, markers and source paths.
public static partial class UiText
{
    /// <summary>The network page and its sidebar entry (requirements §4.2).</summary>
    public static string SummaryNetworkTitle => AppName;
    public static string SummaryNetworkEmpty => T(
        "No conversations yet. Ask a question in “New exploration” and it appears here.",
        "还没有会话。在“新探索”提一个问题就会出现在这里。");
    public static string ShowAll => T("Show all", "显示全部");
    public static string ZoomIn => T("Zoom in", "放大");
    public static string ZoomOut => T("Zoom out", "缩小");
    public static string FindWatermark => T("Find a title", "查找标题");
    public static string ListView => T("List", "列表");
    public static string WheelZooms => T("Zoom with the mouse wheel", "鼠标滚轮直接缩放");
    public static string OpenSummary => T("Open summary", "打开总结");
    public static string SummaryNetworkSelectHint => T(
        "Click for details; double-click to open the conversation or summary.",
        "单击查看详情，双击打开会话或总结。");

    // Summary network page and markers (S7)
    public static string MarkerRead => T("Read", "已读");
    public static string SourcePathHeading => T("Branch path", "来源路径");
    public static string SummaryNetworkLegend => T("Legend", "图例");
    public static string SummaryNetworkLegendHelp => T("Shows what the dots and lines mean; press to keep it open, Esc to close it.", "说明圆点和线的含义；按下保持打开，按 Esc 关闭。");
    public static string SummaryNetworkDetailsWidth => T("Width of the details", "详情栏宽度");
    public static string SummaryNetworkDetailsWidthTip => T("Drag to resize, double-click for the default width", "拖动调整宽度，双击恢复默认");
    public static string CollapseSummaryNetworkDetails => T("Hide details", "收起详情");
    public static string ExpandSummaryNetworkDetails => T("Show details", "展开详情");
    public static string LegendUnread => T("Unread", "未读");
    public static string LegendCandidate => T("Candidate update", "有候选更新");

    /// <summary>Where a summary came from, root first: "火车的发明 → 雨山试验 → 缆索牵引".</summary>
    public static string SourcePath(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return string.Join(" → ", names);
    }

    /// <summary>An unsummarized conversation on a source path: "雨山试验 (unsummarized)" / "雨山试验（未总结）".</summary>
    public static string Unsummarized(string title) => T(title + " (unsummarized)", title + "（未总结）");

    public static string NodeSource(string conversationTitle, bool archived = false) => T(
        "From conversation: " + conversationTitle + (archived ? " (" + SourceArchived + ")" : string.Empty),
        "来源会话：" + conversationTitle + (archived ? "（" + SourceArchived + "）" : string.Empty));

    // The details of a dot.
    public static string DetailQuestion => T("Question", "问题");
    public static string ShowWholeQuestion => T("Show the whole question", "展开");
    public static string HideWholeQuestion => T("Show less", "收起");
    /// <summary>
    /// When a conversation started, in local time, dated as <see cref="DayLabel"/> dates a day: the year only when it is
    /// not this year ("Started Nov 22 01:07", "Started Nov 22, 2025 01:07"). <paramref name="today"/> defaults to the local day.
    /// </summary>
    public static string ConversationStarted(DateTimeOffset at, DateOnly? today = null)
    {
        var local = at.ToLocalTime();
        var when = DayLabel(DateOnly.FromDateTime(local.DateTime), today ?? DateOnly.FromDateTime(DateTime.Now)) + " " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return T("Started " + when, "开始于 " + when);
    }

    // The two views and the legend.
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

    /// <summary>When a conversation started, as a hovered dot says it, in local time: "Nov 20 21:38" / "11月20日 21:38".</summary>
    public static string DotHoverDate(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return T(local.ToString("MMM d HH:mm", EnglishCulture), local.ToString("M'月'd'日' HH:mm", CultureInfo.InvariantCulture));
    }
}
