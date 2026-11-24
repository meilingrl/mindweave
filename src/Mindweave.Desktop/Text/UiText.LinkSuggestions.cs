using System.Globalization;
using Mindweave.Application.Recommendations;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// AI-suggested links the user confirms (requirements §10.3.4, 2026-11-16): the requirement's words 建议关联、接受、忽略、
// 全部忽略、AI 建议，经你确认.
public static partial class UiText
{
    /// <summary>The network toolbar's menu holding the link actions (显示／隐藏关联, 建议关联), so the toolbar keeps one row.</summary>
    public static string LinksMenu => T("Links", "关联");

    public static string LinksMenuName => T("Links: suggest links, show or hide links", "关联：建议关联、显示或隐藏关联");

    public static string SuggestLinks => T("Suggest links", "建议关联");

    public static string SuggestLinksTip => T("Ask AI to suggest up to 5 pairs of possibly related summaries from their titles and excerpts. This sends one request; only the ones you accept become links.", "请 AI 按总结的标题与摘录建议至多 5 对可能相关的总结，会发一次请求；只有你接受的才会成为关联");

    /// <summary>Why 建议关联 is unavailable (requirements §10.3.4: at least three summaries).</summary>
    public static string SuggestLinksNeedsSummaries =>
        T(
            "You need at least " + Plural(LinkSuggestionPrompt.MinSummaries, "summary", "summaries") + " to suggest links",
            string.Format(CultureInfo.CurrentCulture, "至少有 {0} 个总结时才能建议关联", LinkSuggestionPrompt.MinSummaries));

    public static string SuggestingLinks => T("Suggesting links…", "正在建议关联…");

    /// <summary>The heading of the pending suggestions in the details and the list view.</summary>
    public static string LinkSuggestionsHeading => T("Link suggestions", "关联建议");

    public static string LinkSuggestionsHint => T("Suggested by AI. They become links only when you accept them.", "AI 的建议，接受后才成为关联。");

    /// <summary>The word on every suggestion line (never colour or line style alone).</summary>
    public static string LinkSuggestionTag => T("Suggested", "建议");

    /// <summary>The word, with the suggested note when there is one, as written on the line.</summary>
    public static string LinkSuggestionLabel(string? note) => note is null ? LinkSuggestionTag : $"{LinkSuggestionTag} · {note}";

    /// <summary>Legend: the line drawn, in words.</summary>
    public static string LegendLinkSuggestion => T("Dash-dot line: link suggestion (awaiting confirmation)", "点划线：关联建议（待确认）");

    public static string AcceptLinkSuggestion => T("Accept", "接受");

    public static string IgnoreLinkSuggestion => T("Ignore", "忽略");

    public static string IgnoreAllLinkSuggestions => T("Ignore all", "全部忽略");

    /// <summary>A suggestion as a screen reader hears it.</summary>
    public static string LinkSuggestionName(string a, string b, string? note, string? reason) =>
        T(
            $"Link suggestion: “{a}” and “{b}”" + (note is null ? string.Empty : $", note: {note}") + (reason is null ? string.Empty : $", reason: {reason}"),
            $"关联建议：“{a}”与“{b}”" + (note is null ? string.Empty : $"，说明：{note}") + (reason is null ? string.Empty : $"，理由：{reason}"));

    public static string AcceptLinkSuggestionName(string a, string b) => T($"Accept link suggestion: “{a}” and “{b}”", $"接受关联建议：“{a}”与“{b}”");

    public static string IgnoreLinkSuggestionName(string a, string b) => T($"Ignore link suggestion: “{a}” and “{b}”", $"忽略关联建议：“{a}”与“{b}”");

    public static string SuggestedNoteLabel => T("Note:", "说明：");

    public static string SuggestedNoteNone => T("(none)", "（无）");

    /// <summary>Closes the note editor keeping what was typed for 接受 (nothing is stored until then).</summary>
    public static string SuggestedNoteDone => T("Done", "完成");

    public static string EditSuggestedNoteName(string a, string b) => T($"Edit the note of the link suggestion for “{a}” and “{b}”", $"修改“{a}”与“{b}”关联建议的说明");

    public static string SuggestedNoteEditorName(string a, string b) => T($"Note of the link suggestion for “{a}” and “{b}”", $"“{a}”与“{b}”关联建议的说明");

    public static string ShowSuggestedSummaryName(string name) => T($"Go to “{name}” in Mindweave", $"在 Mindweave 中转到“{name}”");

    public static string IgnoreAllLinkSuggestionsConfirm(int count) =>
        T(
            count == 1
                ? "Ignore " + Plural(count, "link suggestion", "link suggestions") + "? This pair of summaries won’t be suggested again."
                : "Ignore all " + Plural(count, "link suggestion", "link suggestions") + "? These pairs of summaries won’t be suggested again.",
            string.Format(CultureInfo.CurrentCulture, "忽略全部 {0} 条关联建议？之后不再建议这些总结对。", count));

    public static string LinkSuggestionAcceptedNotice => T("Accepted. The two summaries are now linked.", "已接受，这两个总结已关联。");

    public static string LinkSuggestionRefused(SummaryLinkRefusal reason) => reason switch
    {
        SummaryLinkRefusal.Duplicate => T("These two summaries are already linked. The suggestion has been removed.", "这两个总结已经关联过了，这条建议已移除。"),
        SummaryLinkRefusal.NoteTooLong => SummaryLinkRefused(reason),
        _ => T("This suggestion no longer exists.", "这条建议已不存在。"),
    };

    /// <summary>The marker of a link that was suggested by AI and accepted (requirements §10.3.4), wherever links are listed.</summary>
    public static string LinkConfirmedMarker => T("Suggested by AI, confirmed by you", "AI 建议，经你确认");

    /// <summary>Said quietly when a request found nothing new to suggest.</summary>
    public static string LinkSuggestionsNoneNew => T("No new link suggestions found.", "没有找到新的关联建议。");

    public static string RetryLinkSuggestionsName => T("Suggest links again", "重新建议关联");

    /// <summary>Why 建议关联 stored nothing; always the user's own click, so it explains (requirements §10.3 共同规则).</summary>
    public static string LinkSuggestionsProblemText(RecommendationProblem problem, GenerationErrorKind? error) => problem switch
    {
        RecommendationProblem.NotConfigured => T("No model is set up yet. Connect a model to suggest links.", "还没有配置模型，连接模型后才能建议关联。"),
        RecommendationProblem.Credentials => error is { } k
            ? T($"Couldn’t suggest links: {ErrorKind(k)}. Check the API key in Settings.", $"没能建议关联：{ErrorKind(k)}，请在设置中检查 API 密钥。")
            : T("The API key can’t be used. Check it in Settings.", "API 密钥无法使用，请在设置中检查。"),
        RecommendationProblem.NoContent => T(SuggestLinksNeedsSummaries + ".", SuggestLinksNeedsSummaries + "。"),
        RecommendationProblem.StoragePaused => SuggestionsStoragePaused,
        RecommendationProblem.Unavailable => T("Mindweave is quitting, so links can’t be suggested right now.","正在退出，暂时不能建议关联。"),
        RecommendationProblem.Empty => LinkSuggestionsNoneNew,
        RecommendationProblem.Failed => error is { } kind
            ? T($"Couldn’t suggest links: {ErrorKind(kind)}.", $"没能建议关联：{ErrorKind(kind)}。")
            : T("Couldn’t suggest links. Try again.", "没能建议关联，请重试。"),
        _ => T("Couldn’t suggest links. Try again.", "没能建议关联，请重试。"),
    };
}
