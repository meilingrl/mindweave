using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Preferences;
using Mindweave.Core.Runs;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// 设置 › 工具 › 网络搜索 (requirements §10.4.5, decision W14): the service, whether a key is set (the key itself is never shown
/// again), replacing, removing and testing it, and the default of the composer's switch. Greyed out with 工具 when the model
/// cannot call tools or tools are off; a saved key is kept.
/// </summary>
public sealed partial class WebSearchSettingsPanel : ViewModelBase
{
    private readonly IWebSearchSettings _settings;
    private bool _loading = true;

    public WebSearchSettingsPanel(IWebSearchSettings settings)
    {
        _settings = settings;
        ServiceName = settings.Provider.DisplayName;
        Loaded = LoadAsync();
    }

    public Task Loaded { get; }

    public string ServiceName { get; }

    /// <summary>Set by the 工具 section: the model can call tools and tools are on.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyState))]
    [NotifyCanExecuteChangedFor(nameof(RemoveKeyCommand))]
    public partial bool HasKey { get; private set; }

    public string KeyState => HasKey ? UiText.WebSearchKeySet : UiText.WebSearchKeyNotSet;

    /// <summary>A new key being typed; cleared once saved. Never filled from the store.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand))]
    public partial string NewKey { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuto), nameof(IsOn), nameof(IsOff))]
    public partial WebSearchMode DefaultMode { get; private set; }

    public bool IsAuto { get => DefaultMode == WebSearchMode.Auto; set => Choose(value, WebSearchMode.Auto); }

    public bool IsOn { get => DefaultMode == WebSearchMode.On; set => Choose(value, WebSearchMode.On); }

    public bool IsOff { get => DefaultMode == WebSearchMode.Off; set => Choose(value, WebSearchMode.Off); }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    public partial bool IsTesting { get; private set; }

    private bool CanSaveKey() => !string.IsNullOrWhiteSpace(NewKey);

    [RelayCommand(CanExecute = nameof(CanSaveKey))]
    private Task SaveKeyAsync() => RunAsync(async () =>
    {
        await _settings.SetKeyAsync(NewKey, CancellationToken.None);
        NewKey = string.Empty;
        HasKey = true;
        return (UiText.WebSearchKeySaved, false);
    });

    [RelayCommand(CanExecute = nameof(HasKey))]
    private Task RemoveKeyAsync() => RunAsync(async () =>
    {
        await _settings.RemoveKeyAsync(CancellationToken.None);
        HasKey = false;
        return (UiText.WebSearchKeyRemoved, false);
    });

    private bool CanTest() => !IsTesting;

    /// <summary>One real search with the typed key, or the saved one (1 credit).</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        (Status, StatusIsError) = (UiText.WebSearchTesting, false);
        try
        {
            var result = await _settings.TestAsync(string.IsNullOrWhiteSpace(NewKey) ? null : NewKey, CancellationToken.None);
            (Status, StatusIsError) = result.Succeeded ? (UiText.WebSearchTestOk, false) : (result.Problem?.ToString(), true);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void Choose(bool chosen, WebSearchMode mode)
    {
        if (!chosen || DefaultMode == mode)
        {
            return;
        }

        DefaultMode = mode;
        if (!_loading)
        {
            _ = RunAsync(async () =>
            {
                await _settings.SetDefaultModeAsync(mode, CancellationToken.None);
                return (UiText.LimitsSaved, false);
            });
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var state = await _settings.GetAsync(CancellationToken.None);
            HasKey = state.HasKey;
            DefaultMode = state.DefaultMode;
        }
#pragma warning disable CA1031 // A store that cannot be read shows no key; saving says why if it fails too.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RunAsync(Func<Task<(string Message, bool Error)>> action)
    {
        try
        {
            (Status, StatusIsError) = await action();
        }
#pragma warning disable CA1031 // A credential store or preference that fails is said so on the page.
        catch (Exception)
#pragma warning restore CA1031
        {
            (Status, StatusIsError) = (UiText.WebSearchSaveFailed, true);
        }
    }
}
