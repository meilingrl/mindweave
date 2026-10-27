using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
namespace Mindweave.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ContentViewModel
{
    private readonly IChatStore store;
    private readonly ChatConfigurationService service;
    private readonly Func<bool> blocked;
    private readonly Action? persistenceFailed;
    private AiConfiguration? saved;
    [ObservableProperty] private string connectionName = "DeepSeek（可修改）";
    [ObservableProperty] private string baseUrl = "https://api.deepseek.com";
    [ObservableProperty] private string secret = "";
    [ObservableProperty] private string modelId = "deepseek-flash";
    [ObservableProperty] private string tokenLimitParameter = "max_tokens";
    [ObservableProperty] private int contextTokenBudget = 32768;
    [ObservableProperty] private int maxOutputTokens = 2048;
    [ObservableProperty] private SummaryThinkingOption? selectedSummaryThinking;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool isBusy;
    public bool CanSave => !IsBusy && !blocked();
    public bool CanDiscover => !IsBusy && !blocked();
    public ObservableCollection<string> Models { get; } = [];
    public string[] TokenParameters { get; } = ["max_tokens", "max_completion_tokens"];
    public IReadOnlyList<SummaryThinkingOption> SummaryThinkingOptions { get; } = Array.AsReadOnly<SummaryThinkingOption>([
        new(SummaryThinkingMode.ServiceDefault, "服务默认"),
        new(SummaryThinkingMode.Disabled, "关闭（仅DeepSeek官方摘要）")]);
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand DiscoverCommand { get; }
    public SettingsViewModel(IChatStore store, ChatConfigurationService service, Func<bool> blocked, Action? persistenceFailed = null)
    {
        this.store = store; this.service = service; this.blocked = blocked; this.persistenceFailed = persistenceFailed;
        SelectedSummaryThinking = SummaryThinkingOptions[0];
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        DiscoverCommand = new AsyncRelayCommand(DiscoverAsync);
    }
    public async Task InitializeAsync()
    {
        saved = await store.LoadActiveConfigurationAsync();
        if (saved is null) return;
        ConnectionName = saved.Name; BaseUrl = saved.BaseUrl; ModelId = saved.ModelId;
        TokenLimitParameter = saved.TokenLimitParameter; ContextTokenBudget = saved.ContextTokenBudget; MaxOutputTokens = saved.MaxOutputTokens;
        SelectedSummaryThinking = SummaryThinkingOptions.SingleOrDefault(option => option.Mode == saved.SummaryThinking);
    }
    private AiConfiguration Configuration() => new(saved?.ConnectionId ?? Guid.NewGuid(), saved?.ModelConfigId ?? Guid.NewGuid(),
        ConnectionName, BaseUrl, saved?.CredentialReference ?? "", ModelId, ContextTokenBudget, MaxOutputTokens, TokenLimitParameter,
        SelectedSummaryThinking?.Mode ?? throw new ArgumentException("请选择摘要思考模式：服务默认或关闭（仅DeepSeek官方摘要）。"));
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        IsBusy = true; ErrorMessage = StatusMessage = null;
        try
        {
            if (Secret.Length == 0 && saved is null) throw new ArgumentException("首次保存需要输入密钥。已有会话无需密钥即可阅读。");
            await service.SaveAsync(Configuration(), Secret.Length == 0 ? null : Secret);
            saved = await store.LoadActiveConfigurationAsync(); Secret = ""; StatusMessage = "已保存。未自动发现模型或发送测试请求。";
        }
        catch (ArgumentException error) { ErrorMessage = error.Message; }
        catch { ErrorMessage = "配置保存失败，当前输入已保留；请检查系统凭据和存储后重试。"; persistenceFailed?.Invoke(); }
        finally { IsBusy = false; }
    }
    private async Task DiscoverAsync()
    {
        if (!CanDiscover) return;
        IsBusy = true; ErrorMessage = StatusMessage = null;
        try
        {
            var models = await service.DiscoverModelsAsync(Configuration(), Secret.Length == 0 ? null : Secret, default);
            Models.Clear(); foreach (var model in models) Models.Add(model);
            StatusMessage = "模型目录已读取；可手工填写模型 ID。";
        }
        catch (ArgumentException error) { ErrorMessage = error.Message; }
        catch { ErrorMessage = "模型发现失败，可继续手工填写模型 ID。未发送生成请求。"; }
        finally { IsBusy = false; }
    }
    public override Task WaitForPendingOperationsAsync() => Task.WhenAll(new[] { SaveCommand.ExecutionTask, DiscoverCommand.ExecutionTask }.OfType<Task>());
    partial void OnIsBusyChanged(bool value) => RefreshAvailability();
    public void RefreshAvailability() { OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanDiscover)); }
}
