using Mindweave.Application.Recommendations;
using Mindweave.Core.Conversations;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Recommended nodes on the summary network (requirements §10.3.2, 2026-11-12).
public static partial class UiText
{
    /// <summary>The word on every recommended node, its legend entry and its details: not colour alone.</summary>
    public static string RecommendedTag => T("Recommended", "推荐");

    /// <summary>The longest question drawn beside a node, in characters; the rest is in the details.</summary>
    public const int RecommendedNodeLabelLength = 12;

    /// <summary>A recommended node as a screen reader hears it (the list view, and the details heading).</summary>
    public static string RecommendedNodeName(string question, string basis) =>
        T($"Recommended: {question}, extends {basis}", $"推荐：{question}，延伸自 {basis}");

    public static string RefreshRecommendedNodes => T("Refresh recommendations", "换一批推荐");

    /// <summary>The toolbar button's own words; it keeps <see cref="RefreshRecommendedNodes"/> as its name and its tip.</summary>
    public static string RefreshRecommendedNodesButton => T("Refresh", "换一批推荐");

    public static string RefreshRecommendedNodesTip => T(
        "Recommend new directions from this workspace’s conversation titles and summary excerpts; sends one request",
        "按本工作区的会话标题与总结摘录重新推荐探索方向，会发一次请求");

    public static string HideRecommendedNodes => T("Hide recommendations", "隐藏推荐");

    public static string ShowRecommendedNodes => T("Show recommendations", "显示推荐");

    /// <summary>The toolbar button's own words; its name stays <see cref="HideRecommendedNodes"/>.</summary>
    public static string HideRecommendedNodesButton => T("Hide", "隐藏推荐");

    /// <summary>The toolbar button's own words; its name stays <see cref="ShowRecommendedNodes"/>.</summary>
    public static string ShowRecommendedNodesButton => T("Show", "显示推荐");

    public static string HideRecommendedNodesTip => T(
        "Only hides them and deletes nothing; while hidden, no new recommendations are prepared automatically",
        "只影响显示，不删除推荐；隐藏时不自动准备新的推荐");

    public static string PreparingRecommendedNodes => T("Preparing recommendations…", "正在准备推荐…");

    public static string RefreshingRecommendedNodes => T("Refreshing recommendations…", "正在换一批推荐…");

    /// <summary>A background attempt failed: said quietly, with 重试.</summary>
    public static string RecommendedNodesQuietFailure => T(
        "Couldn’t prepare recommended directions this time.",
        "推荐探索方向暂时没能准备好。");

    public static string RetryRecommendedNodesName => T("Recommend directions again", "重新推荐探索方向");

    // Legend: a dashed ring with the word, and the dotted line to the summary it extends.
    public static string LegendRecommended => T("Recommended (dashed ring)", "推荐（虚线圆圈）");
    public static string LegendRecommendedLink => T("Dotted line: the summary a recommendation extends", "点线：推荐延伸自的总结");

    // Details of a selected recommended node.
    public static string RecommendedNodeHeading => T("Recommended direction", "推荐的探索方向");
    public static string RecommendedReasonHeading => T("Why it’s recommended", "推荐理由");
    public static string RecommendedBasisHeading => T("Extends", "延伸自");
    public static string RecommendedNodeNote => T(
        "This is an AI recommendation, not a summary yet. A conversation is created only when you start exploring.",
        "这是 AI 的推荐，还不是总结；开始探索后才会建立会话。");
    public static string StartExploration => T("Start exploring", "开始探索");
    public static string IgnoreRecommendedNode => T("Ignore", "忽略");
    public static string IgnoreRecommendedNodeName(string question) => T($"Ignore recommendation: {question}", $"忽略推荐：{question}");

    /// <summary>The list view's heading over the recommended nodes.</summary>
    public static string RecommendedNodesListHeading => T("Recommended directions", "推荐的探索方向");

    /// <summary>Why 换一批推荐 made nothing (only a manual request explains; the background stays quiet).</summary>
    public static string RecommendedNodesProblemText(RecommendationProblem problem, GenerationErrorKind? error) => problem switch
    {
        RecommendationProblem.NotConfigured => T(
            "No model is set up yet. Connect a model to get recommended directions.",
            "还没有配置模型，连接模型后才能推荐探索方向。"),
        RecommendationProblem.Credentials => error is { } k
            ? T($"Couldn’t recommend directions: {ErrorKind(k)}. Check the API key in Settings.", $"没能推荐探索方向：{ErrorKind(k)}，请在设置中检查 API 密钥。")
            : T("The API key can’t be used. Check it in Settings.", "API 密钥无法使用，请在设置中检查。"),
        RecommendationProblem.NoContent => T(
            "This workspace has no summaries yet. Directions can be recommended once it has some.",
            "这个工作区还没有总结，有了总结才能推荐探索方向。"),
        RecommendationProblem.StoragePaused => SuggestionsStoragePaused,
        RecommendationProblem.Unavailable => T("Mindweave is quitting, so it can’t recommend right now.", "正在退出，暂时不能推荐。"),
        RecommendationProblem.Empty => T(
            "No usable new directions came back this time. You can try again.",
            "这次没有得到可用的新方向，可以再试一次。"),
        RecommendationProblem.Failed => error is { } kind
            ? T($"Couldn’t recommend directions: {ErrorKind(kind)}.", $"没能推荐探索方向：{ErrorKind(kind)}。")
            : T("Couldn’t recommend directions. Please try again.", "没能推荐探索方向，请重试。"),
        _ => T("Couldn’t recommend directions. Please try again.", "没能推荐探索方向，请重试。"),
    };
}
