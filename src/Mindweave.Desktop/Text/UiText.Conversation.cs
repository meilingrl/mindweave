using System.Globalization;
using Mindweave.Application.Conversations;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// The conversation page: asking, answers and their states, branches and the context line.
public static partial class UiText
{
    public static string NewConversation => ConversationNames.NewConversation;
    public static string ConversationMissing => T("Conversation not found", "会话不存在");
    public static string AskPlaceholder => T("Ask a question…", "提问…");
    public static string Send => T("Send", "发送");
    public static string Stop => T("Stop", "停止");
    public static string Copy => T("Copy", "复制");
    public static string Copied => T("Copied", "已复制");
    public static string Regenerate => T("Regenerate", "重新生成");
    public static string Rename => T("Rename", "改名");
    public static string Save => T("Save", "保存");
    public static string OpenSettingsLink => T("Go to Settings", "前往设置");
    public static string EarlierAttempt => T("Earlier attempt", "较早的尝试");
    public static string MarkerGenerating => T("Answering", "正在回答");
    public static string MarkerSummarizing => T("Summarizing", "正在总结");
    public static string MarkerNeedsAttention => T("Answer incomplete; you can retry", "回答未完成，可重试");
    public static string MarkerNewAnswer => T("New answer", "有新回答");
    public static string CreateBranch => T("Create branch", "创建分支");

    /// <summary>Creating a branch from the selection by keyboard (2026-11-13; ⌘/Ctrl+B stays the sidebar's).</summary>
    public static string BranchShortcut => OperatingSystem.IsMacOS() ? "⌘⇧B" : "Ctrl+Shift+B";

    public static string CreateBranchTip => T(CreateBranch + " (" + BranchShortcut + ")", CreateBranch + "（" + BranchShortcut + "）");
    public static string ReturnToSource => T("Back to source answer", "返回来源");
    public static string ReturnToSourceHint => T("Go back to the source answer and select this passage", "回到来源回答，并选中这段原文");
    public static string BranchFrom => T("From", "来自");
    public static string BranchesHeading => T("Explorations branched from here", "从这里分出的探索");

    /// <summary>A mark in the conversation's margin for an answer with branches: "2 个分支 · 「蒸汽机车」".</summary>
    /// <param name="count">How many.</param>
    /// <param name="first">The first one as its entry names it: the sentence it left from, quoted, or a continuation's title.</param>
    public static string BranchMark(int count, string first) => T(
        string.Format(CultureInfo.InvariantCulture, "{0} · {1}", Plural(count, "branch", "branches"), first),
        string.Format(CultureInfo.InvariantCulture, "{0} 个分支 · {1}", count, first));

    // The status line of a running answer, one sentence per stage (docs/design/reasoning-and-status.md §2): "正在 + verb
    // + object…", the object after a colon, plain enough to translate sentence by sentence.
    public static string WaitingForAnswer => T("Waiting for the answer…", "正在等待回答…");
    public static string AnswerWriting => T("Writing the answer…", "正在写回答…");
    public static string CompressingContext => T("Compressing earlier conversation…", "正在压缩较早的对话…");
    public static string ModelThinking => T("Thinking…", "正在思考…");
    public static string AnswerRetrying => T("Request failed, retrying…", "请求出错，正在重试…");
    public static string AnswerFinishing => T("Answering from the results so far…", "正在根据已有结果作答…");

    // The reasoning fold at the top of a running answer (requirements §10.4.4 v0.21).
    public static string ReasoningTitle => T("Thinking", "思考过程");
    public static string ReasoningExpand => T("Show thinking", "展开思考过程");
    public static string ReasoningCollapse => T("Hide thinking", "收起思考过程");
    public static string ContextCompressedNote => T("Earlier conversation was sent as a context summary; the full record is kept.", "较早的对话已压缩为摘要发送，原记录完整保留");

