using System.Globalization;
using Mindweave.Application.Recommendations;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;

namespace Mindweave.Desktop.Text;

// AI-suggested links the user confirms (requirements §10.3.4, 2026-11-16): the requirement's words 建议关联、接受、忽略、
// 全部忽略、AI 建议，经你确认.
public static partial class UiText
{
    /// <summary>The network toolbar's menu holding the link actions (显示／隐藏关联, 建议关联), so the toolbar keeps one row.</summary>
    public const string LinksMenu = "关联";

    public const string LinksMenuName = "关联：建议关联、显示或隐藏关联";

    public const string SuggestLinks = "建议关联";

    public const string SuggestLinksTip = "请 AI 按总结的标题与摘录建议至多 5 对可能相关的总结，会发一次请求；只有你接受的才会成为关联";

    /// <summary>Why 建议关联 is unavailable (requirements §10.3.4: at least three summaries).</summary>
    public static string SuggestLinksNeedsSummaries => string.Format(CultureInfo.CurrentCulture, "至少有 {0} 个总结时才能建议关联", LinkSuggestionPrompt.MinSummaries);

    public const string SuggestingLinks = "正在建议关联…";

    /// <summary>The heading of the pending suggestions in the details and the list view.</summary>
    public const string LinkSuggestionsHeading = "关联建议";

    public const string LinkSuggestionsHint = "AI 的建议，接受后才成为关联。";

    /// <summary>The word on every suggestion line (never colour or line style alone).</summary>
    public const string LinkSuggestionTag = "建议";

    /// <summary>The word, with the suggested note when there is one, as written on the line.</summary>
    public static string LinkSuggestionLabel(string? note) => note is null ? LinkSuggestionTag : $"{LinkSuggestionTag} · {note}";

    /// <summary>Legend: the line drawn, in words.</summary>
    public const string LegendLinkSuggestion = "点划线：关联建议（待确认）";

    public const string AcceptLinkSuggestion = "接受";

    public const string IgnoreLinkSuggestion = "忽略";

    public const string IgnoreAllLinkSuggestions = "全部忽略";

    /// <summary>A suggestion as a screen reader hears it.</summary>
    public static string LinkSuggestionName(string a, string b, string? note, string? reason) =>
        $"关联建议：“{a}”与“{b}”" + (note is null ? string.Empty : $"，说明：{note}") + (reason is null ? string.Empty : $"，理由：{reason}");

    public static string AcceptLinkSuggestionName(string a, string b) => $"接受关联建议：“{a}”与“{b}”";

    public static string IgnoreLinkSuggestionName(string a, string b) => $"忽略关联建议：“{a}”与“{b}”";

    public const string SuggestedNoteLabel = "说明：";

    public const string SuggestedNoteNone = "（无）";

    /// <summary>Closes the note editor keeping what was typed for 接受 (nothing is stored until then).</summary>
    public const string SuggestedNoteDone = "完成";

    public static string EditSuggestedNoteName(string a, string b) => $"修改“{a}”与“{b}”关联建议的说明";

    public static string SuggestedNoteEditorName(string a, string b) => $"“{a}”与“{b}”关联建议的说明";

    public static string ShowSuggestedSummaryName(string name) => $"在网络中转到“{name}”";

    public static string IgnoreAllLinkSuggestionsConfirm(int count) =>
        string.Format(CultureInfo.CurrentCulture, "忽略全部 {0} 条关联建议？之后不再建议这些总结对。", count);

    public const string LinkSuggestionAcceptedNotice = "已接受，这两个总结已关联。";

    public static string LinkSuggestionRefused(SummaryLinkRefusal reason) => reason switch
    {
        SummaryLinkRefusal.Duplicate => "这两个总结已经关联过了，这条建议已移除。",
        SummaryLinkRefusal.NoteTooLong => SummaryLinkRefused(reason),
        _ => "这条建议已不存在。",
    };

    /// <summary>The marker of a link that was suggested by AI and accepted (requirements §10.3.4), wherever links are listed.</summary>
    public const string LinkConfirmedMarker = "AI 建议，经你确认";

    /// <summary>Said quietly when a request found nothing new to suggest.</summary>
    public const string LinkSuggestionsNoneNew = "没有找到新的关联建议。";

    public const string RetryLinkSuggestionsName = "重新建议关联";

    /// <summary>Why 建议关联 stored nothing; always the user's own click, so it explains (requirements §10.3 共同规则).</summary>
    public static string LinkSuggestionsProblemText(RecommendationProblem problem, GenerationErrorKind? error) => problem switch
    {
        RecommendationProblem.NotConfigured => "还没有配置模型，连接模型后才能建议关联。",
        RecommendationProblem.Credentials => error is { } k ? $"没能建议关联：{ErrorKind(k)}，请在设置中检查 API 密钥。" : "API 密钥无法使用，请在设置中检查。",
        RecommendationProblem.NoContent => SuggestLinksNeedsSummaries + "。",
        RecommendationProblem.StoragePaused => SuggestionsStoragePaused,
        RecommendationProblem.Unavailable => "正在退出，暂时不能建议关联。",
        RecommendationProblem.Empty => LinkSuggestionsNoneNew,
        RecommendationProblem.Failed => error is { } kind ? $"没能建议关联：{ErrorKind(kind)}。" : "没能建议关联，请重试。",
        _ => "没能建议关联，请重试。",
    };
}
