using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Core.Models;
using Mindweave.Core.Runs;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>
/// The web search switch beside the composer (requirements §10.4.5): 自动／开／关, starting at the default in settings each time
/// the page opens (decision W3). Greyed out with the reason when the model cannot call tools, tools are turned off, or no
/// search key is set; the question then goes as 关.
/// </summary>
public sealed partial class WebSearchSwitch : ViewModelBase, IDisposable
{
    private readonly IWebSearchSettings _settings;
    private readonly IToolSettings? _tools;
    private readonly IModelSettings? _models;
    private readonly Services.IUiDispatcher? _dispatcher;
    private bool _chosen;

    public WebSearchSwitch(IWebSearchSettings settings, IToolSettings? tools, IModelSettings? models, Services.IUiDispatcher? dispatcher = null)
    {
        _settings = settings;
        _tools = tools;
        _models = models;
        _dispatcher = dispatcher;
        _settings.Changed += OnSettingsChanged;
        Loaded = RefreshAsync();
    }

    /// <summary>Completes when the first state is shown.</summary>
    public Task Loaded { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip), nameof(IsOn), nameof(IsAuto), nameof(IsOff), nameof(ShowsOff))]
    public partial WebSearchMode Mode { get; private set; } = WebSearchMode.Auto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip), nameof(IsOn), nameof(ShowsOff))]
    public partial bool IsAvailable { get; private set; }

    /// <summary>Why the switch is greyed out; null while it can be used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    public partial string? UnavailableReason { get; private set; }

    public string Tip => UnavailableReason ?? UiText.WebSearchModeTip(Mode);

    /// <summary>On (开) and usable: the button is drawn as active.</summary>
    public bool IsOn => IsAvailable && Mode == WebSearchMode.On;

    /// <summary>关, or not usable: the globe is drawn muted.</summary>
    public bool ShowsOff => !IsAvailable || Mode == WebSearchMode.Off;

    public bool IsAuto => Mode == WebSearchMode.Auto;

    public bool IsOff => Mode == WebSearchMode.Off;

    /// <summary>What goes with the next question: the chosen position, or 关 while the switch cannot be used.</summary>
    public SendOptions Options => new(IsAvailable ? Mode : WebSearchMode.Off);

    [RelayCommand]
    private void Choose(WebSearchMode mode)
    {
        _chosen = true;
        Mode = mode;
    }

    /// <summary>Reads the model, the tool switch and the key again (after settings changed).</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var state = await _settings.GetAsync(CancellationToken.None);
            var calls = _models is null || await _models.ActiveModelHasAsync(ModelCapability.ToolCalls, CancellationToken.None);
            var enabled = _tools is null || await _tools.GetEnabledAsync(CancellationToken.None);
            if (!_chosen)
            {
                Mode = state.DefaultMode;
            }

            UnavailableReason = !calls ? UiText.ToolsUnavailable : !enabled ? UiText.WebSearchToolsOff : !state.HasKey ? UiText.WebSearchNoKey : null;
            IsAvailable = UnavailableReason is null;
        }
#pragma warning disable CA1031 // A settings read that fails leaves the switch off; asking still works.
        catch (Exception)
#pragma warning restore CA1031
        {
            IsAvailable = false;
        }
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is not null)
        {
            _dispatcher.Post(() => _ = RefreshAsync());
        }
        else
        {
            _ = RefreshAsync();
        }
    }
}
