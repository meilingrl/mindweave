using Mindweave.Application.Navigation;

namespace Mindweave.Desktop.ViewModels;

public sealed class ContentViewModel : ViewModelBase
{
    public NavigationRequest Request { get; private set; } = new(MainArea.Explore);
    public string Eyebrow => Request.Area switch { MainArea.Explore => "EXPLORE", MainArea.Network => "REVISIT", _ => "PREFERENCES" };
    public string Title => Request.Area switch { MainArea.Explore => "从好奇心开始", MainArea.Network => "让探索留下线索", _ => "连接你的 AI 服务" };
    public string Description => Request.Area switch
    {
        MainArea.Explore => "主题工作区已准备好。接下来，你可以在这里提问，从回答中岔开，再回到原来的阅读位置。",
        MainArea.Network => "这里将收集你主动保存的总结，帮助你日后找回解释和探索路径。当前主题还没有总结。",
        _ => "真实聊天接入后，可在这里配置兼容的 AI 服务与模型。密钥将由系统凭据服务保管。"
    };
    public string Availability => Request.Area switch
    {
        MainArea.Explore => "聊天功能正在开发中 · 当前可创建、切换并保存主题",
        MainArea.Network => "总结与网络将在后续阶段开放",
        _ => "连接配置将在聊天阶段开放 · 目前不会发起 AI 请求"
    };
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
