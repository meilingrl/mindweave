using System.Globalization;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Models;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop;

/// <summary>All user-facing text in one place, so wording stays consistent and can be localized later.</summary>
public static class UiText
{
    public const string AppName = "Mindweave";
    public const string StartupOpening = "正在打开数据…";
    public const string OnboardingHeading = "欢迎使用 Mindweave";
    public const string OnboardingBody = "先建一个主题工作区，这个主题下的会话与总结都放在这里。";
    public const string CreateWorkspace = "创建工作区";
    public const string WorkspaceNameWatermark = "例如：火车历史";
    public const string WorkspaceNameLabel = "工作区名称";
    public const string Create = "创建";
    public const string Cancel = "取消";
    public const string NewWorkspace = "新建工作区";
    public const string SectionExplore = "探索";
    public const string SectionNetwork = "总结网络";
    public const string SectionSettings = "设置";
    public const string ConversationsHeading = "会话";
    public const string Back = "后退";

    // Sidebar collapse and resize (S7 round 3 · shell)
    public const string CollapseSidebar = "收起侧栏";
    public const string ExpandSidebar = "展开侧栏";
    public const string SidebarSplitter = "侧栏宽度";

    /// <summary>The toggle's shortcut as the platform writes it (⌘ on macOS).</summary>
    public static string SidebarShortcut { get; } = OperatingSystem.IsMacOS() ? "⌘B" : "Ctrl+B";

    public static string CollapseSidebarTip { get; } = CollapseSidebar + "（" + SidebarShortcut + "）";

    public static string ExpandSidebarTip { get; } = ExpandSidebar + "（" + SidebarShortcut + "）";

    public static string ExpandSidebarAttention { get; } = ExpandSidebarTip + " · 有会话需要处理";
    public const string ExploreEmpty = "这个工作区还没有会话。";
    public const string NetworkTitle = "总结网络";
    public const string NetworkEmpty = "还没有总结。在会话中点“总结此会话”即可生成。";
    public const string LayoutUnsavedTitle = "布局还没有保存";
    public const string LayoutUnsavedBody = "不保存不会丢失会话或总结，下次按上次保存的位置显示。";
    public const string LayoutSaveAndContinue = "保存并继续";
    public const string LayoutDontSave = "不保存";
    public const string LayoutSaveFailed = "布局保存失败，改动仍保留在当前窗口。";

    /// <summary>Core storage failed (requirements §9.3): what is paused, what still works, and that it lifts by itself.</summary>
    public const string DraftConflict = "这份修改基于较早的版本，不能保存为新版本。请复制需要的内容，再放弃修改。";

    public const string DraftStale = "这份修改已在别处保存或放弃，这里的改动不会写入。请复制需要的内容后重新打开。";

    public const string StoragePaused = "本地数据暂时无法保存，已暂停提问、重试、创建分支和生成总结；已有内容仍可阅读和复制。恢复保存后会自动解除。";

    /// <summary>Archiving, restoring or deleting could not be written; the transaction left everything as it was.</summary>
    public const string ArchiveStorageFailed = "本地数据暂时无法保存，没有做任何改动。请稍后再试。";

    /// <summary>The reason on an entry disabled while storage is paused.</summary>
    public const string StoragePausedReason = "本地数据暂时无法保存，恢复后可用";
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

    // Appearance (S7, D-022)
    public const string AppearanceSection = "外观";
    public const string AppearanceFollowSystem = "跟随系统";
    public const string AppearanceLight = "浅色";
    public const string AppearanceDark = "深色";
    public const string ThemeLabel = "主题";

