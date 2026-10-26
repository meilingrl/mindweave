using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Core.Chat;
using Mindweave.Application.Chat;
namespace Mindweave.Desktop.ViewModels;

public sealed partial class MessageViewModel(ChatMessage message) : ViewModelBase
{
    public System.Collections.ObjectModel.ObservableCollection<BranchBookmark> Branches { get; } = [];
    public MessageRole Role => message.Role;
    [ObservableProperty] private long revision = message.Revision;
    [ObservableProperty] private bool hasUnsavedChanges;
    public bool CanCreateBranch => Role == MessageRole.Assistant && Status is (GenerationStatus.Completed or GenerationStatus.Stopped or GenerationStatus.Failed or GenerationStatus.Interrupted) && !HasUnsavedChanges && !string.IsNullOrWhiteSpace(Body);
    public Guid Id { get; } = message.Id;
    public string RoleLabel => message.Role == MessageRole.User ? "你" : message.SupersedesMessageId is null ? "AI" : "AI · 新尝试（旧尝试保留）";
    [ObservableProperty] private string body = message.Body;
    [ObservableProperty] private GenerationStatus status = message.Status;
    [ObservableProperty] private string? errorCode;
    public bool IsIncomplete => message.Role == MessageRole.Assistant && Status != GenerationStatus.Completed;
    private GenerationPhase phase;
    private int completedChunks, totalChunks, storedSummaryCount;
    private bool metadataUnavailable;
    public string ContextNotice => Role != MessageRole.Assistant ? "" :
        phase == GenerationPhase.Compressing && Status is (GenerationStatus.Failed or GenerationStatus.Stopped or GenerationStatus.Interrupted)
            ? ErrorCode == "ContextBudgetExceeded"
                ? "上下文压缩后仍超出预算，尚未请求回答。原记录和问题保留；可重试、缩小来源或调整预算。"
                : "上下文准备未完成，尚未请求回答。原记录和问题保留；可重试、缩小来源或调整预算。"
            : !string.IsNullOrWhiteSpace(Body) && (storedSummaryCount > 0 || (phase == GenerationPhase.Reply && totalChunks > 0 && completedChunks == totalChunks))
                ? "较早对话已压缩，原记录保留"
                : metadataUnavailable ? "上下文使用信息暂不可用，原记录仍可阅读。" : "";
    public bool HasContextNotice => ContextNotice.Length > 0;
    private void ContextChanged() { OnPropertyChanged(nameof(ContextNotice)); OnPropertyChanged(nameof(HasContextNotice)); OnPropertyChanged(nameof(StatusLabel)); }
    public void ProjectContextUsage(ContextUsageMetadata? usage, bool unavailable = false)
    {
        metadataUnavailable = unavailable;
        storedSummaryCount = usage?.SummaryCount ?? 0;
        if (usage is not null)
        {
            if (usage.PlannedSummaryCount > 0 && usage.SummaryCount == 0) phase = GenerationPhase.Compressing;
            ErrorCode = usage.ErrorCode;
        }
        ContextChanged();
    }
    public string StatusLabel => message.Role == MessageRole.User ? "" : phase == GenerationPhase.Compressing && Status is (GenerationStatus.Queued or GenerationStatus.Running)
        ? $"准备上下文 · {completedChunks}/{totalChunks} · 可停止" : Status switch
    {
        GenerationStatus.Queued => "排队中", GenerationStatus.Running => "正在生成 · 尚未完整",
        GenerationStatus.Completed => "已完成", GenerationStatus.Stopped => "已停止 · 内容不完整",
        GenerationStatus.Interrupted => "已中断 · 内容不完整", _ => "失败 · 内容不完整"
    } + (ErrorCode is null ? "" : $" · {ErrorCode}");
    partial void OnStatusChanged(GenerationStatus value) { OnPropertyChanged(nameof(IsIncomplete)); OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(CanCreateBranch)); ContextChanged(); }
    partial void OnErrorCodeChanged(string? value) => ContextChanged();
    partial void OnBodyChanged(string value) { OnPropertyChanged(nameof(CanCreateBranch)); ContextChanged(); }
    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(CanCreateBranch));
    public void Project(GenerationSnapshot snapshot) { phase = snapshot.Phase; completedChunks = snapshot.CompletedContextChunks; totalChunks = snapshot.TotalContextChunks; Revision = snapshot.Revision; HasUnsavedChanges = snapshot.HasUnsavedChanges; Body = snapshot.Body; Status = snapshot.Status; ErrorCode = snapshot.Error; ContextChanged(); }
}
