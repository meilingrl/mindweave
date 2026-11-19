using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Tools.Mcp;
using Mindweave.Core.Runs;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// 设置 › 工具 › MCP 服务器 (requirements §10.4.6): add a server (a local command or an address) after a confirmation that
/// shows exactly what will run or which address is reached; then per server its state and error, 启动／连接, 停止 and 移除,
/// and its tools, each off until enabled, with a risk the user may change. Greyed out with 工具.
/// </summary>
public sealed partial class McpServersPanel : ViewModelBase
{
    private readonly IMcpServers _servers;
    private readonly IMcpConnections _connections;
    private readonly Services.IUiDispatcher? _dispatcher;

    public McpServersPanel(IMcpServers servers, IMcpConnections connections, Services.IUiDispatcher? dispatcher = null)
    {
        _servers = servers;
        _connections = connections;
        _dispatcher = dispatcher;
        _connections.StatusChanged += (_, id) => OnUi(() => Servers.FirstOrDefault(s => s.Id == id)?.Refresh());
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; private set; }

    public ObservableCollection<McpServerItem> Servers { get; } = [];

    public bool HasServers => Servers.Count > 0;

    /// <summary>Set by the 工具 section: the model can call tools and tools are on.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; set; } = true;

    /// <summary>The add form is open (and not yet at the confirmation).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsAdding { get; private set; }

    /// <summary>The confirmation shows what will run; 确认添加 saves it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsConfirming { get; private set; }

    public bool IsIdle => !IsAdding && !IsConfirming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHttp))]
    public partial bool IsCommand { get; set; } = true;

    public bool IsHttp
    {
        get => !IsCommand;
        set => IsCommand = !value;
    }

    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewCommand { get; set; } = string.Empty;

    /// <summary>One argument per line, passed as they are.</summary>
    [ObservableProperty]
    public partial string NewArguments { get; set; } = string.Empty;

    /// <summary>One NAME=value per line.</summary>
    [ObservableProperty]
    public partial string NewEnvironment { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewHeaderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewHeaderValue { get; set; } = string.Empty;

    /// <summary>What the confirmation shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewCommandLines), nameof(PreviewEnvironment), nameof(PreviewHeader), nameof(PreviewIsCommand))]
    public partial McpAddPreview? Preview { get; private set; }

    public bool PreviewIsCommand => Preview?.Command is not null;

    /// <summary>The command and then each argument on its own line, exactly as they will be passed.</summary>
    public string PreviewCommandLines => Preview is { Command: { } command } p ? string.Join("\n", p.Arguments.Prepend(command)) : string.Empty;

    public string PreviewEnvironment => Preview is { EnvironmentNames.Count: > 0 } p ? string.Join("、", p.EnvironmentNames) : UiText.McpNone;

    public string PreviewHeader => Preview?.HeaderName ?? UiText.McpNone;

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    private McpServerDraft? _draft;

    [RelayCommand]
    private void StartAdd()
    {
        (NewName, NewCommand, NewArguments, NewEnvironment, NewUrl, NewHeaderName, NewHeaderValue) = (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        IsCommand = true;
        Status = null;
        IsAdding = true;
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsAdding = false;
        IsConfirming = false;
        _draft = null;
        Preview = null;
    }

    /// <summary>下一步: the confirmation, or why the draft cannot be added.</summary>
    [RelayCommand]
    private void Review()
    {
        var draft = Draft();
        var preview = _servers.Preview(draft);
        if (!preview.CanAdd)
        {
            (Status, StatusIsError) = (preview.Problem, true);
            return;
        }

        _draft = draft;
        Preview = preview;
        Status = null;
        IsAdding = false;
        IsConfirming = true;
    }

    [RelayCommand]
    private void BackToForm()
    {
        IsConfirming = false;
        IsAdding = true;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_draft is null)
        {
            return;
        }

        try
        {
            await _servers.AddAsync(_draft, CancellationToken.None);
            (Status, StatusIsError) = (UiText.McpAdded, false);
            CancelAdd();
            await ReloadAsync();
        }
        catch (ArgumentException ex)
        {
            (Status, StatusIsError) = (ex.Message, true);
        }
#pragma warning disable CA1031 // A store that cannot be written is said so on the page.
        catch (Exception)
#pragma warning restore CA1031
        {
            (Status, StatusIsError) = (UiText.McpSaveFailed, true);
        }
    }

    /// <summary>Reads the servers again (after adding or removing one).</summary>
    public async Task ReloadAsync()
    {
        IReadOnlyList<McpServerConfig> servers;
        try
        {
            servers = await _servers.ListAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Unreadable settings show no servers.
        catch (Exception)
#pragma warning restore CA1031
        {
            servers = [];
        }

        Servers.Clear();
        foreach (var server in servers)
        {
            Servers.Add(new McpServerItem(server, _servers, _connections, this));
        }

        OnPropertyChanged(nameof(HasServers));
    }

    internal void OnUi(Action action)
    {
        if (_dispatcher is null)
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }

    private McpServerDraft Draft()
    {
        if (IsCommand)
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Lines(NewEnvironment))
            {
                var cut = line.IndexOf('=', StringComparison.Ordinal);
                environment[cut > 0 ? line[..cut].Trim() : line.Trim()] = cut > 0 ? line[(cut + 1)..] : string.Empty;
            }

            return new McpServerDraft(NewName, McpServerKind.Command, NewCommand, [.. Lines(NewArguments)], environment);
        }

        return new McpServerDraft(NewName, McpServerKind.Http, Url: NewUrl, HeaderName: NewHeaderName, HeaderValue: string.IsNullOrEmpty(NewHeaderValue) ? null : NewHeaderValue);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0);
}

