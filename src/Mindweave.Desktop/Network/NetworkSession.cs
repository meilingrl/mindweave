using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Network;
using Mindweave.Core.Network;

namespace Mindweave.Desktop.Network;

/// <summary>
/// One workspace's window-local network state. Call on the owning UI synchronization context;
/// awaited store/worker operations resume there before changing observable state.
/// </summary>
public sealed partial class NetworkSession : ObservableObject
{
    private readonly INetworkStore store;
    private readonly NetworkLayoutEngine engine;
    private readonly Func<NetworkGraphSnapshot, ImmutableDictionary<Guid, NodeExtent>, Task<ImmutableDictionary<Guid, NodePosition>>> arrangeAsync;
    private readonly HashSet<Guid> attemptedInitial = [];
    private long lifetime, reloadVersion, arrangeVersion, persistenceVersion;
    private bool isLoading, isArranging, isSaving, isQuiesced;
    private string? readError, layoutError, saveError;
    private Guid? selectedNodeId;

    public NetworkSession(Guid workspaceId, INetworkStore store, NetworkLayoutEngine? engine = null,
        Func<NetworkGraphSnapshot, ImmutableDictionary<Guid, NodeExtent>, Task<ImmutableDictionary<Guid, NodePosition>>>? arrangeAsync = null)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("工作区身份无效。", nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(store);
        WorkspaceId = workspaceId; this.store = store; this.engine = engine ?? new();
        this.arrangeAsync = arrangeAsync ?? ((graph, extents) => Task.Run(() => this.engine.Arrange(graph, extents)));
        Graph = new(workspaceId, "", [], []);
    }

    public Guid WorkspaceId { get; }
    public NetworkGraphSnapshot Graph { get; private set; }
    public ImmutableDictionary<Guid, NodePosition> Positions { get; private set; } = ImmutableDictionary<Guid, NodePosition>.Empty;
    public ImmutableDictionary<Guid, NodePosition> SavedPositions { get; private set; } = ImmutableDictionary<Guid, NodePosition>.Empty;
    public long Revision { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsLoading { get => isLoading; private set => SetProperty(ref isLoading, value); }
    public bool IsArranging { get => isArranging; private set => SetProperty(ref isArranging, value); }
    public bool IsSaving { get => isSaving; private set => SetProperty(ref isSaving, value); }
    public bool IsQuiesced { get => isQuiesced; private set => SetProperty(ref isQuiesced, value); }
    public string? ErrorMessage => saveError ?? layoutError ?? readError;
    public Guid? SelectedNodeId
    {
        get => selectedNodeId;
        set
        {
            if (IsQuiesced) return;
            SetProperty(ref selectedNodeId, value is { } id && Graph.Nodes.Any(n => n.Metadata.Id == id) ? id : null);
        }
    }

    /// <param name="fitAll">True for a genuine workspace/network entry: request fit on the next canvas measurement and retry failed initial inserts.
    /// Metadata refresh and return from a summary use false to retain camera and retry state.</param>
    public Task ReloadAsync(bool fitAll = false)
    {
        if (IsQuiesced) return Task.CompletedTask;
        var stamp = ++reloadVersion; var epoch = lifetime;
        ++arrangeVersion;
        if (fitAll) attemptedInitial.Clear();
        return TrackAsync(() => ReloadCoreAsync(stamp, epoch, fitAll));
    }

    private async Task ReloadCoreAsync(long stamp, long epoch, bool fitAll)
    {
        IsLoading = true;
        try
        {
            NetworkWorkspaceSnapshot snapshot;
            ImmutableDictionary<Guid, NodePosition> placed;
            // A read that overlaps a completed layout write cannot roll the saved baseline back.
            while (true)
            {
                var writeStamp = persistenceVersion;
                snapshot = await store.LoadNetworkAsync(WorkspaceId);
                if (!CurrentRead(stamp, epoch)) return;
                if (snapshot.Graph.WorkspaceId != WorkspaceId) throw new InvalidDataException("网络快照不属于当前工作区。");
                var extents = Extents;
                placed = await Task.Run(() => engine.PlaceMissing(snapshot.Graph, snapshot.SavedPositions, extents));
                if (!CurrentRead(stamp, epoch)) return;
                if (writeStamp == persistenceVersion) break;
            }
            var valid = snapshot.Graph.Nodes.Select(n => n.Metadata.Id).ToHashSet();
            // Failed automatic inserts still have a visible fallback. Ordinary refresh must not
            // rearrange those existing nodes when another node arrives; next entry retries afresh.
            var next = placed.SetItems(Positions.Where(p => valid.Contains(p.Key) &&
                (IsDirty || (!fitAll && !snapshot.SavedPositions.ContainsKey(p.Key)))));
            var layoutChanged = !SamePositions(Positions, next);
            Graph = snapshot.Graph;
            SavedPositions = snapshot.SavedPositions.Where(p => valid.Contains(p.Key)).ToImmutableDictionary();
            Positions = next;
            if (layoutChanged) ++Revision;
            if (selectedNodeId is { } selected && !valid.Contains(selected)) selectedNodeId = null;
            attemptedInitial.RemoveWhere(id => !valid.Contains(id));
            readError = null;
            NotifyGraphState();
            // The full missing-node plan is computed before the first independent insert.
            foreach (var node in snapshot.Graph.Nodes)
            {
                if (!CurrentRead(stamp, epoch)) break;
                var id = node.Metadata.Id;
                if (SavedPositions.ContainsKey(id) || attemptedInitial.Contains(id)) continue;
                await SaveInitialAsync(id, placed[id], stamp, epoch);
            }
            // Entry is loaded before its new canvas is attached. Keep the fit request until
            // that canvas supplies measured extents and size; cached dimensions belong to the
            // previous view. Publish after all awaits so the outgoing canvas cannot consume it.
            if (fitAll && CurrentRead(stamp, epoch)) IsFitPending = true;
        }
        catch
        {
            if (CurrentRead(stamp, epoch)) { readError = "网络读取失败，请重新打开网络。"; OnPropertyChanged(nameof(ErrorMessage)); }
        }
        finally { if (stamp == reloadVersion) IsLoading = false; }
    }

    private bool CurrentRead(long stamp, long epoch) => !IsQuiesced && lifetime == epoch && reloadVersion == stamp;
    private static bool SamePositions(ImmutableDictionary<Guid, NodePosition> first, ImmutableDictionary<Guid, NodePosition> second) =>
        first.Count == second.Count && first.All(p => second.TryGetValue(p.Key, out var position) && position == p.Value);

    private void NotifyGraphState()
    {
        OnPropertyChanged(nameof(Graph)); OnPropertyChanged(nameof(Positions)); OnPropertyChanged(nameof(SavedPositions));
        OnPropertyChanged(nameof(Revision)); OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(SelectedNodeId)); OnPropertyChanged(nameof(ErrorMessage));
    }
}
