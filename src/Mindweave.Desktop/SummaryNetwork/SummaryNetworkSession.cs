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
/// One workspace's network in memory for the life of the process (architecture §11.2–11.4): the projection, node
/// positions with a layout revision, the last saved revision, the viewport and the selection. Survives leaving the
/// network to read a summary; the viewport is dropped when the workspace is left. New summaries' automatic places are
/// saved one row at a time and never mark the layout as unsaved (U3); "arrange" does, and an explicit save writes a
/// snapshot of one revision in one transaction.
/// </summary>
public sealed partial class SummaryNetworkSession : ObservableObject
{
    private readonly ISummaryNetworkLayout _summarySummaryNetwork;
    private Dictionary<SummaryNodeId, NodePosition> _positions = [];
    private long _revision;
    private bool _loaded;

    private readonly SessionNetworkGate _gate;
    private IReadOnlyList<SummaryNodeId> _dayStarts = [];
    private IReadOnlyDictionary<SummaryNodeId, SpatialLabel> _labels = SpatialArrangement.Empty.Labels;

    public SummaryNetworkSession(WorkspaceId workspace, ISummaryNetworkLayout network, SessionNetworkGate gate)
    {
        Workspace = workspace;
        _summarySummaryNetwork = network;
        _gate = gate;
    }

    /// <summary>The session network (every conversation a dot, layouts computed and never saved) is switched on.</summary>
    public bool IsSessionNetwork => _gate.Enabled;

    /// <summary>The view of the session network; remembered for the life of the process. Not used without the gate.</summary>
    public SummaryNetworkViewMode ViewMode { get; private set; } = SummaryNetworkViewMode.Spatial;

    /// <summary>The session network is shown as its timeline (the gate on and that view chosen).</summary>
    public bool IsTimeline => _gate.Enabled && ViewMode == SummaryNetworkViewMode.Timeline;

    /// <summary>The roots that start a new local day, in timeline order; empty without the gate.</summary>
    public IReadOnlyList<SummaryNodeId> DayStarts => _dayStarts;

    /// <summary>Switches the view and recomputes every position from the current projection. Nothing is saved.</summary>
    public void SetViewMode(SummaryNetworkViewMode mode)
    {
        ViewMode = mode;
        if (!_gate.Enabled)
        {
            return;
        }

        var before = new Dictionary<ConversationId, ExplorationPlace>(_places);
        ComputeSessionPositions(Projection);
        Viewport = null;
        FitAllRequested = false;

        // Explorations first placed in this view (say, after a load in the timeline) keep their place on the next load.
        _ = SavePlacesAsync(before, CancellationToken.None);
    }

    /// <summary>The local calendar day a conversation was created on (the timeline groups roots by it).</summary>
    internal static DateOnly LocalDay(DateTimeOffset createdAt) => DateOnly.FromDateTime(createdAt.ToLocalTime().DateTime);

    private void ComputeSessionPositions(SummaryNetworkProjection projection)
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