    // Settings pages (S7 round 3): presets fill the address; hints only where the page cannot show it otherwise.
    public const string ProviderLabel = "服务商";
    public const string ProviderPlaceholder = "选择服务商";
    public const string ProviderDashScope = "阿里云百炼（通义）";
    public const string ProviderMoonshot = "Moonshot（Kimi）";
    public const string ProviderZhipu = "智谱 GLM";
    public const string ProviderSiliconFlow = "硅基流动";
    public const string ProviderOllama = "Ollama（本机）";
    public const string ProviderCustom = "自定义";
    public const string BaseUrlPlaceholder = "https://…/v1";
    public const string KeySavedPlaceholder = "已保存，留空则继续使用";
    public const string KeyMissingPlaceholder = "尚未保存";
    public const string KeyOtherServicePlaceholder = "请填写此服务的密钥";
    public const string OllamaKeyPlaceholder = "本机服务不校验，可填任意内容";
    public const string ConnectionNamePlaceholder = "可选";
    public const string ModelPlaceholder = "选择或输入模型名称";
    public const string ShowModels = "显示模型列表";
    public const string TokensUnit = "token";
    public const string DataSection = "数据";
    public const string LocationSection = "位置";
    public const string OpenFolder = "打开";
    public const string NoBackupsShort = "暂无。数据升级前会自动备份。";
    public const string AppearanceSaveFailed = "外观设置未能保存，本次运行中仍然有效。";

    // Summaries (S4)
    public const string Summarize = "总结此会话";
    public const string Resummarize = "重新总结";
    public const string SummaryGenerating = "正在生成总结…";
    public const string SummaryStop = "停止";
    public const string SummaryRetry = "重试";
    public const string ReturnToConversation = "回到来源会话继续";
    public const string MarkRead = "标记为已读";
    public const string MarkUnread = "标记为未读";
    public const string MarkerUnread = "未读";
    public const string MarkerCandidate = "有候选更新";
    public const string MarkerLater = "有后续内容";
    public const string CandidateHeading = "新生成（候选）";
    public const string CandidateNote = "这是重新生成的候选版本，当前版本保持不变。采用后它成为当前版本；丢弃后删除。";
    public const string ViewCandidate = "查看候选";
    public const string ViewCurrent = "回到当前版本";
    public const string Adopt = "采用";
    public const string Discard = "丢弃";
    public const string Edit = "编辑";
    public const string SaveEdit = "保存为新版本";
    public const string DiscardEdit = "放弃修改";
    public const string EditDraftSaved = "修改已自动保存为草稿；点击“保存为新版本”后才成为一个版本。";
    public const string EditTitleWatermark = "标题";
    public const string HistoryHeading = "版本历史";
    public const string SetCurrent = "设为当前总结";
    public const string View = "查看";
    public const string DeleteSelected = "删除所选版本";
    public const string Confirm = "确认";
    public const string SummaryMissing = "这个总结已不存在。";
    public const string LaterContentNote = "来源会话在这个版本之后还有新的内容，未纳入总结。";
    public const string ReadIsNotMastery = "已读只表示读过这个版本，不代表已经掌握。";
    public const string EditNote = "保存后成为新的当前版本，原版本保留在版本历史中。";
    public const string RenameWatermark = "总结名称";

    // Export (S6, §8.3)
    public const string ExportMarkdown = "导出为 Markdown…";
    public const string ExportOmitSource = "不附来源说明";
    public const string ExportDraftNote = "导出的是页面上显示的已保存版本；未保存的编辑草稿不包含在内。";
    public const string ExportCandidateHint = "候选版本不能导出，请先采用。";
    public const string ExportOptionsHeading = "导出为 Markdown";
    public const string ExportRun = "选择位置并导出…";
    public const string MarkdownFileType = "Markdown 文件";

    // Archive and delete (S6, §9.5, D-021)
    public const string Archive = "归档";
    public const string ArchiveInstead = "改为归档";
    public const string Delete = "删除";
    public const string Restore = "恢复";
    public const string RestoreAndContinue = "恢复并继续";
    public const string RestoreAndSummarize = "恢复并总结";
    public const string ArchivedTitle = "已归档";
    public const string ArchivedMarker = "已归档";
    public const string SourceArchived = "来源已归档";
    public const string ArchivedReadOnly = "这个会话已归档，只能阅读。恢复后可以继续提问、创建分支或总结；已有总结照常可用。";
    public const string ArchivedEmpty = "没有已归档的会话。";
    public const string ArchivedNote = "归档的会话连同分支不在侧栏显示；它们的总结仍在总结网络中。";
    public const string DeleteConfirm = "将永久删除这个会话：全部消息、生成记录和未发送的草稿都会一起删除，无法撤销。";
    public static string DeleteConfirmFor(string title) => $"永久删除“{title}”？全部消息、生成记录和未发送的草稿都会一起删除，无法撤销。";
    public const string ConversationDeleted = "这个会话已被删除。";
    public const string SettingsTitle = "设置";
    public const string StorageTitle = "数据与存储";
    public const string StorageDetailsLink = "数据与存储";
    public const string DataDirectoryLabel = "数据目录";
    public const string DatabaseLabel = "数据库";
    public const string SchemaVersionLabel = "数据结构版本";
    public const string BackupsLabel = "升级前备份";
    public const string ChannelLabel = "数据通道";
    public const string OpenDataFolder = "打开数据位置";
    public const string Retry = "重试";
    public const string Exit = "退出";
    public const string RestoreThis = "恢复此备份…";
    public const string ConfirmRestore = "确认恢复";
    public const string AvailableBackups = "可用的备份";
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
    public const string MarkerGenerating = "正在生成";
    public const string MarkerNeedsAttention = "回答未完成，可重试";
    public const string CreateBranch = "创建分支";
    public const string ReturnToSource = "返回来源";
    public const string ReturnToSourceHint = "回到来源回答，并选中这段原文";
    public const string BranchFrom = "来自";
    public const string BranchesHeading = "从这里分出的探索";

