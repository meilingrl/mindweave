using Mindweave.Application.Recommendations;
using Mindweave.Core.Conversations;

namespace Mindweave.Desktop.Text;

// Recommended questions on 新探索 (requirements §10.3.1, 2026-11-12) and the recommendation settings in 设置 › 提示.
public static partial class UiText
{
    /// <summary>The list's heading under the input.</summary>
    public const string RecommendedQuestions = "推荐问题";

    /// <summary>Asks for a new batch (also when there is none yet).</summary>
    public const string RefreshRecommendations = "换一批";

    public const string RefreshRecommendationsName = "换一批推荐问题";

    /// <summary>What 换一批 sends and costs, before anything is sent.</summary>
    public const string RefreshRecommendationsTip = "按本工作区的会话标题与总结摘录重新推荐，会发一次请求";

    /// <summary>The per-item action: removed and remembered.</summary>
    public const string IgnoreRecommendation = "不感兴趣";

    public static string IgnoreRecommendationName(string question) => $"不感兴趣：{question}";

    /// <summary>The input's help text while a recommended question is drawn in it.</summary>
    public const string RecommendedQuestionHelpFormat = "推荐问题：{0}，按 Tab 填入";

    /// <summary>"延伸自：蒸汽机－原理" under a question that extends a summary.</summary>
    public static string ExtendsFrom(string summary) => $"延伸自：{summary}";

    /// <summary>A question's row as a screen reader hears it.</summary>
    public static string RecommendedQuestionName(string question, string? basis) =>
        basis is null ? $"推荐问题：{question}，按 Enter 填入输入框" : $"推荐问题：{question}，{ExtendsFrom(basis)}，按 Enter 填入输入框";

    public const string RecommendedQuestionsListName = "推荐问题列表，用上下方向键选择";

    public const string PreparingRecommendations = "正在准备推荐问题…";

    public const string RefreshingRecommendations = "正在换一批…";

    /// <summary>Nothing recommended yet and nothing running (自动准备 off, or nothing usable came back).</summary>
    public const string NoRecommendationsYet = "点“换一批”，按本工作区的会话与总结推荐几个问题。";

    /// <summary>A background attempt failed: said quietly, with 重试.</summary>
    public const string RecommendationsQuietFailure = "推荐问题暂时没能准备好。";

    public const string RetryRecommendationsName = "重新推荐问题";

    /// <summary>Why 换一批 made nothing (only a manual request explains; the background stays quiet).</summary>
    public static string RecommendationProblemText(RecommendationProblem problem, GenerationErrorKind? error) => problem switch
    {
        RecommendationProblem.NotConfigured => "还没有配置模型，连接模型后才能推荐问题。",
        RecommendationProblem.Credentials => error is { } k ? $"没能推荐问题：{ErrorKind(k)}，请在设置中检查 API 密钥。" : "API 密钥无法使用，请在设置中检查。",
        RecommendationProblem.NoContent => "这个工作区还没有可依据的会话标题或总结。",
        RecommendationProblem.StoragePaused => SuggestionsStoragePaused,
        RecommendationProblem.Unavailable => "正在退出，暂时不能推荐问题。",
        RecommendationProblem.Empty => "这次没有得到可用的新问题，可以再试一次。",
        RecommendationProblem.Failed => error is { } kind ? $"没能推荐问题：{ErrorKind(kind)}。" : "没能推荐问题，请重试。",
        _ => "没能推荐问题，请重试。",
    };

    // Settings (设置 › 提示, beside 自动准备工作区名称建议): requirements §10.3 共同规则 and §9.4.
    public const string RecommendationsLabel = "推荐";
    public const string RecommendQuestionsSetting = "新探索推荐问题";
    public const string RecommendNodesSetting = "网络推荐节点";
    public const string AutoPrepareRecommendations = "自动准备推荐";
    public const string RecommendationsHint = "只把会话标题、总结标题和总结的简短摘录发给模型，不发送完整对话；自动准备会在后台发请求，产生少量调用费用。关闭自动准备后，只在点“换一批”时生成。";
    public const string RecommendationsSaveFailed = "推荐设置未能保存，本次运行中仍然有效。";
}
