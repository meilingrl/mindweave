using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Summaries;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Network;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>One summary in the list view: display name and markers in words.</summary>
public sealed record SummaryListItem(SummaryNodeId Node, string DisplayName, string Markers, bool HasMarkers);

/// <summary>A node as the canvas draws it, in canvas coordinates.</summary>
public sealed record NodeView(SummaryNodeId Id, string ShortTitle, string DisplayName, double X, double Y, bool Unread, bool HasCandidate, double LabelWidth);

/// <summary>A source edge as the canvas draws it; dashed with a label when it passes unsummarized conversations.</summary>
public sealed record EdgeView(SummaryNodeId From, SummaryNodeId To, double FromX, double FromY, double ToX, double ToY, bool IsDirect, string? Label);

/// <summary>What the side panel says about the selected node; never the summary body, never marks it read.</summary>
public sealed record NodeDetail(SummaryNodeId Id, ConversationId Conversation, string DisplayName, string ShortTitle, string Markers, string Source, string Created, string? Via);

/// <summary>
/// The summary network of the current workspace (requirement §7.3–7.4, architecture §11): nodes with short titles
/// and real source edges, selection with details, search, show all, zoom, explicit arrange and save. State lives in
/// the workspace's <see cref="NetworkSession"/>, so the viewport and selection survive reading a summary and coming
/// back. A list view of the same summaries stays available for keyboard and screen-reader use.
/// </summary>
public sealed partial class NetworkHomeViewModel : PageViewModel, IDisposable
{
    private readonly SummaryService _summaries;
    private readonly ConversationService _conversations;
    private readonly IWorkbenchNavigator _navigator;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private Dictionary<ConversationId, string> _titles = [];
    private bool _disposed;

    public NetworkHomeViewModel(Workspace workspace, NetworkSession session, SummaryService summaries, ConversationService conversations, IWorkbenchNavigator navigator, IUiDispatcher dispatcher)
        : base(NetworkHomeRoute.Instance)
    {
        Workspace = workspace;
        Session = session;
        _summaries = summaries;
        _conversations = conversations;
        _navigator = navigator;
        _load = new SerialRefresh(LoadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());
        _summaries.Changed += OnChanged;
        Session.PropertyChanged += OnSessionChanged;
        Session.Discarded += OnDiscarded;
        Loaded = _load.Request();
    }

    public Workspace Workspace { get; }

    public NetworkSession Session { get; }

    public override string Title => UiText.NetworkTitle;

    internal Task Loaded { get; }

    public ObservableCollection<SummaryListItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public IReadOnlyList<NodeView> Nodes { get; private set; } = [];