    /// <summary>A mark in the conversation's margin for an answer with branches: "2 个分支 · 「蒸汽机车」".</summary>
    public static string BranchMark(int count, string firstQuote) =>
        string.Format(CultureInfo.InvariantCulture, "{0} 个分支 · {1}", count, Quoted(firstQuote));
    public const string ModelSection = "模型连接";
    public const string ConnectionNameLabel = "名称";
    public const string BaseUrlLabel = "服务地址";
    public const string ApiKeyLabel = "API 密钥";
    public const string ContextWindowLabel = "上下文容量";
    public const string ContextWindowPlaceholder = "留空按 32000 计";
    public const string ContextWindowHint = "对话超出时，较早的内容先压缩成摘要再发送，会多几次请求、多些等待与费用；原始记录完整保留。";
    public const string WaitingForAnswer = "正在等待回答…";
    public const string AnswerStreaming = "正在生成…";
    public const string CompressingContext = "正在压缩较早的对话…";
    public const string ContextCompressedNote = "较早的对话已压缩为摘要发送，原记录完整保留";
    public const string ModelLabel = "模型";
    public const string FetchModels = "获取列表";
    public const string TestConnection = "测试连接";
    public const string TestCostHint = "可能产生少量费用";
    public const string TestCostTip = "发出一次很短的真实请求。获取模型列表不生成内容。";
    public const string PrivacyHint = "提问与会话内容会发送到此服务；密钥只存于系统钥匙串／凭据管理器。";
    public const string SettingsSaved = "已保存。新的提问将使用此配置；正在进行的回答不受影响。";
    public const string NoModelsFound = "服务没有返回模型，可以直接手动填写模型名称。";
    public const string TestSucceeded = "连接正常。";
    public const string ExitTitle = "仍有任务在生成";
    public const string ExitConfirm = "停止并退出";
    public const string ExitCancel = "继续生成";
    public const string ExitSaving = "正在停止任务并保存……";
    public const string ExitSaveFailedTitle = "部分内容未能保存";
    public const string ExitSaveFailedBody = "草稿或回答的结束状态没能写入，内容仍保留在程序里，写入成功前不会退出。已停止的任务不会自动恢复。";
    public const string ExitRetry = "重试退出";
    public const string ExitReturn = "返回";

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

    public static string ModelsFound(int count) => string.Format(CultureInfo.CurrentCulture, "找到 {0} 个模型。", count);

    /// <summary>The exit prompt's body, naming user answers and summaries separately ("1 个回答、1 个总结").</summary>
    public static string ExitWithUnfinished(int answers, int summaries)
    {
        var parts = new List<string>(2);
        if (answers > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} 个回答", answers));
        }

