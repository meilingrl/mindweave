using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;
public sealed partial class ChatViewModel
{
    private KnowledgeSummaryCoordinator? summaries;
    private ISummaryVersionStore? summaryStore;
    private KnowledgeSummarySnapshot? displayedSummary;
    private bool hasCandidate, acceptingSummary, summaryFeedbackNeedsRepair;
    private Func<bool>? summaryInteractionAllowed;
    private long summaryRefreshStamp;
    private string summaryFeedback = "";
    public string SummaryScopeNotice => "总结当前会话：覆盖接受时已保存的本会话内容及既有来源，不包含之后新增的内容。会单独请求 AI，可能产生费用；完整输入超出预算时会拒绝，不会截断。";
    public string SummaryAvailabilityNotice => HasActiveReply ? "请先等待或停止当前回答，再生成总结。" :
        hasCandidate ? "已有候选总结，请先在网络中处理该候选版本。" :
        blocked() ? "请先完成保存或重试保存。" :
        !CanGenerateSummary ? "知识总结正在处理，可继续阅读和聊天。" : "完成后可在网络中查看，当前页面不会自动跳转。";
    public string SummaryAcceptanceNotice => summaryFeedback;
    public string SummaryStatusNotice => displayedSummary is not { } task ? "" :
        task.RetainedContent is not null ? "总结响应已完整接收，尚未确认保存；请查看工作台保留结果并重试保存，不要重新生成。" :
        DescribeSummaryStatus(task) + (task.HasUnsavedChanges ? " 状态尚未确认保存，请重试保存；不要重新生成。" :
            CanGenerateSummary && task.Status is (GenerationStatus.Failed or GenerationStatus.Stopped or GenerationStatus.Interrupted) ? " 需要时可点击“总结当前会话”重新生成。" : "");
    private static string DescribeSummaryStatus(KnowledgeSummarySnapshot task) => task.Status switch
    {
        GenerationStatus.Queued => "总结已排队，可停止此总结；切换页面不会取消。",
        GenerationStatus.Running => "总结正在生成，可停止此总结；当前会话可以继续。",
        GenerationStatus.Completed => "总结已完成并保存，可在总结网络中查看。",
        GenerationStatus.Stopped => "总结已停止，没有保存不完整版本。",
        GenerationStatus.Interrupted => "总结已中断，没有自动重新发起。",
        GenerationStatus.Failed => task.Error switch
        {
            "CredentialUnavailable" => "总结失败：无法读取服务凭据。请检查设置。",
            "InvalidSummaryResponse" => "总结失败：服务未返回完整有效的总结（可能截断或格式无效）。请检查模型与输出限制。",
            "SummaryTransportFailed" => "总结失败：服务连接或响应处理异常。请检查连接。",
            _ => "总结失败：本次结果无法完成。请检查配置与会话状态。"
        },
        _ => "总结状态待确认，请先重试保存。"
    };
    public bool HasSummaryService => summaries is not null;
    public bool CanGenerateSummary => HasSummaryService && CanSend && !acceptingSummary && !hasCandidate &&
        !summaries!.Snapshots.Any(s => s.ConversationId == ConversationId && (s.HasUnsavedChanges || s.Status is GenerationStatus.Queued or GenerationStatus.Running));
    public bool CanStopSummary => (summaryInteractionAllowed?.Invoke() ?? !blocked()) && displayedSummary is { RetainedContent: null, Status: GenerationStatus.Queued or GenerationStatus.Running };
    public IAsyncRelayCommand GenerateSummaryCommand { get; private set; } = new AsyncRelayCommand(() => Task.CompletedTask);
    public IAsyncRelayCommand StopSummaryCommand { get; private set; } = new AsyncRelayCommand(() => Task.CompletedTask);
    public void ConfigureSummaries(KnowledgeSummaryCoordinator service, ISummaryVersionStore versions, Func<bool>? canInteract = null)
    {
        summaries = service; summaryStore = versions; summaryInteractionAllowed = canInteract;
        StopSummaryCommand = new AsyncRelayCommand(async () =>
        {
            if (!CanStopSummary || displayedSummary is not { } shown) return;
            // Freeze the identity the control displayed before the first await. Never stop a Reply or a later Summary.
            try { await service.StopAsync(shown.TaskId); }
            catch { summaryFeedback = "停止后的总结状态尚未确认保存，请重试保存。"; summaryFeedbackNeedsRepair = true; persistenceFailed(); }
            finally { ProjectSummaryStatus(); }
        });
        GenerateSummaryCommand = new AsyncRelayCommand(async () =>
        {
            if (!CanGenerateSummary) return;
            acceptingSummary = true; summaryFeedbackNeedsRepair = false; summaryFeedback = "正在接受总结请求。"; RefreshSummaryAvailability();
            try
            {
                var result = await service.AcceptAsync(ConversationId);
                summaryFeedback = result.Kind is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange
                    ? ""
                    : result.Code switch
                    {
                        "MissingAiConfiguration" => "请先在设置保存 AI 服务配置。",
                        _ => "目前不能生成总结：请等待或停止当前回答，处理候选版本，并检查完整输入预算与配置。"
                    };
                await RefreshSummaryAsync();
            }
            catch (InvalidOperationException) { summaryFeedback = "目前不能生成总结，请先检查保存状态与当前任务。"; }
            catch { summaryFeedback = "总结接受状态尚未确认，请重试保存；不要重复发起。"; summaryFeedbackNeedsRepair = true; persistenceFailed(); }
            finally { acceptingSummary = false; ProjectSummaryStatus(); }
        });
        OnPropertyChanged(nameof(GenerateSummaryCommand)); OnPropertyChanged(nameof(StopSummaryCommand)); ProjectSummaryStatus();
    }
    public async Task RefreshSummaryAsync()
    {
        ProjectSummaryStatus(); // Memory status remains visible even if the optional candidate read fails.
        if (summaryStore is null) return;
        var stamp = ++summaryRefreshStamp;
        var node = await summaryStore.FindConversationSummaryAsync(ConversationId);
        if (stamp != summaryRefreshStamp) return;
        hasCandidate = node?.State.Node.CandidateVersionId is not null;
        ProjectSummaryStatus();
    }
    private void ProjectSummaryStatus()
    {
        // Coordinator order is acceptance order; at most one active Summary belongs to a conversation.
        displayedSummary = summaries?.Snapshots.LastOrDefault(s => s.ConversationId == ConversationId);
        if (summaryFeedbackNeedsRepair && summaries is { HasPersistenceFailure: false })
        { summaryFeedbackNeedsRepair = false; summaryFeedback = ""; }
        RefreshSummaryAvailability();
    }
    private void RefreshSummaryAvailability()
    {
        OnPropertyChanged(nameof(HasSummaryService)); OnPropertyChanged(nameof(CanGenerateSummary));
        OnPropertyChanged(nameof(SummaryAcceptanceNotice)); OnPropertyChanged(nameof(CanStopSummary)); OnPropertyChanged(nameof(SummaryStatusNotice)); OnPropertyChanged(nameof(SummaryAvailabilityNotice));
    }
}
