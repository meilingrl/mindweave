using Mindweave.Application.Recommendations;
using Mindweave.Core.Conversations;

namespace Mindweave.Desktop.Text;

// Recommended nodes on the summary network (requirements §10.3.2, 2026-11-12).
public static partial class UiText
{
    /// <summary>The word on every recommended node, its legend entry and its details: not colour alone.</summary>
    public const string RecommendedTag = "推荐";

    /// <summary>The longest question drawn beside a node, in characters; the rest is in the details.</summary>
    public const int RecommendedNodeLabelLength = 12;

    /// <summary>A recommended node as a screen reader hears it (the list view, and the details heading).</summary>
    public static string RecommendedNodeName(string question, string basis) => $"推荐：{question}，延伸自 {basis}";

    public const string RefreshRecommendedNodes = "换一批推荐";

    public const string RefreshRecommendedNodesTip = "按本工作区的会话标题与总结摘录重新推荐探索方向，会发一次请求";

    public const string HideRecommendedNodes = "隐藏推荐";

    public const string ShowRecommendedNodes = "显示推荐";

    public const string HideRecommendedNodesTip = "只影响显示，不删除推荐；隐藏时不自动准备新的推荐";

    public const string PreparingRecommendedNodes = "正在准备推荐…";

    public const string RefreshingRecommendedNodes = "正在换一批推荐…";

    /// <summary>A background attempt failed: said quietly, with 重试.</summary>
    public const string RecommendedNodesQuietFailure = "推荐探索方向暂时没能准备好。";

    public const string RetryRecommendedNodesName = "重新推荐探索方向";

    // Legend: a dashed ring with the word, and the dotted line to the summary it extends.
    public const string LegendRecommended = "推荐（虚线圆圈）";
    public const string LegendRecommendedLink = "点线：推荐延伸自的总结";

    // Details of a selected recommended node.
    public const string RecommendedNodeHeading = "推荐的探索方向";
    public const string RecommendedReasonHeading = "推荐理由";
    public const string RecommendedBasisHeading = "延伸自";
    public const string RecommendedNodeNote = "这是 AI 的推荐，还不是总结；开始探索后才会建立会话。";
    public const string StartExploration = "开始探索";
    public const string IgnoreRecommendedNode = "忽略";
    public static string IgnoreRecommendedNodeName(string question) => $"忽略推荐：{question}";

    /// <summary>The list view's heading over the recommended nodes.</summary>
    public const string RecommendedNodesListHeading = "推荐的探索方向";

    /// <summary>Why 换一批推荐 made nothing (only a manual request explains; the background stays quiet).</summary>
    public static string RecommendedNodesProblemText(RecommendationProblem problem, GenerationErrorKind? error) => problem switch
    {
        RecommendationProblem.NotConfigured => "还没有配置模型，连接模型后才能推荐探索方向。",
        RecommendationProblem.Credentials => error is { } k ? $"没能推荐探索方向：{ErrorKind(k)}，请在设置中检查 API 密钥。" : "API 密钥无法使用，请在设置中检查。",
        RecommendationProblem.NoContent => "这个工作区还没有总结，有了总结才能推荐探索方向。",
        RecommendationProblem.StoragePaused => SuggestionsStoragePaused,
        RecommendationProblem.Unavailable => "正在退出，暂时不能推荐。",
        RecommendationProblem.Empty => "这次没有得到可用的新方向，可以再试一次。",
        RecommendationProblem.Failed => error is { } kind ? $"没能推荐探索方向：{ErrorKind(kind)}。" : "没能推荐探索方向，请重试。",
        _ => "没能推荐探索方向，请重试。",
    };
}
