using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;
public sealed partial class ChatViewModel
{
    private KnowledgeSummaryCoordinator? summaries;
    private ISummaryVersionStore? summaryStore;
    private bool hasCandidate, acceptingSummary;
    public string SummaryScopeNotice => "总结当前会话：覆盖接受时已保存的本会话内容及既有来源，不包含之后新增的内容。会单独请求 AI，可能产生费用；完整输入超出预算时会拒绝，不会截断。";
    public string SummaryAvailabilityNotice => HasActiveReply ? "请先等待或停止当前回答，再生成总结。" :
        hasCandidate ? "已有候选总结，请先在网络中处理该候选版本。" :
        blocked() ? "请先完成保存或重试保存。" :
        !CanGenerateSummary ? "知识总结正在处理，可继续阅读和聊天。" : "完成后可在网络中查看，当前页面不会自动跳转。";
    public bool HasSummaryService => summaries is not null;
    public bool CanGenerateSummary => HasSummaryService && CanSend && !acceptingSummary && !hasCandidate &&
        !summaries!.Snapshots.Any(s => s.ConversationId == ConversationId && s.Status is GenerationStatus.Queued or GenerationStatus.Running);
    public IAsyncRelayCommand GenerateSummaryCommand { get; private set; } = new AsyncRelayCommand(() => Task.CompletedTask);
    public void ConfigureSummaries(KnowledgeSummaryCoordinator service, ISummaryVersionStore versions)
    {
        summaries = service; summaryStore = versions;
        GenerateSummaryCommand = new AsyncRelayCommand(async () =>
        {
            if (!CanGenerateSummary) return;
            acceptingSummary = true; RefreshSummaryAvailability();
            try
            {
                var result = await service.AcceptAsync(ConversationId);
                ErrorMessage = result.Kind is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange
                    ? "总结已接受，完成后可在网络中查看；可继续当前会话。"
                    : result.Code switch
                    {
                        "MissingAiConfiguration" => "请先在设置保存 AI 服务配置。",
                        _ => "目前不能生成总结：请等待或停止当前回答，处理候选版本，并检查完整输入预算与配置。"
                    };
                await RefreshSummaryAsync();
            }
            catch (InvalidOperationException error) { ErrorMessage = error.Message; }
            catch { ErrorMessage = "总结接受状态尚未确认，请重试保存；不要重复发起。"; persistenceFailed(); }
            finally { acceptingSummary = false; RefreshSummaryAvailability(); }
        });
        OnPropertyChanged(nameof(GenerateSummaryCommand)); RefreshSummaryAvailability();
    }
    public async Task RefreshSummaryAsync()
    {
        if (summaryStore is null) return;
        hasCandidate = (await summaryStore.FindConversationSummaryAsync(ConversationId))?.State.Node.CandidateVersionId is not null;
        RefreshSummaryAvailability();
    }
    private void RefreshSummaryAvailability() { OnPropertyChanged(nameof(HasSummaryService)); OnPropertyChanged(nameof(CanGenerateSummary)); OnPropertyChanged(nameof(SummaryAvailabilityNotice)); }
}
