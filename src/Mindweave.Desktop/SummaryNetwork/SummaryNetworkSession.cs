using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.Text;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>The part of the canvas in view: centre in canvas coordinates and zoom. Never stored (architecture §11.3).</summary>
public readonly record struct Viewport(double CenterX, double CenterY, double Zoom)
{
    public const double MinZoom = 0.25;
    public const double MaxZoom = 2.5;
    public const double FitMargin = 32;

    public static double Clamp(double zoom) => Math.Clamp(zoom, MinZoom, MaxZoom);
}

/// <summary>
/// One workspace's network in memory for the life of the process (architecture §11.2–11.4): the projection, the dots'
/// positions in the view shown, the viewport and the selection. Survives leaving the network to read a summary; the
/// viewport is dropped when the workspace is left. Positions are computed; only each exploration's place in the spatial
/// view is saved, directly and without a pending-save state.
/// </summary>
public sealed partial class SummaryNetworkSession : ObservableObject
{
    private readonly ISummaryNetworkLayout _network;
    private Dictionary<SummaryNodeId, NodePosition> _positions = [];
    private IReadOnlyList<SummaryNodeId> _dayStarts = [];
    private IReadOnlyDictionary<SummaryNodeId, SpatialLabel> _labels = SpatialArrangement.Empty.Labels;

    public SummaryNetworkSession(WorkspaceId workspace, ISummaryNetworkLayout network)
    {
        Workspace = workspace;
        _network = network;
    }

    /// <summary>The view shown; remembered for the life of the process.</summary>
    public SummaryNetworkViewMode ViewMode { get; private set; } = SummaryNetworkViewMode.Spatial;

    /// <summary>The network is shown as its timeline.</summary>
    public bool IsTimeline => ViewMode == SummaryNetworkViewMode.Timeline;

    /// <summary>The roots that start a new local day, in timeline order.</summary>
    public IReadOnlyList<SummaryNodeId> DayStarts => _dayStarts;

    /// <summary>Switches the view and recomputes every position from the current projection.</summary>
    public void SetViewMode(SummaryNetworkViewMode mode)
    {
        ViewMode = mode;
        var before = new Dictionary<ConversationId, ExplorationPlace>(_places);
        ComputePositions(Projection);
        Viewport = null;
        FitAllRequested = false;

        // Explorations first placed in this view (say, after a load in the timeline) keep their place on the next load.
        _ = SavePlacesAsync(before, CancellationToken.None);
    }

    /// <summary>The local calendar day a conversation was created on (the timeline groups roots by it).</summary>
    internal static DateOnly LocalDay(DateTimeOffset createdAt) => DateOnly.FromDateTime(createdAt.ToLocalTime().DateTime);

    private void ComputePositions(SummaryNetworkProjection projection)
    {
        var parents = BranchParents(projection);
        var arrangement = TimelineLayout.Arrange([.. projection.Nodes.Select((n, i) => new TimelineNode(
            n.Id, parents.TryGetValue(n.Id, out var parent) ? parent : null, LocalDay(n.CreatedAt), i))]);
        _dayStarts = arrangement.DayStarts;
        if (IsTimeline)
        {
            _positions = new Dictionary<SummaryNodeId, NodePosition>(TimelineColumns.Widen(arrangement.Positions, projection.Nodes.ToDictionary(n => n.Id, LabelWidthOf), TimelineColumns.MinColumn));
            _labels = SpatialArrangement.Empty.Labels;
            return;
        }

        // Explorations return to their places (design §16, option C): the saved ones, or the ones this run last used.
        var spatial = SpatialLayout.Arrange(SpatialNodes(projection, parents, _dayStarts), places: _places.ToDictionary(p => p.Key.Value, p => p.Value));
        _positions = new Dictionary<SummaryNodeId, NodePosition>(spatial.Positions);
        _labels = spatial.Labels;
        _places = spatial.Places.ToDictionary(p => new ConversationId(p.Key), p => p.Value);
    }

    /// <summary>Each exploration's place as last laid out, by its root conversation: read from storage on load, saved when it changes.</summary>
    private Dictionary<ConversationId, ExplorationPlace> _places = [];

    /// <summary>
    /// Where each label sits round its dot in the spatial view, and its wrapped lines, as the layout placed them; empty
    /// on the timeline, where labels go to the right of their dots.
    /// </summary>
    public IReadOnlyDictionary<SummaryNodeId, SpatialLabel> SpatialLabels => !IsTimeline ? _labels : SpatialArrangement.Empty.Labels;