        if (summaries > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} 个总结", summaries));
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0}还没有完成。退出会停止它们：已收到的回答会保留并标为“已停止”，总结不留新版本，排队的任务会取消，下次启动也不会重发。",
            string.Join('、', parts));
    }

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
            DatabaseOpenStatus.MigrationFailed => "升级过程中出错，已整体撤销，原数据保持升级前的状态，升级前的备份也已保存。",
            DatabaseOpenStatus.IoError => "无法读写数据目录。请检查磁盘空间与权限后重试。",
            _ => string.Empty,
        };
    }

    public static string RestoreConsequence(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return string.Format(
            CultureInfo.CurrentCulture,
            "将用 {0} 的备份（结构版本 {1}）替换当前数据库。当前文件会改名保留在数据目录中，不会删除；备份之后的改动不在备份里。",
            FormatTime(backup.CreatedAt),
            backup.SchemaVersion);
    }

    /// <summary>A backup row's second part beside its date: "结构版本 8 · 212 KB".</summary>
    public static string BackupDetails(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return string.Format(CultureInfo.CurrentCulture, "结构版本 {0} · {1:N0} KB", backup.SchemaVersion, backup.SizeBytes / 1024);
    }

    public const string DataLocation = "数据位置";
    /// <summary>A file size for reading: KB below one megabyte, MB with one decimal above.</summary>
    public static string FormatSize(long bytes) => bytes < 1024 * 1024
        ? string.Format(CultureInfo.CurrentCulture, "{0:N0} KB", Math.Max(1, (bytes + 1023) / 1024))
        : string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", bytes / (1024.0 * 1024.0));

    public static string SchemaVersionBadge(int version) => string.Format(CultureInfo.CurrentCulture, "结构版本 {0}", version);

    public static string FormatBackup(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return string.Format(
            CultureInfo.CurrentCulture,
            "{0}　结构版本 {1}　{2:N0} KB",
            FormatTime(backup.CreatedAt),
            backup.SchemaVersion,
            backup.SizeBytes / 1024);
    }

    public static string SummarizeRejected(SummarizeRejection rejection) => rejection switch
    {
        SummarizeRejection.NotConfigured => "还没有配置模型，无法生成总结。",
        SummarizeRejection.StorageUnavailable => "数据暂时无法保存，总结没有开始。",
        SummarizeRejection.NothingToSummarize => "会话里还没有完成的回答，暂时没有可总结的内容。",
        SummarizeRejection.AnswerInProgress => "回答完成或停止后才能总结。",
        SummarizeRejection.SummaryInProgress => "这个会话的总结正在生成。",
        SummarizeRejection.HasCandidate => "已有一个候选版本，请先在总结页采用或丢弃。",
        SummarizeRejection.TooLongForModel => "会话太长，压缩较早内容后也超出模型容量；可以在设置中换用容量更大的模型。",
        SummarizeRejection.ConversationArchived => "这个会话已归档，请先恢复再总结。",
        SummarizeRejection.ConversationMissing => ConversationDeleted,
        _ => "无法生成总结。",
    };

    public static string SummaryRefused(SummaryRefusal refusal) => refusal switch
    {
        SummaryRefusal.HasDraft => "有未保存的修改。请先保存为新版本或放弃修改。",
        SummaryRefusal.TaskRunning => "这个会话的总结正在生成，完成或停止后才能删除版本。",
        SummaryRefusal.HasCandidate => "还有一个候选版本。删除全部已保存版本前，请先采用或丢弃它。",
        SummaryRefusal.IsCandidate => "候选版本不能设为当前或作为版本删除，请采用或丢弃它。",
        _ => "总结已经变化，请查看最新内容后再操作。",
    };

    public static string SummaryFailed(SummaryTaskStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            GenerationTaskState.Interrupted => "上次的总结在退出或重启时中断，没有保存任何内容。",
            GenerationTaskState.Stopped or Application.Storage.GenerationTaskState.Cancelled => "上次的总结已停止，没有保存任何内容。",
            _ => "上次的总结失败，没有保存任何内容：" + (status.Error is { } kind ? ErrorKind(kind) : "原因未知") + (status.Detail is { Length: > 0 } detail ? "（" + detail + "）" : string.Empty),
        };
    }

    public static string SummaryEntry(string displayName) => "总结：" + displayName;

    public static string Markers(SummaryMarkers markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var parts = new List<string>();
        if (markers.Unread)
        {
            parts.Add(MarkerUnread);
        }

        if (markers.HasCandidate)
        {
            parts.Add(MarkerCandidate);
        }

        if (markers.HasLaterContent)
        {
            parts.Add(MarkerLater);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The read tag of a version other than the current one: "此版本已读" / "此版本未读".</summary>
    public static string ThisVersionRead(bool read) => read ? "此版本已读" : "此版本未读";

    /// <summary>The current version's read state in words, for screen readers: "当前版本已读" / "当前版本未读".</summary>
    public static string CurrentVersionRead(bool read) => read ? "当前版本已读" : "当前版本未读";

    /// <summary>
    /// The summary page's tags for the version on screen. On the current version the read tag is the node's own
    /// marker (未读 / 已读, as on the network and in the list); on any other version it says "此版本…".
    /// </summary>
    public static string ShownReadMarker(bool read, bool isCurrent) => isCurrent ? (read ? MarkerRead : MarkerUnread) : ThisVersionRead(read);

    /// <summary>The same tags in one line for screen readers, saying which version the read state belongs to.</summary>
    public static string ShownMarkers(bool read, bool isCurrent, bool hasCandidate, bool hasLaterContent)
    {
        var parts = new List<string> { isCurrent ? CurrentVersionRead(read) : ThisVersionRead(read) };
        if (hasCandidate)
        {
            parts.Add(MarkerCandidate);
        }

        if (hasLaterContent)
        {
            parts.Add(MarkerLater);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// A moment as the interface shows it everywhere (S7): local time, Chinese date and a 24-hour clock without
    /// AM/PM, for example "2026年3月14日 09:30", the same whatever the machine's regional format.
    /// </summary>
    public static string FormatTime(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy'年'M'月'd'日' HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// When a message was sent or answered: only the clock ("09:30") on the day it is shown, otherwise the full
    /// <see cref="FormatTime"/> date. Both moments are compared as local calendar days.
    /// </summary>
    public static string MessageTime(DateTimeOffset at, DateTimeOffset now) =>
        at.ToLocalTime().Date == now.ToLocalTime().Date
            ? at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : FormatTime(at);

    /// <summary>How long an answer has been running: "12 秒", "1 分 05 秒", "1 小时 02 分" (whole seconds, never negative).</summary>
    public static string Elapsed(TimeSpan span)
    {
        var seconds = (long)Math.Max(0, Math.Floor(span.TotalSeconds));
        return seconds < 60 ? string.Format(CultureInfo.InvariantCulture, "{0} 秒", seconds)
            : seconds < 3600 ? string.Format(CultureInfo.InvariantCulture, "{0} 分 {1:00} 秒", seconds / 60, seconds % 60)
            : string.Format(CultureInfo.InvariantCulture, "{0} 小时 {1:00} 分", seconds / 3600, seconds % 3600 / 60);
    }

    /// <summary>A finished answer's total time beside its clock time: "用时 14 秒"; "用时不到 1 秒" for an instant one.</summary>
    public static string AnswerDuration(TimeSpan span) => span < TimeSpan.FromSeconds(1) ? "用时不到 1 秒" : "用时 " + Elapsed(span);

    public static string VersionNumber(int seq) => string.Format(CultureInfo.CurrentCulture, "第 {0} 版", seq);

    public static string VersionRole(bool isCurrent, bool isCandidate) => isCandidate ? "候选" : isCurrent ? "当前" : "历史";

    public static string VersionOrigin(SummaryOrigin origin) => origin == SummaryOrigin.Edited ? "手改" : "生成";

    /// <summary>How far a version covers its conversation and when it was made: "覆盖到第 2 条消息 · 2026年3月14日 09:30".</summary>
    public static string VersionCoverage(SummaryVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return string.Format(CultureInfo.CurrentCulture, "覆盖到第 {0} 条消息 · {1}", version.ToSeq + 1, FormatTime(version.CreatedAt));
    }

    public static string VersionLabel(SummaryVersion version, bool isCurrent, bool isCandidate)
    {
        ArgumentNullException.ThrowIfNull(version);
        return string.Join(" · ", VersionNumber(version.Seq), VersionRole(isCurrent, isCandidate), VersionOrigin(version.Origin), VersionCoverage(version));
    }

    /// <summary>The heading above the editor: which version the edit starts from.</summary>
    public static string EditingVersion(int seq) => string.Format(CultureInfo.CurrentCulture, "编辑第 {0} 版", seq);

    /// <summary>The history's header with the number of versions it lists, the candidate included.</summary>
    public static string HistoryHeadingCount(int count) => string.Format(CultureInfo.CurrentCulture, "版本历史（{0}）", count);

    public static string AdoptReplaces(SummaryVersion current) =>
        string.Format(CultureInfo.CurrentCulture, "候选生成之后，当前版本已变为第 {0} 版（{1}）。采用候选会替换当前版本；第 {0} 版仍保留在历史中。", current.Seq, current.Origin == SummaryOrigin.Edited ? "手改" : "生成");

    public const string DiscardCandidateConfirm = "丢弃候选会删除它的正文和阅读记录，无法恢复。";

    public static string DeleteVersionsConfirm(int count, SummaryVersion? nextCurrent) => nextCurrent is null
        ? string.Format(CultureInfo.CurrentCulture, "将删除全部 {0} 个版本，总结本身也会被删除；原会话和分支保留，以后可以重新总结。删除后无法恢复。", count)
        : string.Format(CultureInfo.CurrentCulture, "将删除 {0} 个版本，删除后无法恢复。当前总结将是第 {1} 版。", count, nextCurrent.Seq);

    public static string Exported(string fileName) => "已导出到 " + fileName + "。";

    public static string ExportRefused(ExportRefusal refusal) => refusal switch
    {
        ExportRefusal.VersionMissing => "这个版本已被删除，没有导出。",
        ExportRefusal.IsCandidate => ExportCandidateHint,
        _ => "无法导出这个版本。",
    };

    public static string ExportFailed(ExportFailure failure, string detail) => failure switch
    {
        ExportFailure.InUse => "文件正被其他程序使用，没有导出。请关闭占用它的程序后重试；原文件未改动。",
        ExportFailure.NotWritable => "无法写入：文件或文件夹是只读的，或没有写入权限。原文件未改动。",
        _ => "导出失败，原文件未改动：" + detail,
    };

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

    /// <summary>The sidebar entry to the archived page, with how many conversations the user archived.</summary>
    public static string ArchivedEntry(int count) => string.Format(CultureInfo.CurrentCulture, "已归档（{0}）", count);

    /// <summary>The read-only note of a conversation hidden because conversations above it are archived.</summary>
    public static string HiddenByAncestors(IReadOnlyList<string> ancestors)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        return "所在的上级会话已归档：" + string.Join(" › ", ancestors) + "。这个会话只能阅读；恢复它会同时恢复这些上级会话。";
    }

    /// <summary>Deep restore (D-021): which archived ancestors come back with it and how many other branches reappear.</summary>
    public static string RestoreConfirm(IReadOnlyList<string> ancestors, int othersReappearing)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        var text = "恢复这个会话会同时恢复它路径上已归档的上级会话：" + string.Join(" › ", ancestors) + "。";
        if (othersReappearing > 0)
        {
            text += string.Format(CultureInfo.CurrentCulture, "另有 {0} 个分支会因此重新出现在侧栏。", othersReappearing);
        }

        return text + "不在这条路径上、单独归档的会话保持归档。";
    }

    public static string ArchiveRefused(IReadOnlyList<string> busy)
    {
        ArgumentNullException.ThrowIfNull(busy);
        return "以下会话还有回答或总结在生成或排队，完成或停止后才能归档：" + string.Join("、", busy) + "。";
    }

    /// <summary>
    /// Every reason a delete was refused, together; archiving is suggested instead only when
    /// <paramref name="offerArchive"/> (not for a conversation that is already archived or hidden).
    /// </summary>
    public static string DeleteRefused(DeleteBlockers blockers, bool offerArchive)
    {
        var reasons = new List<string>(4);
        if (blockers.HasFlag(DeleteBlockers.HasBranches))
        {
            reasons.Add("已经从它创建了分支");
        }

        if (blockers.HasFlag(DeleteBlockers.HasSummary))
        {
            reasons.Add("它有总结（删除总结的全部版本后才能删除会话）");
        }

        if (blockers.HasFlag(DeleteBlockers.AnswerUnfinished))
        {
            reasons.Add("有回答正在生成或排队");
        }

        if (blockers.HasFlag(DeleteBlockers.SummaryUnfinished))
        {
            reasons.Add("有总结正在生成或排队");
        }

        return "不能删除：" + string.Join("；", reasons) + (offerArchive ? "。可以改为归档，归档可以随时恢复。" : "。");
    }

    public static string NodeCreated(DateTimeOffset at) => "生成于 " + FormatTime(at);
}