/// <summary>One server in 设置 › 工具 › MCP 服务器: its state, error, recent output and tools.</summary>
public sealed partial class McpServerItem : ViewModelBase
{
    private readonly IMcpServers _servers;
    private readonly IMcpConnections _connections;
    private readonly McpServersPanel _panel;

    public McpServerItem(McpServerConfig server, IMcpServers servers, IMcpConnections connections, McpServersPanel panel)
    {
        Server = server;
        _servers = servers;
        _connections = connections;
        _panel = panel;
        Refresh();
    }

    public McpServerConfig Server { get; private set; }

    public string Id => Server.Id;

    public string Name => Server.Name;

    public string Kind => Server.Kind == McpServerKind.Command ? UiText.McpKindCommand : UiText.McpKindHttp;

    /// <summary>What runs or which address: the command line as one line (each argument quoted when it has spaces), or the address.</summary>
    public string Target => Server.Kind == McpServerKind.Command
        ? string.Join(" ", Server.Arguments.Prepend(Server.Command ?? string.Empty).Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a))
        : Server.Url?.OriginalString ?? string.Empty;

    public string StartLabel => Server.Kind == McpServerKind.Command ? UiText.McpStart : UiText.McpConnect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsProblem))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial McpServerState State { get; private set; }

    public string StateText => UiText.McpState(State);

    public bool IsProblem => State is McpServerState.Failed or McpServerState.Halted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStandardError))]
    public partial string StandardError { get; private set; } = string.Empty;

    public bool HasStandardError => IsProblem && StandardError.Length > 0;

    public ObservableCollection<McpToolItem> Tools { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoListing))]
    public partial bool IsListed { get; private set; }

    public bool HasNoListing => !IsListed && Tools.Count == 0;

    [ObservableProperty]
    public partial string ToolCount { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConfirmingRemove { get; private set; }

    /// <summary>Reads the state and tools again (on a status change, or after a tool's setting changed).</summary>
    public void Refresh()
    {
        var status = _connections.StatusOf(Server.Id);
        State = status.State;
        Error = status.Error;
        StandardError = string.Join("\n", status.StandardError);
        _ = ReloadToolsAsync(status.Tools);
    }

    private bool CanStart() => State is not McpServerState.Starting and not McpServerState.Connected;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        try
        {
            await _connections.StartAsync(Server.Id, CancellationToken.None);
        }
        catch (McpServerException)
        {
            // The status says why.
        }

        _panel.OnUi(Refresh);
    }

    private bool CanStop() => State is McpServerState.Connected or McpServerState.Starting;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await _connections.StopAsync(Server.Id);
        _panel.OnUi(Refresh);
    }

    [RelayCommand]
    private void AskRemove() => IsConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand]
    private async Task RemoveAsync()
    {
        await _connections.RemoveAsync(Server.Id, CancellationToken.None);
        await _panel.ReloadAsync();
    }

    internal async Task EnableAsync(McpToolInfo tool, bool enabled)
    {
        try
        {
            if (enabled)
            {
                await _servers.EnableToolAsync(Server.Id, tool, CancellationToken.None);
            }
            else
            {
                await _servers.DisableToolAsync(Server.Id, tool.Name, CancellationToken.None);
            }
        }
#pragma warning disable CA1031 // A setting that could not be saved shows as it was after the refresh.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _panel.OnUi(Refresh);
    }

    internal async Task SetRiskAsync(string tool, ToolRisk risk)
    {
        try
        {
            await _servers.SetRiskAsync(Server.Id, tool, risk, CancellationToken.None);
        }
#pragma warning disable CA1031 // As above.
        catch (Exception)
#pragma warning restore CA1031
        {
            _panel.OnUi(Refresh);
        }
    }

    /// <summary>The tools listed by the running server, joined with the stored settings (a tool turned off with a reason stays visible).</summary>
    private async Task ReloadToolsAsync(IReadOnlyList<McpToolInfo>? listed)
    {
        try
        {
            Server = await _servers.GetAsync(Server.Id, CancellationToken.None) ?? Server;
        }
#pragma warning disable CA1031 // The last known settings are shown.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _panel.OnUi(() =>
        {
            var tools = (listed ?? []).Select(t => (Info: t, Setting: Server.Tool(t.Name))).ToList();
            tools.AddRange(Server.Tools.Where(s => listed?.Any(t => t.Name == s.Name) != true).Select(s => (s.Definition, (McpToolSetting?)s)));
            Tools.Clear();
            foreach (var (info, setting) in tools.OrderBy(t => t.Info.Name, StringComparer.Ordinal))
            {
                Tools.Add(new McpToolItem(info, setting, this));
            }

            IsListed = listed is not null;
            ToolCount = UiText.McpToolCount(Server.Tools.Count(t => t.Enabled), listed?.Count ?? 0);
            OnPropertyChanged(nameof(HasNoListing));
        });
    }
}

