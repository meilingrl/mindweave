using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Network;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop.Network;

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
public sealed partial class NetworkSession : ObservableObject
{
    private readonly INetworkLayout _network;
    private Dictionary<SummaryNodeId, NodePosition> _positions = [];
    private long _revision;
    private bool _loaded;

    public NetworkSession(WorkspaceId workspace, INetworkLayout network)
    {
        Workspace = workspace;
        _network = network;
    }

    public WorkspaceId Workspace { get; }

    public NetworkProjection Projection { get; private set; } = new([], []);

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

    /// <summary>Null until the canvas fits everything into view; kept while the workspace stays active.</summary>
    public Viewport? Viewport { get; set; }

    public SummaryNodeId? Selected { get; set; }

    /// <summary>Raised after positions or the projection changed; any thread that changed them.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>
    /// Reads the projection and saved positions. Nodes without a position are placed beside their source and each
    /// such place is saved by itself. While unsaved changes exist, the positions in memory are kept as they are and
    /// serve as the reference for new places, so a new node appears beside the source the reader sees.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var projection = await _network.ProjectAsync(Workspace, cancellationToken);
        var saved = await _network.GetLayoutAsync(Workspace, cancellationToken);
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

        var added = NetworkLayout.PlaceNew(nodes, reference);
        foreach (var (id, position) in added)
        {
            reference[id] = position;

            // A failed direct save is not reported; the next load computes the same place again (MW-C12).
            try
            {
                await _network.PlaceAsync(Workspace, id, position, cancellationToken);
            }
#pragma warning disable CA1031 // Layout saving is independent of content; a lost automatic place is recomputed.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        Projection = projection;
        _positions = reference;
        _loaded = true;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Explicit "arrange": a full new layout, kept in memory until saved.</summary>
    public void Arrange()
    {
        _positions = NetworkLayout.Arrange(LayoutNodes(Projection)).ToDictionary(p => p.Key, p => p.Value);
        _revision++;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Revision));
        Viewport = null;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The end of a manual drag: one node at a new place in the same canvas coordinates, kept in memory and marked
    /// unsaved exactly like "arrange" (requirement §7.4, architecture §11.4). The viewport is left as it is. Returns
    /// false, changing nothing, for a node not in this network or a place it already has.
    /// </summary>
    public bool Move(SummaryNodeId node, NodePosition position)
    {
        if (!_positions.TryGetValue(node, out var current) || current == position)
        {
            return false;
        }

        _positions = new Dictionary<SummaryNodeId, NodePosition>(_positions) { [node] = position };
        _revision++;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Revision));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Saves the current revision's positions in one transaction. Changes made while saving stay unsaved: success
    /// only records the revision that was captured. A failure keeps everything in memory and the unsaved mark.
    /// </summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
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
            await _network.SaveLayoutAsync(Workspace, snapshot, cancellationToken);
            SavedRevision = Math.Max(SavedRevision, revision);
            return !IsDirty;
        }
#pragma warning disable CA1031 // A failed layout save is reported next to the button and can be retried.
        catch (Exception)
#pragma warning restore CA1031
        {
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

    private static List<LayoutNode> LayoutNodes(NetworkProjection projection)
    {
        var parents = projection.Edges.ToDictionary(e => e.To, e => e.From);
        return [.. projection.Nodes.Select((n, i) => new LayoutNode(
            n.Id, parents.TryGetValue(n.Id, out var parent) ? parent : null, n.Root.Value, i, NetworkLayout.LabelWidth(NetworkLayout.Label(n.ShortTitle, n.Markers.HasCandidate))))];
    }
}

/// <summary>The network sessions of all workspaces opened in this process.</summary>
public sealed class NetworkSessions(INetworkLayout network)
{
    private readonly Dictionary<WorkspaceId, NetworkSession> _sessions = [];

    public NetworkSession Get(WorkspaceId workspace)
    {
        if (!_sessions.TryGetValue(workspace, out var session))
        {
            session = new NetworkSession(workspace, network);
            _sessions[workspace] = session;
        }

        return session;
    }

    public IEnumerable<NetworkSession> Dirty => _sessions.Values.Where(s => s.IsDirty);

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