        // Explorations return to their places (design §16, option C): the saved ones, or the ones this session last used.
        var spatial = SpatialLayout.Arrange(SpatialNodes(projection, parents, _dayStarts), places: _places.ToDictionary(p => p.Key.Value, p => p.Value));
        _positions = new Dictionary<SummaryNodeId, NodePosition>(spatial.Positions);
        _labels = spatial.Labels;
        _places = spatial.Places.ToDictionary(p => new ConversationId(p.Key), p => p.Value);
    }

    /// <summary>Each exploration's place as last laid out, by its root conversation: read from storage on load, saved when it changes.</summary>
    private Dictionary<ConversationId, ExplorationPlace> _places = [];

    /// <summary>
    /// Where each label sits round its dot in the spatial view of the session network, and its wrapped lines, as the
    /// layout placed them; empty on the timeline and without the gate (labels then go to the right of their dots).
    /// </summary>
    public IReadOnlyDictionary<SummaryNodeId, SpatialLabel> SpatialLabels => _gate.Enabled && !IsTimeline ? _labels : SpatialArrangement.Empty.Labels;

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
            await _summarySummaryNetwork.SaveExplorationPlacesAsync(Workspace, changed, cancellationToken);
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
    /// The sessions as the spatial layout takes them (§6): branches are the parents, the projection's order is creation
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

    /// <summary>Changes on every layout change; an explicit save records which revision it wrote.</summary>
    public long Revision => _revision;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial long SavedRevision { get; private set; }

    /// <summary>The layout has changes that only an explicit save keeps.</summary>
    public bool IsDirty => _revision != SavedRevision;

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial bool SaveFailed { get; private set; }

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
    /// positions in the session network.
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
        if (!_gate.Enabled || !_viewportIsOpening)
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
    /// Reads the projection and saved positions. Nodes without a position are placed beside their source and each
    /// such place is saved by itself. While unsaved changes exist, the positions in memory are kept as they are and
    /// serve as the reference for new places, so a new node appears beside the source the reader sees.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_gate.Enabled)
        {
            var sessions = await _summarySummaryNetwork.ProjectSessionsAsync(Workspace, cancellationToken);
            var savedPlaces = await _summarySummaryNetwork.GetExplorationPlacesAsync(Workspace, cancellationToken);
            _places = savedPlaces.ToDictionary(p => p.Key, p => p.Value);
            ComputeSessionPositions(sessions);
            Projection = sessions;
            _loaded = true;
            await SavePlacesAsync(savedPlaces, cancellationToken);
            return;
        }

        var projection = await _summarySummaryNetwork.ProjectAsync(Workspace, cancellationToken);
        var saved = await _summarySummaryNetwork.GetLayoutAsync(Workspace, cancellationToken);
        var nodes = LayoutNodes(projection);
        var ids = projection.Nodes.Select(n => n.Id).ToHashSet();

        var reference = _loaded && IsDirty ? new Dictionary<SummaryNodeId, NodePosition>(_positions) : saved.ToDictionary(p => p.Key, p => p.Value);
        foreach (var gone in reference.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            reference.Remove(gone);
        }

        // Nodes saved but not in memory (another load placed them) join what is shown.
        foreach (var (id, position) in saved.Where(p => ids.Contains(p.Key) && !reference.ContainsKey(p.Key)))
        {
            reference[id] = position;
        }

        var added = SummaryNetworkLayout.PlaceNew(nodes, reference);
        foreach (var (id, position) in added)
        {
            reference[id] = position;

            // A failed direct save is not reported; the next load computes the same place again (MW-C12).
            try
            {
                await _summarySummaryNetwork.PlaceAsync(Workspace, id, position, cancellationToken);
            }
#pragma warning disable CA1031 // Layout saving is independent of content; a lost automatic place is recomputed.
            catch (Exception swallowed)
#pragma warning restore CA1031
            {
                DesktopLog.Swallowed(swallowed);
            }
        }

        Projection = projection;
        _positions = reference;
        _loaded = true;
    }

    /// <summary>Explicit "arrange": a full new layout, kept in memory until saved.</summary>
    public void Arrange()
    {
        if (_gate.Enabled)
        {
            return;
        }

        _positions = SummaryNetworkLayout.Arrange(LayoutNodes(Projection)).ToDictionary(p => p.Key, p => p.Value);
        _revision++;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Revision));
        Viewport = null;
    }

    /// <summary>
    /// The end of a manual drag: one node at a new place in the same canvas coordinates, kept in memory and marked
    /// unsaved exactly like "arrange" (requirement §7.4, architecture §11.4). The viewport is left as it is. Returns
    /// false, changing nothing, for a node not in this network or a place it already has.
    /// </summary>
    public bool Move(SummaryNodeId node, NodePosition position)
    {
        if (_gate.Enabled)
        {
            return false;
        }

        if (!_positions.TryGetValue(node, out var current) || current == position)
        {
            return false;
        }

        _positions = new Dictionary<SummaryNodeId, NodePosition>(_positions) { [node] = position };
        _revision++;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Revision));
        return true;
    }

    /// <summary>
    /// Saves the current revision's positions in one transaction. Changes made while saving stay unsaved: success
    /// only records the revision that was captured. A failure keeps everything in memory and the unsaved mark. The
    /// session network's layouts are computed and never saved (spec §9), whatever the revision says.
    /// </summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        if (_gate.Enabled)
        {
            return true;
        }

        if (IsSaving || !IsDirty)
        {
            return !IsDirty;
        }

        var revision = _revision;
        var snapshot = new Dictionary<SummaryNodeId, NodePosition>(_positions);
        IsSaving = true;
        SaveFailed = false;
        try
        {
            await _summarySummaryNetwork.SaveLayoutAsync(Workspace, snapshot, cancellationToken);
            SavedRevision = Math.Max(SavedRevision, revision);
            return !IsDirty;
        }
#pragma warning disable CA1031 // A failed layout save is reported next to the button and can be retried.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            SaveFailed = true;
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>"Don't save": forget unsaved changes; pages showing this network reload the saved positions.</summary>
    public void DiscardChanges()
    {
        SavedRevision = _revision;
        SaveFailed = false;
        _loaded = false;
        Discarded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Unsaved changes were dropped; what is shown must be read again.</summary>
    public event EventHandler? Discarded;

    private List<LayoutNode> LayoutNodes(SummaryNetworkProjection projection)
    {
        // Only branches are tree parents; the time thread between roots is not.
        var parents = projection.Edges.Where(e => !_gate.Enabled || e.Kind == SummaryNetworkEdgeKind.Branch).ToDictionary(e => e.To, e => e.From);
        return [.. projection.Nodes.Select((n, i) => new LayoutNode(
            n.Id, parents.TryGetValue(n.Id, out var parent) ? parent : null, n.Root.Value, i, SummaryNetworkLayout.LabelWidth(SummaryNetworkLayout.Label(n.ShortTitle, n.Markers.HasCandidate))))];
    }
}

/// <summary>The network sessions of all workspaces opened in this process.</summary>
public sealed class SummaryNetworkSessions(ISummaryNetworkLayout network, SessionNetworkGate gate)
{
    private readonly Dictionary<WorkspaceId, SummaryNetworkSession> _sessions = [];

    public SummaryNetworkSession Get(WorkspaceId workspace)
    {
        if (!_sessions.TryGetValue(workspace, out var session))
        {
            session = new SummaryNetworkSession(workspace, network, gate);
            _sessions[workspace] = session;
        }

        return session;
    }

    public IEnumerable<SummaryNetworkSession> Dirty => _sessions.Values.Where(s => s.IsDirty);

    /// <summary>
    /// The workspace was deleted: its session goes, unsaved layout included, without asking (there is nothing left to
    /// save it to), so neither leaving nor quitting asks about it afterwards.
    /// </summary>
    public void Remove(WorkspaceId workspace)
    {
        if (_sessions.Remove(workspace, out var session) && session.IsDirty)
        {
            session.DiscardChanges();
        }
    }
}