    public IReadOnlyList<EdgeView> Edges { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsListView { get; set; }

    [ObservableProperty]
    public partial bool WheelZooms { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial NodeDetail? Selected { get; private set; }

    public bool IsDirty => Session.IsDirty;

    public bool IsSaving => Session.IsSaving;

    public bool SaveFailed => Session.SaveFailed;

    /// <summary>The canvas redraws; <c>true</c> when the viewport was reset and should fit everything again.</summary>
    public event EventHandler<bool>? ViewChanged;

    /// <summary>The canvas zooms around its centre by this factor (the + / − buttons).</summary>
    public event EventHandler<double>? ZoomRequested;

    /// <summary>Single click: select and show details. Not reading: the summary's read state does not change.</summary>
    public void Select(SummaryNodeId? node)
    {
        Session.Selected = node;
        Selected = node is { } id ? Detail(id) : null;
        ViewChanged?.Invoke(this, false);
    }

    /// <summary>Double click or "open summary".</summary>
    [RelayCommand]
    private void Open(SummaryNodeId node) => _navigator.Navigate(new NavigationRequest(MainSection.Network, new SummaryRoute(node)));

    [RelayCommand]
    private void OpenSelected()
    {
        if (Selected is { } detail)
        {
            Open(detail.Id);
        }
    }

    [RelayCommand]
    private void ReturnToConversation()
    {
        if (Selected is { } detail)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(detail.Conversation)));
        }
    }

    /// <summary>Title search: selects the first match and brings it to the centre.</summary>
    [RelayCommand]
    private void Find()
    {
        var text = SearchText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var match = Nodes.FirstOrDefault(n => n.ShortTitle.Contains(text, StringComparison.CurrentCultureIgnoreCase) || n.DisplayName.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        if (match is null)
        {
            return;
        }

        Session.Viewport = new Viewport(match.X, match.Y, Session.Viewport?.Zoom ?? 1);
        Select(match.Id);
    }

    /// <summary>Only the viewport changes; no node moves.</summary>
    [RelayCommand]
    private void ShowAll()
    {
        Session.Viewport = null;
        ViewChanged?.Invoke(this, true);
    }

    [RelayCommand]
    private void ZoomIn() => ZoomRequested?.Invoke(this, 1.25);

    [RelayCommand]
    private void ZoomOut() => ZoomRequested?.Invoke(this, 0.8);

    /// <summary>Explicit re-layout: kept in memory and marked unsaved until saved.</summary>
    [RelayCommand]
    private void Arrange()
    {
        Session.Arrange();
        BuildViews();
        ViewChanged?.Invoke(this, true);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task<bool> SaveLayoutAsync() => Session.SaveAsync(CancellationToken.None);

    private bool CanSave() => Session.IsDirty && !Session.IsSaving;

    [RelayCommand]
    private void ToggleView() => IsListView = !IsListView;

    public void Dispose()
    {
        _disposed = true;
        _summaries.Changed -= OnChanged;
        Session.PropertyChanged -= OnSessionChanged;
        Session.Discarded -= OnDiscarded;
    }

    private void OnDiscarded(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            _reload.Request();
        }
    }

    private void OnChanged(object? sender, ConversationId e)
    {
        if (!_disposed)
        {
            _reload.Request();
        }
    }

    private void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsSaving));
        OnPropertyChanged(nameof(SaveFailed));
        SaveLayoutCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadAsync()
    {
        await Session.LoadAsync(CancellationToken.None);
        var conversations = await _conversations.ListAsync(Workspace.Id, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        _titles = conversations.ToDictionary(c => c.Conversation.Id, c => UiText.ConversationTitle(c.Conversation, c.FirstQuestion, c.Quote));
        BuildViews();
        Items.Clear();
        foreach (var node in Session.Projection.Nodes)
        {
            var markers = UiText.Markers(node.Markers);
            Items.Add(new SummaryListItem(node.Id, node.DisplayName, markers, markers.Length > 0));
        }

        OnPropertyChanged(nameof(IsEmpty));
        Selected = Session.Selected is { } selected && Session.Projection.Nodes.Any(n => n.Id == selected) ? Detail(selected) : null;
        ViewChanged?.Invoke(this, Session.Viewport is null);
    }

    private void BuildViews()
    {
        var positions = Session.Positions;
        Nodes = [.. Session.Projection.Nodes.Where(n => positions.ContainsKey(n.Id)).Select(n => new NodeView(
            n.Id, n.ShortTitle, n.DisplayName, positions[n.Id].X, positions[n.Id].Y, n.Markers.Unread, n.Markers.HasCandidate, NetworkLayout.LabelWidth(n.ShortTitle)))];
        Edges = [.. Session.Projection.Edges.Where(e => positions.ContainsKey(e.From) && positions.ContainsKey(e.To)).Select(e => new EdgeView(
            e.From, e.To, positions[e.From].X, positions[e.From].Y, positions[e.To].X, positions[e.To].Y, e.IsDirect, e.IsDirect ? null : UiText.ViaCount(e.Via.Count)))];
    }

    private NodeDetail? Detail(SummaryNodeId id)
    {
        if (Session.Projection.Nodes.FirstOrDefault(n => n.Id == id) is not { } node)
        {
            return null;
        }

        var edge = Session.Projection.Edges.FirstOrDefault(e => e.To == id && !e.IsDirect);
        return new NodeDetail(
            node.Id,
            node.Conversation,
            node.DisplayName,
            node.ShortTitle,
            UiText.Markers(node.Markers),
            UiText.NodeSource(_titles.GetValueOrDefault(node.Conversation) ?? UiText.NewConversation),
            UiText.NodeCreated(node.CreatedAt),
            edge is null ? null : UiText.ViaUnsummarized([.. edge.Via.Select(c => _titles.GetValueOrDefault(c) ?? UiText.NewConversation)]));
    }
}
