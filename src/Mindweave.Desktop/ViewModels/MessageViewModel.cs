using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Core.Chat;
using Mindweave.Application.Chat;
namespace Mindweave.Desktop.ViewModels;

public sealed partial class MessageViewModel(ChatMessage message) : ViewModelBase
{
    public Guid Id { get; } = message.Id;
    public string RoleLabel => message.Role == MessageRole.User ? "你" : message.SupersedesMessageId is null ? "AI" : "AI · 新尝试（旧尝试保留）";
    [ObservableProperty] private string body = message.Body;
    [ObservableProperty] private GenerationStatus status = message.Status;
    [ObservableProperty] private string? errorCode;
    public bool IsIncomplete => message.Role == MessageRole.Assistant && Status != GenerationStatus.Completed;
    public string StatusLabel => message.Role == MessageRole.User ? "" : Status switch
    {
        GenerationStatus.Queued => "排队中", GenerationStatus.Running => "正在生成 · 尚未完整",
        GenerationStatus.Completed => "已完成", GenerationStatus.Stopped => "已停止 · 内容不完整",
        GenerationStatus.Interrupted => "已中断 · 内容不完整", _ => "失败 · 内容不完整"
    } + (ErrorCode is null ? "" : $" · {ErrorCode}");
    partial void OnStatusChanged(GenerationStatus value) { OnPropertyChanged(nameof(IsIncomplete)); OnPropertyChanged(nameof(StatusLabel)); }
    partial void OnErrorCodeChanged(string? value) => OnPropertyChanged(nameof(StatusLabel));
    public void Project(GenerationSnapshot snapshot) { Body = snapshot.Body; Status = snapshot.Status; ErrorCode = snapshot.Error; }
}
