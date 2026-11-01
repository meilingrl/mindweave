using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Network;
using Mindweave.Application.Summaries;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Network;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// One summary in the list view: display name, markers in words, and the same tags as the canvas details
/// (未读 / 已读, 有候选更新, and <paramref name="Other"/> for the rest, such as "来源已归档").
/// </summary>
public sealed record SummaryListItem(SummaryNodeId Node, string DisplayName, string Markers, bool HasMarkers, bool Unread = false, bool HasCandidate = false, string Other = "")
{
    public bool HasOther => Other.Length > 0;
}

/// <summary>
/// A node as the canvas draws it, in canvas coordinates. <paramref name="Parent"/> is its nearest summarized source
/// (the edge it hangs from); <paramref name="IsRoot"/> marks the summary of an exploration's root conversation.
/// </summary>
public sealed record NodeView(SummaryNodeId Id, string ShortTitle, string DisplayName, double X, double Y, bool Unread, bool HasCandidate, double LabelWidth, SummaryNodeId? Parent = null, bool IsRoot = false);

/// <summary>
/// "探索 · root conversation" for an exploration. <paramref name="Anchor"/> is its root's node, or its top-left node
/// when the root has no summary; the canvas draws the caption above the bounding box of <paramref name="Members"/>,
/// so no edge of the group crosses it.
/// </summary>
public sealed record GroupCaption(string Text, SummaryNodeId Anchor, IReadOnlyList<SummaryNodeId> Members);

/// <summary>A source edge as the canvas draws it; dashed with a label when it passes unsummarized conversations.</summary>
public sealed record EdgeView(SummaryNodeId From, SummaryNodeId To, double FromX, double FromY, double ToX, double ToY, bool IsDirect, string? Label);

/// <summary>
/// One conversation on a node's source path: a summarized one by its short name, an unsummarized one by its title,
/// shown muted with "（未总结）" so it never passes for a summary (architecture §11).
/// </summary>
public sealed record PathStep(string Name, bool Summarized, bool IsFirst)
{
    public string Text => Summarized ? Name : UiText.Unsummarized(Name);
}

/// <summary>
/// What the side panel says about the selected node; never the summary body, never marks it read.
/// <paramref name="Steps"/> is the source path step by step, root first; <paramref name="Path"/> the same in one line.
/// </summary>
public sealed record NodeDetail(SummaryNodeId Id, ConversationId Conversation, string DisplayName, string ShortTitle, string Markers, string Source, string Created, string? Via,
    bool Unread, bool HasCandidate, bool SourceArchived, string Path, IReadOnlyList<PathStep> Steps);

/// <summary>
/// The summary network of the current workspace (requirement §7.3–7.4, architecture §11): nodes with short titles
/// and real source edges, selection with details, search, show all, zoom, explicit arrange and save. State lives in
/// the workspace's <see cref="NetworkSession"/>, so the viewport and selection survive reading a summary and coming
/// back. A list view of the same summaries stays available for keyboard and screen-reader use.
/// </summary>
public sealed partial class NetworkHomeViewModel : PageViewModel, IDisposable
{
    private readonly ISummaries _summaries;
    private readonly IConversations _conversations;
    private readonly IWorkbenchNavigator _navigator;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private Dictionary<ConversationId, string> _titles = [];
    private bool _disposed;

    public NetworkHomeViewModel(Workspace workspace, NetworkSession session, ISummaries summaries, IConversations conversations, IWorkbenchNavigator navigator, IUiDispatcher dispatcher)
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
        _conversations.VisibilityChanged += OnVisibilityChanged;
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

    public IReadOnlyList<GroupCaption> Captions { get; private set; } = [];

    /// <summary>The selected node and every summarized source up to its exploration's root; empty without a selection.</summary>
    public IReadOnlySet<SummaryNodeId> Trail { get; private set; } = new HashSet<SummaryNodeId>();

    /// <summary>Nodes whose short or full name contains the search text, updated as it is typed.</summary>
    public IReadOnlySet<SummaryNodeId> Matches { get; private set; } = new HashSet<SummaryNodeId>();

    /// <summary>The save button's text: says plainly when the arrangement is not saved.</summary>
    public string SaveLayoutLabel => Session.IsDirty ? UiText.SaveLayoutUnsaved : UiText.LayoutSaved;

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
        Trail = TrailOf(node);
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

        var match = Nodes.FirstOrDefault(n => IsMatch(n, text));
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

