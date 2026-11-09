using System.Globalization;

namespace Mindweave.Desktop;

// The summary network (S7): layout, the graph and list views, markers and source paths.
public static partial class UiText
{
    public const string NetworkTitle = "总结网络";
    public const string NetworkEmpty = "还没有总结。在会话中点“总结此会话”即可生成。";
    public const string LayoutUnsavedTitle = "布局还没有保存";
    public const string LayoutUnsavedBody = "不保存不会丢失会话或总结，下次按上次保存的位置显示。";
    public const string LayoutSaveAndContinue = "保存并继续";
    public const string LayoutDontSave = "不保存";
    public const string LayoutSaveFailed = "布局保存失败，改动仍保留在当前窗口。";

    public const string SaveLayout = "保存布局";
    public const string ArrangeLayout = "整理布局";
    public const string ShowAll = "显示全部";
    public const string ZoomIn = "放大";
    public const string ZoomOut = "缩小";
    public const string FindWatermark = "查找标题";
    public const string ListView = "列表";
    public const string GraphView = "网络图";
    public const string WheelZooms = "鼠标滚轮直接缩放";
    public const string OpenSummary = "打开总结";
    public const string NetworkSelectHint = "单击查看详情，双击打开总结。";

    // Network page and markers (S7)
    public const string SaveLayoutUnsaved = "保存布局（未保存）";
    public const string LayoutSaved = "布局已保存";
    public const string MarkerRead = "已读";
    public const string SourcePathHeading = "来源路径";
    public const string Exploration = "探索";
    public const string LegendUnread = "未读";
    public const string LegendRead = "已读";
    public const string LegendCandidate = "↻ 有候选更新";
    public const string LegendIndirect = "虚线：经过未总结的会话";

    public static string ViaUnsummarized(IReadOnlyList<string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return string.Format(CultureInfo.CurrentCulture, "经 {0} 个未总结会话：{1}", titles.Count, string.Join(" › ", titles));
    }

    /// <summary>The caption above an exploration on the network: "探索 · root conversation".</summary>
    public static string ExplorationCaption(string? rootTitle) =>
        string.IsNullOrWhiteSpace(rootTitle) ? Exploration : Exploration + " · " + rootTitle;

    /// <summary>Where a summary came from, root first: "火车的发明 → 雨山试验 → 缆索牵引".</summary>
    public static string SourcePath(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return string.Join(" → ", names);
    }

    /// <summary>An unsummarized conversation on a source path: "雨山试验（未总结）".</summary>
    public static string Unsummarized(string title) => title + "（未总结）";

    public static string ViaCount(int count) => string.Format(CultureInfo.CurrentCulture, "经 {0} 个未总结会话", count);

    public static string NodeSource(string conversationTitle, bool archived = false) =>
        "来源会话：" + conversationTitle + (archived ? "（" + SourceArchived + "）" : string.Empty);

    public static string NodeCreated(DateTimeOffset at) => "生成于 " + FormatTime(at);
}
