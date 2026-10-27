using System.Globalization;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Startup;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop;

/// <summary>All user-facing text in one place, so wording stays consistent and can be localized later.</summary>
public static class UiText
{
    public const string AppName = "Mindweave";
    public const string StartupOpening = "正在打开数据…";
    public const string OnboardingHeading = "新建一个主题工作区";
    public const string OnboardingBody = "工作区按主题组织一次或一类探索中的会话与总结。之后可以随时新建或切换。";
    public const string WorkspaceNameWatermark = "例如：火车历史";
    public const string Create = "创建";
    public const string Cancel = "取消";
    public const string NewWorkspace = "新建工作区";
    public const string SectionExplore = "探索";
    public const string SectionNetwork = "总结网络";
    public const string SectionSettings = "设置";
    public const string Back = "后退";
    public const string ExploreEmpty = "这个工作区还没有会话。";
    public const string NetworkTitle = "总结网络";
    public const string NetworkEmpty = "还没有总结。保存总结后，它们会按探索来源出现在这里。";
    public const string SettingsTitle = "设置";
    public const string StorageTitle = "数据与存储";
    public const string StorageDetailsLink = "数据与存储…";
    public const string DataDirectoryLabel = "数据目录";
    public const string DatabaseLabel = "数据库";
    public const string SchemaVersionLabel = "数据结构版本";
    public const string BackupsLabel = "升级前备份";
    public const string NoBackups = "暂无备份。只有在数据需要升级时才会自动备份。";
    public const string AppVersionLabel = "版本";
    public const string ChannelLabel = "数据通道";
    public const string OpenDataFolder = "打开数据位置";
    public const string Retry = "重试";
    public const string Exit = "退出";
    public const string RestoreThis = "恢复此备份…";
    public const string ConfirmRestore = "确认恢复";
    public const string AvailableBackups = "可用的备份";
    public const string NewConversation = "新会话";
    public const string ConversationMissing = "会话不存在";
    public const string AskPlaceholder = "提问…（Enter 发送，Shift+Enter 换行）";
    public const string Send = "发送";
    public const string Stop = "停止";
    public const string Regenerate = "重新生成";
    public const string Rename = "改名";
    public const string Save = "保存";
    public const string OpenSettingsLink = "前往设置";
    public const string EarlierAttempt = "较早的尝试";
    public const string RetryAttempt = "重新回答";
    public const string MarkerGenerating = "正在生成";
    public const string MarkerNeedsAttention = "回答未完成，可重试";
    public const string CreateBranch = "就选中的文字创建分支";
    public const string ReturnToSource = "返回来源";
    public const string BranchFrom = "来自";
    public const string BranchesHeading = "从这里分出的探索";
    public const string ModelSection = "模型连接";
    public const string ConnectionNameLabel = "名称（可选）";
    public const string BaseUrlLabel = "服务地址";
    public const string BaseUrlHint = "OpenAI 兼容 Chat Completions 地址，通常以 /v1 结尾，例如 https://api.deepseek.com";
    public const string ApiKeyLabel = "API 密钥";
    public const string ContextWindowLabel = "上下文容量";
    public const string ContextWindowPlaceholder = "留空按 32000 计";
    public const string ContextWindowHint = "模型一次能读入的 token 数，例如 DeepSeek 为 128000。对话超出时，较早的内容会先压缩成摘要再发送：这会多发几次请求、多等一会儿，也会产生相应费用；原始记录始终完整保留在本地。";
    public const string WaitingForAnswer = "正在等待回答…";
    public const string CompressingContext = "正在压缩较早的对话…";
    public const string ContextCompressedNote = "较早的对话已压缩为摘要发送，原记录完整保留";
    public const string ModelLabel = "模型";
    public const string FetchModels = "获取模型列表";
    public const string TestConnection = "测试连接";
    public const string TestCostHint = "测试会发出一次很短的真实请求，可能产生少量费用。获取模型列表不生成内容。";
    public const string PrivacyHint = "提问及会话内容会发送到上面配置的服务。密钥保存在系统钥匙串／凭据管理器中，不写入数据文件。";
    public const string KeySavedHint = "已保存密钥。留空表示继续使用；填写则替换。";
    public const string KeyMissingHint = "尚未保存密钥。";
    public const string SettingsSaved = "已保存。新的提问将使用此配置；正在进行的回答不受影响。";
    public const string NoModelsFound = "服务没有返回模型，可以直接手动填写模型名称。";
    public const string TestSucceeded = "连接正常。";
    public const string ExitTitle = "仍有回答在生成";
    public const string ExitConfirm = "停止并退出";
    public const string ExitCancel = "继续生成";

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
        _ => "无法创建分支。",
    };

    public static string ModelsFound(int count) => string.Format(CultureInfo.CurrentCulture, "找到 {0} 个模型。", count);

    public static string ExitWithRunningAnswers(int count) => string.Format(
        CultureInfo.CurrentCulture,
        "有 {0} 个回答尚未完成。退出会停止它们，已收到的内容会保存并标记为“已停止”；排队中的会被取消。下次启动不会自动重发。",
        count);

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
        _ => "无法发送。",
    };

    public static string TestFailed(GenerationErrorKind? kind, string? detail) =>
        $"连接失败：{(kind is { } k ? ErrorKind(k) : "未知错误")}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $"（{detail}）")}";

    public static string SettingsProblem(ModelSettingsProblem problem) => problem switch
    {
        ModelSettingsProblem.InvalidUrl => "服务地址无效，需要以 https:// 开头的完整地址。",
        ModelSettingsProblem.InsecureUrl => "只允许 https 地址（本机地址除外），以免密钥以明文传输。",
        ModelSettingsProblem.MissingModel => "请填写模型名称。",
        ModelSettingsProblem.MissingKey => "请填写 API 密钥。",
        ModelSettingsProblem.InvalidContextWindow => $"上下文容量应在 {ModelSettingsInput.MinContextWindow:N0} 到 {ModelSettingsInput.MaxContextWindow:N0} token 之间，不清楚可留空。",
        _ => "设置无效。",
    };

    public static string CredentialUnavailable(string detail) => $"系统凭据服务不可用，请解锁后重试（{detail}）。密钥不会改为明文保存。";

    public static string WorkspaceNameError(WorkspaceNameError error) => error switch
    {
        Core.Workspaces.WorkspaceNameError.Empty => "名称不能为空。",
        Core.Workspaces.WorkspaceNameError.TooLong => string.Format(CultureInfo.CurrentCulture, "名称最多 {0} 个字符。", WorkspaceName.MaxLength),
        _ => "名称无效。",
    };

    public static string SaveFailed(string detail) => $"保存失败，输入内容已保留：{detail}";

    public static string RecoveryHeading(DatabaseOpenStatus status) => status switch
    {
        DatabaseOpenStatus.Unreadable => "无法读取数据",
        DatabaseOpenStatus.ForeignDatabase => "数据目录中的文件不属于 Mindweave",
        DatabaseOpenStatus.TooNew => "数据来自更新版本的 Mindweave",
        DatabaseOpenStatus.BackupFailed => "升级前备份失败",
        DatabaseOpenStatus.MigrationFailed => "数据升级失败",
        DatabaseOpenStatus.IoError => "无法访问数据目录",
        _ => "无法打开数据",
    };

    public static string RecoveryExplanation(DatabaseOpenResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            DatabaseOpenStatus.Unreadable => "数据库文件损坏或不是有效的数据库。Mindweave 没有修改它。可以检查数据位置，或从备份恢复。",
            DatabaseOpenStatus.ForeignDatabase => "数据目录中的 mindweave.db 是其他程序的数据库。Mindweave 没有修改它；请移走该文件后重试。",
            DatabaseOpenStatus.TooNew => string.Format(
                CultureInfo.CurrentCulture,
                "数据结构版本为 {0}，本程序只支持到 {1}。为避免损坏数据，本程序不会写入。请使用更新的 Mindweave 打开。",
                result.FoundVersion,
                result.SupportedVersion),
            DatabaseOpenStatus.BackupFailed => "数据需要升级，但升级前的备份没有成功，因此没有开始升级，原数据未改动。请检查磁盘空间与权限后重试。",
            DatabaseOpenStatus.MigrationFailed => "升级过程中出错，已整体撤销，原数据保持升级前的状态。升级前备份已保存，可在下方查看。",
            DatabaseOpenStatus.IoError => "无法读写数据目录。请检查磁盘空间与权限后重试。",
            _ => string.Empty,
        };
    }

    public static string RestoreConsequence(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return string.Format(
            CultureInfo.CurrentCulture,
            "将用 {0:yyyy-MM-dd HH:mm} 的备份（结构版本 {1}）替换当前数据库。当前文件会改名保留在数据目录中，不会删除；备份之后的改动不在备份里。",
            backup.CreatedAt.ToLocalTime(),
            backup.SchemaVersion);
    }

    public static string FormatBackup(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return string.Format(
            CultureInfo.CurrentCulture,
            "{0:yyyy-MM-dd HH:mm}　结构版本 {1}　{2:N0} KB",
            backup.CreatedAt.ToLocalTime(),
            backup.SchemaVersion,
            backup.SizeBytes / 1024);
    }
}
