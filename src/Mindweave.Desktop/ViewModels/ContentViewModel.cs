using Mindweave.Application.Navigation;

namespace Mindweave.Desktop.ViewModels;

public class ContentViewModel : ViewModelBase
{
    public NavigationRequest Request { get; private set; } = new(MainArea.Explore);
    public string Eyebrow => Request.Area switch { MainArea.Explore => "EXPLORE", MainArea.Network => "REVISIT", _ => "PREFERENCES" };
    public string Title => Request.Area switch { MainArea.Explore => "从好奇心开始", MainArea.Network => "让探索留下线索", _ => "连接你的 AI 服务" };
    public string Description => Request.Area switch
    {
        MainArea.Explore => "在左侧新建会话，开始提问。每次回答及重试都会保留，切换主题时生成仍会继续。",
        MainArea.Network => "这里将收集你主动保存的总结，帮助你日后找回解释和探索路径。当前主题还没有总结。",
        _ => "真实聊天接入后，可在这里配置兼容的 AI 服务与模型。密钥将由系统凭据服务保管。"
    };
    public string Availability => Request.Area switch
    {
        MainArea.Explore => "新建会话即可开始。发送需已保存 AI 服务配置；旧会话无需密钥即可阅读。",
        MainArea.Network => "总结与网络将在后续阶段开放",
        _ => "连接配置将在聊天阶段开放 · 目前不会发起 AI 请求"
    };
    public virtual Task WaitForPendingOperationsAsync() => Task.CompletedTask;
    public override void SetParameter(NavigationRequest parameter)
    {
        Request = parameter;
        OnPropertyChanged(nameof(Request));
        OnPropertyChanged(nameof(Eyebrow));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Availability));
    }
}