    /// <summary>
    /// A drag ended: the node keeps its new place in memory and the layout is unsaved until saved, as after
    /// <see cref="ArrangeCommand"/>; unlike arranging, the viewport stays where the reader put it.
    /// </summary>
    public void MoveNode(SummaryNodeId node, double x, double y)
    {
        if (!Session.Move(node, new Application.Storage.NodePosition(x, y)))
        {
            return;
        }

        BuildViews();
        ViewChanged?.Invoke(this, false);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task<bool> SaveLayoutAsync() => Session.SaveAsync(CancellationToken.None);

    private bool CanSave() => Session.IsDirty && !Session.IsSaving;

    [RelayCommand]
    private void ToggleView() => IsListView = !IsListView;

    partial void OnSearchTextChanged(string value)
    {
        Matches = FindMatches();
        ViewChanged?.Invoke(this, false);
    }

    private static bool IsMatch(NodeView node, string text) =>
        node.ShortTitle.Contains(text, StringComparison.CurrentCultureIgnoreCase) || node.DisplayName.Contains(text, StringComparison.CurrentCultureIgnoreCase);

    private HashSet<SummaryNodeId> FindMatches()
    {
        var text = SearchText.Trim();
        return text.Length == 0 ? [] : [.. Nodes.Where(n => IsMatch(n, text)).Select(n => n.Id)];
    }

    /// <summary>The selected node and its sources, following each node's parent edge to the top.</summary>
    private HashSet<SummaryNodeId> TrailOf(SummaryNodeId? node)
    {
        var trail = new HashSet<SummaryNodeId>();
        var parents = Nodes.ToDictionary(n => n.Id, n => n.Parent);
        for (var at = node; at is { } id && parents.ContainsKey(id) && trail.Add(id); at = parents[id])
        {
        }

        return trail;
    }

    public void Dispose()
    {
        _disposed = true;
        _summaries.Changed -= OnChanged;
        _conversations.VisibilityChanged -= OnVisibilityChanged;
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

    /// <summary>A source was archived, restored or deleted: its node's "source archived" marker may have changed.</summary>
    private void OnVisibilityChanged(object? sender, IReadOnlyList<ConversationId> e)
    {
        if (!_disposed)
        {
            _reload.Request();
        }
    }

    private void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(SaveLayoutLabel));
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
            var markers = string.Join(" · ", new[] { UiText.Markers(node.Markers), node.SourceArchived ? UiText.SourceArchived : string.Empty }.Where(m => m.Length > 0));
            var other = string.Join(" · ", new[] { node.Markers.HasLaterContent ? UiText.MarkerLater : string.Empty, node.SourceArchived ? UiText.SourceArchived : string.Empty }.Where(m => m.Length > 0));
            Items.Add(new SummaryListItem(node.Id, node.DisplayName, markers, markers.Length > 0, node.Markers.Unread, node.Markers.HasCandidate, other));
        }

        OnPropertyChanged(nameof(IsEmpty));
        Selected = Session.Selected is { } selected && Session.Projection.Nodes.Any(n => n.Id == selected) ? Detail(selected) : null;
        Trail = TrailOf(Selected?.Id);
        Matches = FindMatches();
        ViewChanged?.Invoke(this, Session.Viewport is null);
    }

    private void BuildViews()
    {
        var positions = Session.Positions;
        var parents = Session.Projection.Edges.ToDictionary(e => e.To, e => e.From);
        Nodes = [.. Session.Projection.Nodes.Where(n => positions.ContainsKey(n.Id)).Select(n => new NodeView(
            n.Id, n.ShortTitle, n.DisplayName, positions[n.Id].X, positions[n.Id].Y, n.Markers.Unread, n.Markers.HasCandidate, NetworkLayout.LabelWidth(NetworkLayout.Label(n.ShortTitle, n.Markers.HasCandidate)),
            parents.TryGetValue(n.Id, out var parent) && positions.ContainsKey(parent) ? parent : null, n.Conversation == n.Root))];
        Edges = [.. Session.Projection.Edges.Where(e => positions.ContainsKey(e.From) && positions.ContainsKey(e.To)).Select(e => new EdgeView(
            e.From, e.To, positions[e.From].X, positions[e.From].Y, positions[e.To].X, positions[e.To].Y, e.IsDirect, e.IsDirect ? null : UiText.ViaCount(e.Via.Count)))];
        Captions = [.. Session.Projection.Nodes.Where(n => positions.ContainsKey(n.Id)).GroupBy(n => n.Root).Select(g =>
        {
            // Over the root's own summary; when the root conversation has none, over the group's top-left node.
            var anchor = g.FirstOrDefault(n => n.Conversation == g.Key) ?? g.OrderBy(n => positions[n.Id].Y).ThenBy(n => positions[n.Id].X).First();
            return new GroupCaption(UiText.ExplorationCaption(_titles.GetValueOrDefault(g.Key)), anchor.Id, [.. g.Select(n => n.Id)]);
        })];
        Trail = TrailOf(Session.Selected);
    }

    private NodeDetail? Detail(SummaryNodeId id)
    {
        if (Session.Projection.Nodes.FirstOrDefault(n => n.Id == id) is not { } node)
        {
            return null;
        }

        var edge = Session.Projection.Edges.FirstOrDefault(e => e.To == id && !e.IsDirect);
        var steps = PathOf(node);
        return new NodeDetail(
            node.Id,
            node.Conversation,
            node.DisplayName,
            node.ShortTitle,
            UiText.Markers(node.Markers),
            UiText.NodeSource(_titles.GetValueOrDefault(node.Conversation) ?? UiText.NewConversation, node.SourceArchived),
            UiText.NodeCreated(node.CreatedAt),
            edge is null ? null : UiText.ViaUnsummarized([.. edge.Via.Select(c => _titles.GetValueOrDefault(c) ?? UiText.NewConversation)]),
            node.Markers.Unread,
            node.Markers.HasCandidate,
            node.SourceArchived,
            UiText.SourcePath([.. steps.Select(step => step.Text)]),
            steps);
    }

    /// <summary>
    /// Every conversation from the exploration's root to this node, root first: summarized ones by short name, the
    /// unsummarized ones by title and marked, also when no summarized ancestor exists at all.
    /// </summary>
    private List<PathStep> PathOf(NetworkNode node)
    {
        var summarized = Session.Projection.Nodes.ToDictionary(n => n.Conversation, n => n.ShortTitle);
        var chain = node.Ancestors.Append(node.Conversation).ToList();
        return [.. chain.Select((conversation, i) => summarized.TryGetValue(conversation, out var shortTitle)
            ? new PathStep(shortTitle, true, i == 0)
            : new PathStep(_titles.GetValueOrDefault(conversation) ?? UiText.NewConversation, false, i == 0))];
    }
}
