using System.Globalization;
using Mindweave.Application.Conversations;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;

namespace Mindweave.Desktop;

// The conversation page: asking, answers and their states, branches and the context line.
public static partial class UiText
{
    public const string NewConversation = "新会话";
    public const string ConversationMissing = "会话不存在";
    public const string AskPlaceholder = "提问…";
    public const string Send = "发送";
    public const string Stop = "停止";
    public const string Copy = "复制";
    public const string Copied = "已复制";
    public const string Regenerate = "重新生成";
    public const string Rename = "改名";
    public const string Save = "保存";
    public const string OpenSettingsLink = "前往设置";
    public const string EarlierAttempt = "较早的尝试";
    public const string RetryAttempt = "重新回答";
    public const string MarkerGenerating = "正在回答";
    public const string MarkerSummarizing = "正在总结";
    public const string MarkerNeedsAttention = "回答未完成，可重试";
    public const string MarkerNewAnswer = "有新回答";
    public const string CreateBranch = "创建分支";
    public const string ReturnToSource = "返回来源";
    public const string ReturnToSourceHint = "回到来源回答，并选中这段原文";
    public const string BranchFrom = "来自";
    public const string BranchesHeading = "从这里分出的探索";

    /// <summary>A mark in the conversation's margin for an answer with branches: "2 个分支 · 「蒸汽机车」".</summary>
    public static string BranchMark(int count, string firstQuote) =>
        string.Format(CultureInfo.InvariantCulture, "{0} 个分支 · {1}", count, Quoted(firstQuote));

    public const string WaitingForAnswer = "正在等待回答…";
    public const string AnswerStreaming = "正在生成…";
    public const string CompressingContext = "正在压缩较早的对话…";
    public const string ModelThinking = "正在思考…";
    public const string ContextCompressedNote = "较早的对话已压缩为摘要发送，原记录完整保留";

    /// <summary>
    /// The name shown for a conversation: its title, else (for a branch) the quoted passage it explores, else its
    /// first question. Sidebar, page header and ancestor path all use this, so a conversation is named the same everywhere.
    /// </summary>
    public static string ConversationTitle(Conversation conversation, string? firstQuestion, string? quote)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation.Title
            ?? (quote is not null ? Quoted(quote) : null)
            ?? (firstQuestion is not null ? ConversationRules.FallbackTitle(firstQuestion) : NewConversation);
    }

    /// <summary>The context line above the input: how much of the model's budget the conversation takes and what happens next.</summary>
    public static string ContextUsage(ContextUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var c = CultureInfo.CurrentCulture;
        if (usage.TooLong)
        {
            return string.Format(c, "上下文约 {0:N0} token，超出可用的 {1:N0}：压缩较早内容后也放不下。可以从需要的回答处创建分支继续，或在设置中换用容量更大的模型。", usage.FullTokens, usage.Budget);
        }

        var line = string.Format(c, "上下文约 {0:N0} / {1:N0} token（{2:P0}）", usage.Tokens, usage.Budget, usage.Fraction);
        if (usage.Compressed)
        {
            return line + string.Format(c, " · 较早的对话以摘要发送（完整约 {0:N0}）", usage.FullTokens);
        }

        return usage.Fraction >= 0.8 ? line + " · 接近上限，之后较早的对话会先压缩成摘要" : line;
    }

    public static string Quoted(string quote) => "「" + ConversationRules.Shorten(quote, ConversationRules.FallbackTitleLength) + "」";

    public static string SourceNotLocated(string quote) => $"无法精确定位原句（回答的排版或内容与创建分支时不同），已回到来源回答。原句：{Quoted(quote)}";

    public static string BranchRejected(BranchRejection rejection) => rejection switch
    {
        BranchRejection.AnswerNotFinished => "回答完成或停止后才能从中创建分支。",
        BranchRejection.EmptyAnswer or BranchRejection.NotAnAnswer => "只能从有内容的 AI 回答中创建分支。",
        BranchRejection.EmptyQuote => "请先选中回答中的文字。",
        BranchRejection.QuoteTooLong => $"选中的文字太长（最多 {BranchRules.MaxQuoteLength} 字），请缩小范围。",
        BranchRejection.AnchorTooLarge => "选区跨越的内容太多，请缩小范围。",
        BranchRejection.StorageUnavailable => "数据暂时无法保存，分支没有创建。",
        BranchRejection.SourceNotFound => "来源回答已不存在。",
        BranchRejection.ConversationArchived => "这个会话已归档，恢复后才能创建分支。",
        _ => "无法创建分支。",
    };

    public static string ErrorKind(GenerationErrorKind kind) => kind switch
    {
        GenerationErrorKind.Unauthorized => "密钥无效或无权访问",
        GenerationErrorKind.QuotaOrRateLimited => "额度不足或请求过于频繁",
        GenerationErrorKind.ContextTooLong => "内容超出模型上下文长度",
        GenerationErrorKind.ServerError => "服务端出错",
        GenerationErrorKind.Network => "网络连接失败",
        GenerationErrorKind.Timeout => "等待超时",
        GenerationErrorKind.InvalidResponse => "服务返回了无法识别的内容",
        GenerationErrorKind.CredentialUnavailable => "系统凭据服务不可用（可能已锁定），请解锁后重试",
        GenerationErrorKind.NotConfigured => "尚未配置模型或密钥",
        GenerationErrorKind.StorageUnavailable => "无法保存到本地数据",
        GenerationErrorKind.CompressionFailed => "压缩较早的对话失败，提问已保留",
        _ => "未知错误",
    };

    public static string? AnswerState(MessageState state, GenerationErrorKind? error) => state switch
    {
        MessageState.Pending => "等待中…",
        MessageState.Streaming => null,
        MessageState.Stopped => "已停止",
        MessageState.Failed => "失败：" + (error is { } e ? ErrorKind(e) : "未知错误"),
        MessageState.Interrupted => "已中断（上次退出时未完成，未自动重发）",
        MessageState.Cancelled => "已取消",
        _ => null,
    };

    public static string SendRejected(SendRejection rejection) => rejection switch
    {
        SendRejection.EmptyQuestion => "请输入问题。",
        SendRejection.QuestionTooLong => "问题过长。",
        SendRejection.NotConfigured => "还没有配置模型连接。",
        SendRejection.AnswerInProgress => "当前回答尚未结束，请等待或先停止。",
        SendRejection.StorageUnavailable => "本地数据暂时无法保存，已暂停新的提问；已有内容仍可阅读和复制。",
        SendRejection.NotRetryable => "只能重试会话中最后一条回答。",
        SendRejection.QuestionTooLargeForModel => "问题连同必需的来源内容已超出模型的上下文容量。请缩短问题或缩小分支选区，或在设置中换用容量更大的模型并填写上下文容量。",
        SendRejection.HistoryTooLongForModel => "这段对话即使压缩较早内容也放不进模型的上下文容量。可以从需要的回答处创建分支继续，或在设置中换用容量更大的模型。",
        SendRejection.ConversationArchived => "这个会话已归档，请先恢复。",
        SendRejection.ConversationMissing => ConversationDeleted,
        _ => "无法发送。",
    };
}