    /// <summary>
    /// The name shown for a conversation (<see cref="ConversationNames.Of"/>): its title, else (for a branch) the quoted
    /// passage it explores, else its first question. Sidebar, page header, ancestor path and the summary network all use
    /// this rule, so a conversation is named the same everywhere.
    /// </summary>
    public static string ConversationTitle(Conversation conversation, string? firstQuestion, string? quote)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return ConversationNames.Of(conversation.Title, quote, firstQuestion);
    }

    /// <summary>The context line above the input: how much of the model's budget the conversation takes and what happens next.</summary>
    public static string ContextUsage(ContextUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var c = CultureInfo.CurrentCulture;
        var en = Culture;
        if (usage.TooLong)
        {
            return T(
                string.Format(en, "Context is about {0:N0} tokens, more than the {1:N0} available, and doesn’t fit even with earlier parts compressed. You can create a branch from the answer you need and continue there, or switch to a model with a larger context window in Settings.", usage.FullTokens, usage.Budget),
                string.Format(c, "上下文约 {0:N0} token，超出可用的 {1:N0}：压缩较早内容后也放不下。可以从需要的回答处创建分支继续，或在设置中换用容量更大的模型。", usage.FullTokens, usage.Budget));
        }

        var line = T(
            string.Format(en, "Context: about {0:N0} / {1:N0} tokens ({2:P0})", usage.Tokens, usage.Budget, usage.Fraction),
            string.Format(c, "上下文约 {0:N0} / {1:N0} token（{2:P0}）", usage.Tokens, usage.Budget, usage.Fraction));
        if (usage.Compressed)
        {
            return line + T(
                string.Format(en, " · earlier conversation sent as a context summary (about {0:N0} in full)", usage.FullTokens),
                string.Format(c, " · 较早的对话以摘要发送（完整约 {0:N0}）", usage.FullTokens));
        }

        return usage.Fraction >= 0.8
            ? line + T(" · near the limit; next, earlier conversation will be compressed into a context summary", " · 接近上限，之后较早的对话会先压缩成摘要")
            : line;
    }

    /// <summary>The ring's tooltip: the share and the figures, nothing else (the details are one press away).</summary>
    public static string ContextUsageShort(ContextUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return T("Context used ", "上下文已用 ") + ContextPercent(usage) + " · " + ContextTotal(usage);
    }

    public static string ContextUsageTitle => T("Context used", "上下文已用");

    /// <summary>"1%", "&lt;1%" for a little, "&gt;100%" when it no longer fits.</summary>
    public static string ContextPercent(ContextUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return usage.TooLong ? ">100%"
            : usage.Tokens > 0 && usage.Fraction < 0.005 ? "<1%"
            : usage.Fraction.ToString("0%", CultureInfo.InvariantCulture);
    }

    /// <summary>"~14.5K / 61K": what is used, an estimate, of what there is room for.</summary>
    public static string ContextTotal(ContextUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return "~" + CompactTokens(usage.Tokens) + " / " + CompactTokens(usage.Budget);
    }

    public static string CompactTokens(int tokens) => tokens switch
    {
        >= 999_950 => (tokens / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (tokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    public static string ContextPart(ContextUsageKind kind) => kind switch
    {
        ContextUsageKind.Ancestors => T("Source conversations", "来源会话"),
        ContextUsageKind.ContextSummaries => T("Context summaries", "上下文摘要"),
        ContextUsageKind.Attachments => T("Attachments", "附件"),
        ContextUsageKind.Instructions => T("System prompt", "系统提示词"),
        ContextUsageKind.Tools => T("Tool definitions", "工具定义"),
        _ => T("This conversation", "本会话"),
    };

    /// <summary>What the state means for the next question; null while there is plenty of room.</summary>
    public static string? ContextUsageNote(ContextUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return usage.TooLong
            ? T("It no longer fits. Branch from the answer you need, or use a model with a larger context window.", "已放不下。可以从需要的回答处创建分支，或换用容量更大的模型。")
            : usage.Compressed
                ? T($"Earlier conversation is sent as a context summary (about {CompactTokens(usage.FullTokens)} in full).", $"较早的对话以摘要发送（完整约 {CompactTokens(usage.FullTokens)}）。")
                : usage.Fraction >= 0.8
                    ? T("Near the limit: earlier conversation will be compressed into a context summary.", "接近上限，之后较早的对话会先压缩成摘要。")
                    : null;
    }

    public static string ContextUsageEstimate => T("Estimated for the next question", "按下一次提问估算");

    /// <summary>The context ring's name for a screen reader when there is no figure yet.</summary>
    public static string ContextRingName => T("Context usage", "上下文用量");

    public static string Quoted(string quote) => ConversationNames.Quoted(quote);

    public static string SourceNotLocated(string quote) => T(
        $"Couldn’t find the exact passage (the answer’s layout or content differs from when the branch was created), so you’re back at the source answer. Passage: {Quoted(quote)}",
        $"无法精确定位原句（回答的排版或内容与创建分支时不同），已回到来源回答。原句：{Quoted(quote)}");

    public static string BranchRejected(BranchRejection rejection) => rejection switch
    {
        BranchRejection.AnswerNotFinished => T("You can create a branch only after the answer finishes or is stopped.", "回答完成或停止后才能从中创建分支。"),
        BranchRejection.EmptyAnswer or BranchRejection.NotAnAnswer => T("Branches can only be created from an AI answer with content.", "只能从有内容的 AI 回答中创建分支。"),
        BranchRejection.EmptyQuote => T("Select some text in the answer first.", "请先选中回答中的文字。"),
        BranchRejection.QuoteTooLong => T($"The selected text is too long (at most {BranchRules.MaxQuoteLength} characters). Select less.", $"选中的文字太长（最多 {BranchRules.MaxQuoteLength} 字），请缩小范围。"),
        BranchRejection.AnchorTooLarge => T("The selection spans too much content. Select less.", "选区跨越的内容太多，请缩小范围。"),
        BranchRejection.StorageUnavailable => T("Data can’t be saved right now, so the branch was not created.", "数据暂时无法保存，分支没有创建。"),
        BranchRejection.SourceNotFound => T("The source answer no longer exists.", "来源回答已不存在。"),
        BranchRejection.ConversationArchived => T("This conversation is archived. Restore it to create branches.", "这个会话已归档，恢复后才能创建分支。"),
        _ => T("Couldn’t create the branch.", "无法创建分支。"),
    };

    public static string ErrorKind(GenerationErrorKind kind) => kind switch
    {
        GenerationErrorKind.Unauthorized => T("invalid key or no access", "密钥无效或无权访问"),
        GenerationErrorKind.QuotaOrRateLimited => T("quota used up or too many requests", "额度不足或请求过于频繁"),
        GenerationErrorKind.ContextTooLong => T("the content exceeds the model’s context length", "内容超出模型上下文长度"),
        GenerationErrorKind.ServerError => T("server error", "服务端出错"),
        GenerationErrorKind.Network => T("network connection failed", "网络连接失败"),
        GenerationErrorKind.Timeout => T("timed out", "等待超时"),
        GenerationErrorKind.InvalidResponse => T("the service returned content that couldn’t be read", "服务返回了无法识别的内容"),
        GenerationErrorKind.CredentialUnavailable => T("the system credential service is unavailable (it may be locked); unlock it and try again", "系统凭据服务不可用（可能已锁定），请解锁后重试"),
        GenerationErrorKind.NotConfigured => T("no model or key is set up yet", "尚未配置模型或密钥"),
        GenerationErrorKind.StorageUnavailable => T("couldn’t save to local data", "无法保存到本地数据"),
        GenerationErrorKind.CompressionFailed => T("compressing earlier conversation failed; your question was kept", "压缩较早的对话失败，提问已保留"),
        GenerationErrorKind.OutputLimit => T("the model’s output length limit was reached, so the answer is incomplete", "达到模型的输出长度上限，回答不完整"),
        GenerationErrorKind.ContentFiltered => T("the service’s content moderation stopped the answer", "服务的内容审核中止了回答"),
        _ => T("unknown error", "未知错误"),
    };

    public static string? AnswerState(MessageState state, GenerationErrorKind? error) => state switch
    {
        MessageState.Pending => T("Waiting…", "等待中…"),
        MessageState.Streaming => null,
        MessageState.Stopped => error is { } cut ? T("Incomplete: " + ErrorKind(cut), "未完成：" + ErrorKind(cut)) : T("Stopped", "已停止"),
        MessageState.Failed => T("Failed: " + (error is { } fe ? ErrorKind(fe) : "unknown error"), "失败：" + (error is { } e ? ErrorKind(e) : "未知错误")),
        MessageState.Interrupted => T("Interrupted (unfinished when the app last quit; not resent automatically)", "已中断（上次退出时未完成，未自动重发）"),
        MessageState.Cancelled => T("Canceled", "已取消"),
        _ => null,
    };

    public static string SendRejected(SendRejection rejection) => rejection switch
    {
        SendRejection.EmptyQuestion => T("Enter a question.", "请输入问题。"),
        SendRejection.QuestionTooLong => T("The question is too long.", "问题过长。"),
        SendRejection.NotConfigured => T("No model connection is set up yet.", "还没有配置模型连接。"),
        SendRejection.AnswerInProgress => T("The current answer hasn’t finished. Wait, or stop it first.", "当前回答尚未结束，请等待或先停止。"),
        SendRejection.StorageUnavailable => T("Local data can’t be saved right now, so new questions are paused; you can still read and copy what’s here.", "本地数据暂时无法保存，已暂停新的提问；已有内容仍可阅读和复制。"),
        SendRejection.NotRetryable => T("Only the last answer in a conversation can be retried.", "只能重试会话中最后一条回答。"),
        SendRejection.QuestionTooLargeForModel => T(
            "The question, with its attachments and the branch source content it needs, is too large for the model’s context window, even when only the attachments’ digests and relevant parts are sent. Shorten the question, remove attachments or narrow the branch selection, or switch to a model with a larger context window in Settings and fill in its context window.",
            "问题连同附件与必需的来源内容，即使只发送附件的摘要和相关部分，也超出了模型的上下文容量。请缩短问题、减少附件或缩小分支选区，或在设置中换用容量更大的模型并填写上下文容量。"),
        SendRejection.HistoryTooLongForModel => T(
            "This conversation doesn’t fit in the model’s context window, even with earlier parts compressed. You can create a branch from the answer you need and continue there, or switch to a model with a larger context window in Settings.",
            "这段对话即使压缩较早内容也放不进模型的上下文容量。可以从需要的回答处创建分支继续，或在设置中换用容量更大的模型。"),
        SendRejection.ConversationArchived => T("This conversation is archived. Restore it first.", "这个会话已归档，请先恢复。"),
        SendRejection.ConversationMissing => ConversationDeleted,
        SendRejection.TooManyAttachments => T("A question can have at most 10 attachments.", "一条提问最多带 10 个附件。"),
        SendRejection.ImagesNotSupported => T("The current model can’t view images. Remove the image attachments, or declare image support in Settings › Model connection › Advanced settings.", "当前模型不能查看图片，请移除图片附件，或在设置 › 模型连接 › 高级设置中声明。"),
        SendRejection.AttachmentMissing => T("An attachment is no longer there. Attach it again.", "有附件已不在，请重新附加。"),
        _ => T("Couldn’t send.", "无法发送。"),
    };

    /// <summary>The input box's name for screen readers.</summary>
    public static string AskAccessibleName => T("Ask a question", "提问");

    /// <summary>Two sentences read one after the other, with the language's own full stop between them.</summary>
    public static string Sentences(string first, string second) => T(first + ". " + second, first + "。" + second);

    /// <summary>Names in a run, separated the language's way: "a, b" / "a、b".</summary>
    public static string List(IEnumerable<string> names)
    {
        var all = names.ToList();
        return T(string.Join(", ", all), string.Join("、", all));
    }
}
