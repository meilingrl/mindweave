using Mindweave.Application.Recommendations;
using Mindweave.Core.Conversations;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Recommended questions on 新探索 (requirements §10.3.1, 2026-11-12) and the recommendation settings in 设置 › 提示.
public static partial class UiText
{
    /// <summary>The list's heading under the input.</summary>
    public static string RecommendedQuestions => T("Recommended questions", "推荐问题");

    /// <summary>Asks for a new batch (also when there is none yet).</summary>
    public static string RefreshRecommendations => T("Refresh", "换一批");

    public static string RefreshRecommendationsName => T("Refresh recommended questions", "换一批推荐问题");

    /// <summary>What 换一批 sends and costs, before anything is sent.</summary>
    public static string RefreshRecommendationsTip => T(
        "Recommend new questions from this workspace’s conversation titles and summary excerpts; sends one request",
        "按本工作区的会话标题与总结摘录重新推荐，会发一次请求");

    /// <summary>The per-item action: removed and remembered.</summary>
    public static string IgnoreRecommendation => T("Not interested", "不感兴趣");

    public static string IgnoreRecommendationName(string question) => T($"Not interested: {question}", $"不感兴趣：{question}");

    /// <summary>The input's help text while a recommended question is drawn in it.</summary>
    public static string RecommendedQuestionHelpFormat => T("Recommended question: {0}, press Tab to use it", "推荐问题：{0}，按 Tab 填入");

    /// <summary>"Extends: Steam engine – principle" / "延伸自：蒸汽机－原理" under a question that extends a summary.</summary>
    public static string ExtendsFrom(string summary) => T($"Extends: {summary}", $"延伸自：{summary}");

    /// <summary>A question's row as a screen reader hears it.</summary>
    public static string RecommendedQuestionName(string question, string? basis) => T(
        basis is null
            ? $"Recommended question: {question}, press Enter to put it in the input box"
            : $"Recommended question: {question}, {ExtendsFrom(basis)}, press Enter to put it in the input box",
        basis is null ? $"推荐问题：{question}，按 Enter 填入输入框" : $"推荐问题：{question}，{ExtendsFrom(basis)}，按 Enter 填入输入框");

    public static string RecommendedQuestionsListName => T(
        "Recommended questions, choose with the up and down arrow keys",
        "推荐问题列表，用上下方向键选择");

    public static string PreparingRecommendations => T("Preparing recommended questions…", "正在准备推荐问题…");

    public static string RefreshingRecommendations => T("Refreshing…", "正在换一批…");

    /// <summary>Nothing recommended yet and nothing running (自动准备 off, or nothing usable came back).</summary>
    public static string NoRecommendationsYet => T(
        "Click “Refresh” to get a few questions recommended from this workspace’s conversations and summaries.",
        "点“换一批”，按本工作区的会话与总结推荐几个问题。");

    /// <summary>A background attempt failed: said quietly, with 重试.</summary>
    public static string RecommendationsQuietFailure => T(
        "Couldn’t prepare recommended questions this time.",
        "推荐问题暂时没能准备好。");

    public static string RetryRecommendationsName => T("Recommend questions again", "重新推荐问题");

    /// <summary>Why 换一批 made nothing (only a manual request explains; the background stays quiet).</summary>
    public static string RecommendationProblemText(RecommendationProblem problem, GenerationErrorKind? error) => problem switch
    {
        RecommendationProblem.NotConfigured => T(
            "No model is set up yet. Connect a model to get recommended questions.",
            "还没有配置模型，连接模型后才能推荐问题。"),
        RecommendationProblem.Credentials => error is { } k
            ? T($"Couldn’t recommend questions: {ErrorKind(k)}. Check the API key in Settings.", $"没能推荐问题：{ErrorKind(k)}，请在设置中检查 API 密钥。")
            : T("The API key can’t be used. Check it in Settings.", "API 密钥无法使用，请在设置中检查。"),
        RecommendationProblem.NoContent => T(
            "This workspace has no conversation titles or summaries to base questions on yet.",
            "这个工作区还没有可依据的会话标题或总结。"),
        RecommendationProblem.StoragePaused => SuggestionsStoragePaused,
        RecommendationProblem.Unavailable => T("Mindweave is quitting, so it can’t recommend questions right now.", "正在退出，暂时不能推荐问题。"),
        RecommendationProblem.Empty => T(
            "No usable new questions came back this time. You can try again.",
            "这次没有得到可用的新问题，可以再试一次。"),
        RecommendationProblem.Failed => error is { } kind
            ? T($"Couldn’t recommend questions: {ErrorKind(kind)}.", $"没能推荐问题：{ErrorKind(kind)}。")
            : T("Couldn’t recommend questions. Please try again.", "没能推荐问题，请重试。"),
        _ => T("Couldn’t recommend questions. Please try again.", "没能推荐问题，请重试。"),
    };

    // Settings (设置 › 提示, beside 自动准备工作区名称建议): requirements §10.3 共同规则 and §9.4.
    public static string RecommendQuestionsSetting => T("Recommended questions in New exploration", "新探索推荐问题");
    public static string RecommendNodesSetting => T("Recommended nodes in the summary network", "网络推荐节点");
    public static string AutoPrepareRecommendations => T("Prepare recommendations automatically", "自动准备推荐");
    public static string RecommendationsHint => T(
        "Only conversation titles, summary titles and short summary excerpts are sent to the model, never whole conversations. Preparing automatically sends requests in the background, which costs a little. With it off, recommendations are made only when you click “Refresh”.",
        "只把会话标题、总结标题和总结的简短摘录发给模型，不发送完整对话；自动准备会在后台发请求，产生少量调用费用。关闭自动准备后，只在点“换一批”时生成。");
    public static string RecommendationsSaveFailed => T(
        "Couldn’t save the recommendation settings. They still apply until you quit.",
        "推荐设置未能保存，本次运行中仍然有效。");
}