/// <summary>One tool of a server: its name, description, whether it declares itself read-only, on or off, and its risk.</summary>
public sealed partial class McpToolItem : ViewModelBase
{
    private readonly McpServerItem _server;
    private readonly bool _loading;

    public McpToolItem(McpToolInfo tool, McpToolSetting? setting, McpServerItem server)
    {
        Tool = tool;
        _server = server;
        _loading = true;
        IsOn = setting?.Enabled == true;
        RiskIndex = (int)(setting?.Risk ?? tool.DefaultRisk);
        DisabledReason = setting is { Enabled: false } ? setting.DisabledReason : null;
        _loading = false;
    }

    public McpToolInfo Tool { get; }

    public string Name => Tool.Name;

    public string Description => (Tool.Description ?? Tool.Title ?? string.Empty).ReplaceLineEndings(" ");

    public bool HasDescription => Description.Length > 0;

    public bool DeclaresReadOnly => Tool.DeclaresReadOnly;

    public static IReadOnlyList<string> Risks => UiText.McpRisks;

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial int RiskIndex { get; set; }

    /// <summary>Why it was turned off by itself (decision M6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisabledReason))]
    public partial string? DisabledReason { get; private set; }

    public bool HasDisabledReason => DisabledReason is not null;

    partial void OnIsOnChanged(bool value)
    {
        if (!_loading)
        {
            DisabledReason = null;
            _ = _server.EnableAsync(Tool, value);
        }
    }

    partial void OnRiskIndexChanged(int value)
    {
        if (!_loading && IsOn && value is >= 0 and <= 2)
        {
            _ = _server.SetRiskAsync(Tool.Name, (ToolRisk)value);
        }
    }
}
