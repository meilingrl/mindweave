using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application;
using Mindweave.Application.Startup;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>Workspace entry page: start a conversation or pick one from the sidebar.</summary>
public sealed partial class ExploreHomeViewModel(Workspace workspace, IContentNavigator content) : PageViewModel(ExploreHomeRoute.Instance)
{
    public Workspace Workspace { get; } = workspace;

    public override string Title => Workspace.Name.Value;

    [RelayCommand]
    private void NewConversation() => content.Push(NewConversationRoute.Instance);
}

/// <summary>One summary in the workspace list: its display name, markers and source conversation.</summary>
public sealed record SummaryListItem(SummaryNodeId Node, string DisplayName, string Markers, bool HasMarkers);

/// <summary>
/// Summary network section. Until the graph view (S5) it lists the workspace's summaries in creation order with
/// their markers; opening one shows its page. Selecting a row never marks it read.
/// </summary>
public sealed partial class NetworkHomeViewModel : PageViewModel, IDisposable
{
    private readonly SummaryService _summaries;
    private readonly IContentNavigator _content;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private bool _disposed;

    public NetworkHomeViewModel(Workspace workspace, SummaryService summaries, IContentNavigator content, IUiDispatcher dispatcher)
        : base(NetworkHomeRoute.Instance)
    {
        Workspace = workspace;
        _summaries = summaries;
        _content = content;
        _load = new SerialRefresh(LoadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());
        _summaries.Changed += OnChanged;
        Loaded = _load.Request();
    }

    public Workspace Workspace { get; }

    public override string Title => UiText.NetworkTitle;

    public ObservableCollection<SummaryListItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    internal Task Loaded { get; }

    [RelayCommand]
    private void Open(SummaryNodeId node) => _content.Push(new SummaryRoute(node));

    public void Dispose()
    {
        _disposed = true;
        _summaries.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, Core.Conversations.ConversationId e)
    {
        if (!_disposed)
        {
            _reload.Request();
        }
    }

    private async Task LoadAsync()
    {
        var entries = await _summaries.ListAsync(Workspace.Id, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        Items.Clear();
        foreach (var entry in entries)
        {
            var markers = UiText.Markers(entry.Markers);
            Items.Add(new SummaryListItem(entry.Node.Id, entry.Node.DisplayName, markers, markers.Length > 0));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}

public sealed partial class SettingsHomeViewModel(AppEnvironment environment, ModelSettingsViewModel model, IContentNavigator content) : PageViewModel(SettingsHomeRoute.Instance)
{
    public AppEnvironment Environment { get; } = environment;

    public ModelSettingsViewModel Model { get; } = model;

    public override string Title => UiText.SettingsTitle;

    [RelayCommand]
    private void ShowStorageDetails() => content.Push(StorageDetailsRoute.Instance);
}

/// <summary>Where data lives and which migration backups exist. Read-only; restoring is offered only on the recovery page.</summary>
public sealed partial class StorageDetailsViewModel : PageViewModel
{
    private readonly ISystemShell _shell;

    public StorageDetailsViewModel(AppEnvironment environment, IReadOnlyList<BackupInfo> backups, ISystemShell shell)
        : base(StorageDetailsRoute.Instance)
    {
        Environment = environment;
        Backups = new ObservableCollection<BackupInfo>(backups);
        _shell = shell;
    }

    public AppEnvironment Environment { get; }

    public ObservableCollection<BackupInfo> Backups { get; }

    public bool HasBackups => Backups.Count > 0;

    public override string Title => UiText.StorageTitle;

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(Environment.DataDirectory);
}