    /// <summary>
    /// Saves the explorations whose place is new or changed since <paramref name="saved"/>, directly (like a new node's
    /// automatic place, U3). A failed save is not reported: the next load starts from the last saved places again.
    /// </summary>
    private async Task SavePlacesAsync(IReadOnlyDictionary<ConversationId, ExplorationPlace> saved, CancellationToken cancellationToken)
    {
        if (IsTimeline)
        {
            return;
        }

        var changed = _places.Where(p => !saved.TryGetValue(p.Key, out var before) || !Same(before, p.Value)).ToDictionary(p => p.Key, p => p.Value);
        if (changed.Count == 0)
        {
            return;
        }

        try
        {
            await _network.SaveExplorationPlacesAsync(Workspace, changed, cancellationToken);
        }
#pragma warning disable CA1031 // Layout saving is independent of content; a lost place is worked out again on the next load.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
        }
    }

    /// <summary>Two places alike to well under a pixel (turning and moving back leaves rounding behind).</summary>
    private static bool Same(ExplorationPlace a, ExplorationPlace b) =>
        a.Size == b.Size && Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6 && Math.Abs(a.Angle - b.Angle) < 1e-9;

    /// <summary>Each branch's parent: only branches are tree parents; the time thread between roots is not.</summary>
    internal static Dictionary<SummaryNodeId, SummaryNodeId> BranchParents(SummaryNetworkProjection projection) =>
        projection.Edges.Where(e => e.Kind == SummaryNetworkEdgeKind.Branch).ToDictionary(e => e.To, e => e.From);

    /// <summary>The room a node's label takes, as the layout and the canvas reserve it.</summary>
    private static double LabelWidthOf(SummaryNetworkNode node) => SummaryNetworkLayout.LabelWidth(SummaryNetworkLayout.Label(node.ShortTitle, node.Markers.HasCandidate));

    /// <summary>
    /// The conversations as the spatial layout takes them (§6): branches are the parents, the projection's order is creation
    /// order, the weight is the mass, a dot is as large as drawn (its summary ring included), the label is the one the
    /// canvas shows, and a root that starts a new day carries its date under the label (spec §4.4).
    /// </summary>
    internal static List<SpatialNode> SpatialNodes(SummaryNetworkProjection projection, IReadOnlyDictionary<SummaryNodeId, SummaryNodeId> parents, IReadOnlyList<SummaryNodeId> dayStarts)
    {
        var dated = dayStarts.ToHashSet();
        return [.. projection.Nodes.Select((n, i) => new SpatialNode(
            n.Id,
            parents.TryGetValue(n.Id, out var parent) ? parent : null,
            i,
            n.Weight,
            SummaryNetworkLayout.DotFootprintRadius(n.Weight, n.HasSummary),
            SummaryNetworkLayout.Label(n.ShortTitle, n.Markers.HasCandidate),
            dated.Contains(n.Id),
            n.Conversation.Value))];
    }

    public WorkspaceId Workspace { get; }

    public SummaryNetworkProjection Projection { get; private set; } = new([], []);

    public IReadOnlyDictionary<SummaryNodeId, NodePosition> Positions => _positions;

    /// <summary>Puts a dot somewhere until the next layout (tests: a picture the layout would not make).</summary>
    internal void Put(SummaryNodeId node, NodePosition position) => _positions[node] = position;

    /// <summary>
    /// Null until the canvas chooses the opening view (the timeline: zoom 1 on its newest end; otherwise everything fitted);
    /// kept while the workspace stays active.
    /// </summary>
    public Viewport? Viewport
    {
        get => _viewport;
        set
        {
            _viewport = value;
            _viewportIsOpening = false;
        }
    }

    private Viewport? _viewport;
    private bool _viewportIsOpening;

    /// <summary>
    /// The canvas chose the opening view. Until the reader moves the view (pan, zoom, show all, a jump) or selects a dot,
    /// a reload that moves the dots (a title arriving, a conversation changing) opens the view again from the new
    /// positions.
    /// </summary>
    public void SetOpeningViewport(Viewport opening)
    {
        _viewport = opening;
        _viewportIsOpening = true;
    }

    /// <summary>
    /// The dots the canvas shows changed (a reload rebuilt them): while the view is still the opening one, forget it so the
    /// canvas opens again from the new positions. A view the reader moved is kept. Returns whether it was dropped.
    /// </summary>
    public bool ReopenIfStillOpening()
    {
        if (!_viewportIsOpening)
        {
            return false;
        }

        _viewport = null;
        return true;
    }

    /// <summary>
    /// "显示全部" asked for everything to be fitted into view: the canvas fits instead of choosing the opening view, the
    /// next time it fills an empty <see cref="Viewport"/>.
    /// </summary>
    public bool FitAllRequested { get; set; }

    /// <summary>The selected node. Selecting (or clearing the selection) ends the opening view's following of the dots.</summary>
    public SummaryNodeId? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            _viewportIsOpening = false;
        }
    }

    private SummaryNodeId? _selected;

    /// <summary>
    /// Reads the projection and the explorations' saved places, lays everything out, and saves the places that are new or
    /// changed.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var projection = await _network.ProjectAsync(Workspace, cancellationToken);
        var saved = await _network.GetExplorationPlacesAsync(Workspace, cancellationToken);
        _places = saved.ToDictionary(p => p.Key, p => p.Value);
        ComputePositions(projection);
        Projection = projection;
        await SavePlacesAsync(saved, cancellationToken);
    }
}

/// <summary>The network sessions of all workspaces opened in this process.</summary>
public sealed class SummaryNetworkSessions(ISummaryNetworkLayout network)
{
    private readonly Dictionary<WorkspaceId, SummaryNetworkSession> _sessions = [];

    public SummaryNetworkSession Get(WorkspaceId workspace)
    {
        if (!_sessions.TryGetValue(workspace, out var session))
        {
            session = new SummaryNetworkSession(workspace, network);
            _sessions[workspace] = session;
        }

        return session;
    }

    /// <summary>The workspace was deleted: its session goes.</summary>
    public void Remove(WorkspaceId workspace) => _sessions.Remove(workspace);
}
