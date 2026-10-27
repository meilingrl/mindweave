using Mindweave.Application.Chat;
using Mindweave.Application.Generation;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
namespace Mindweave.Application.Summaries;

public sealed record GroupNamingSnapshot(Guid TaskId, GenerationStatus Status, bool HasUnsavedChanges, string? Error);
/// <summary>Session-owned background work. Only durable current changes create intents; rename completion never does.</summary>
public sealed partial class GroupNamingCoordinator : IAsyncDisposable, IGenerationRuntimeParticipant
{
    private readonly INodeNamingStore store;
    private readonly ISummaryVersionStore versions;
    private readonly IChatStore chat;
    private readonly IChatTransport transport;
    private readonly ICredentialVault vault;
    private readonly AppGenerationRuntime.FailureSource failure;
    private readonly IDisposable registration;
    private readonly object sync = new();
    private readonly SemaphoreSlim control = new(1, 1);
    private readonly Dictionary<(Guid, string), Group> groups = [];
    private readonly Dictionary<Guid, Guid?> deferred = [];
    private readonly List<Job> jobs = [];
    private readonly List<Task> tails = [];
    private bool initialized, accepting, disposed, configurationReadFailed;
    private long lifetime;
    public AppGenerationRuntime Runtime { get; }
    public event Action? Changed;
    public bool HasPersistenceFailure => Runtime.HasPersistenceFailure;
    public bool HasActiveOrPendingWork { get { lock (sync) return deferred.Count != 0 || groups.Values.Any(g => g.Pending is not null) || jobs.Any(j => Active(j.Record.Status) || j.Dirty || j.Completion is not null) || tails.Any(t => !t.IsCompleted); } }
    public IReadOnlyList<GroupNamingSnapshot> Snapshots { get { lock (sync) return jobs.Select(j => new GroupNamingSnapshot(j.Record.Id, j.Record.Status, j.Dirty || j.Completion is not null, j.Record.ErrorCode)).ToArray(); } }
    public GroupNamingCoordinator(INodeNamingStore store, ISummaryVersionStore versions, IChatStore chat, IChatTransport transport, ICredentialVault vault, AppGenerationRuntime runtime)
    {
        this.store = store; this.versions = versions; this.chat = chat; this.transport = transport; this.vault = vault; Runtime = runtime;
        failure = runtime.CreateFailureSource(); registration = runtime.RegisterParticipant(this); runtime.Changed += RuntimeChanged;
    }
    public async Task InitializeAsync()
    {
        await control.WaitAsync().ConfigureAwait(false);
        try { lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); if (initialized) return; } await store.RecoverInterruptedNodeNamingAsync().ConfigureAwait(false); lock (sync) initialized = accepting = true; }
        catch { failure.Report(); throw; }
        finally { control.Release(); Notify(); }
    }
    public Task NotifyCurrentChangedAsync(Guid nodeId, Guid? expectedVersionId = null)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); if (!initialized || !accepting) return Task.CompletedTask;
            if (Runtime.IsAdmissionPaused) { deferred[nodeId] = expectedVersionId; return Task.CompletedTask; }
            var epoch = lifetime; return Track(() => ProcessCurrentAsync(nodeId, expectedVersionId, epoch));
        }
    }
    private Task Track(Func<Task> work)
    {
        // Caller holds sync, so shutdown cannot miss a newly created tail.
        tails.RemoveAll(t => t.IsCompleted); var task = Task.Run(work); tails.Add(task);
        _ = task.ContinueWith(_ => { Dispatch(); Notify(); }, TaskScheduler.Default); return task;
    }
    private async Task ProcessCurrentAsync(Guid nodeId, Guid? expected, long epoch)
    {
        await control.WaitAsync().ConfigureAwait(false);
        using var demand = Runtime.Scheduler.RegisterLogicalWork(GenerationPriority.Background);
        try
        {
            lock (sync) { if (!accepting || epoch != lifetime) return; if (Runtime.IsAdmissionPaused) { deferred[nodeId] = expected; return; } }
            var node = (await versions.LoadSummaryNodeAsync(nodeId).ConfigureAwait(false))?.State.Node;
            if (node is null || expected is { } v && node.CurrentVersionId != v) return;
            var key = SummaryTitleNormalization.GroupKey(node.BaseTitle);
            var snapshot = await store.ReadNodeNamingGroupAsync(node.WorkspaceId, key).ConfigureAwait(false);
            if (snapshot.Members.IsEmpty || snapshot.Members.All(m => m.NameIsManual) || snapshot.Members.Length == 1 && node.DisplayName == node.BaseTitle) return;
            AiConfiguration? config;
            try { config = await chat.LoadActiveConfigurationAsync().ConfigureAwait(false); } catch { configurationReadFailed = true; throw; }
            if (config is null) return;
            Group group;
            lock (sync)
            {
                if (!accepting || epoch != lifetime) return;
                if (!groups.TryGetValue((node.WorkspaceId, key), out group!)) groups[(node.WorkspaceId, key)] = group = new();
                if (group.Active?.Record.Snapshot.SnapshotHash == snapshot.SnapshotHash || group.Pending?.Intent.Snapshot.SnapshotHash == snapshot.SnapshotHash) return;
                var previous = group.Pending?.Intent.OperationId;
                group.Pending = new(new(Guid.NewGuid(), snapshot, config, DateTimeOffset.UtcNow)) { ReplacedOperation = previous };
            }
            var pending = group.Pending!;
            var saved = await store.SaveNodeNamingIntentAsync(pending.Intent).ConfigureAwait(false);
            if (!Success(saved.Kind)) { lock (sync) group.Pending = null; return; }
            pending.Saved = true;
            await AcceptPendingAsync(group).ConfigureAwait(false);
        }
        catch { failure.Report(); throw; }
        finally { control.Release(); Dispatch(); Notify(); }
    }
    // Caller holds control. At most one active and one latest pending identity per group.
    private async Task AcceptPendingAsync(Group group)
    {
        Pending? pending;
        lock (sync) { pending = group.Pending; if (group.Active is not null || pending is null || pending.Cancelled || !accepting || Runtime.IsAdmissionPaused) return; }
        IDisposable admission;
        try { admission = await Runtime.EnterAcceptanceAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) when (Runtime.IsAdmissionPaused) { return; }
        using var admissionLease = admission;
        lock (sync) if (pending.Cancelled || !accepting || Runtime.IsAdmissionPaused) return;
        pending.AcceptUnknown = true;
        var result = await store.AcceptNodeNamingAsync(pending.Intent.OperationId, pending.Intent.Snapshot, pending.Intent.Configuration).ConfigureAwait(false);
        pending.AcceptUnknown = false;
        if (Success(result.Kind) && result.Value is { } record)
        {
            lock (sync) { group.Pending = null; Adopt(group, record, pending.Cancelled || !accepting); }
        }
        else
        {
            await store.ClearNodeNamingIntentAsync(pending.Intent.Snapshot.WorkspaceId, pending.Intent.Snapshot.GroupKey, pending.Intent.OperationId).ConfigureAwait(false);
            lock (sync) group.Pending = null;
        }
    }
    private Job Adopt(Group group, NodeNamingJob record, bool stopped)
    {
        var job = jobs.FirstOrDefault(j => j.Record.Id == record.Id);
        if (job is null) { job = new(record, group); jobs.Add(job); }
        group.Active = job;
        if (stopped) Stop(job);
        else if (Active(job.Record.Status)) job.Demand ??= Runtime.Scheduler.RegisterLogicalWork(GenerationPriority.Background);
        return job;
    }
    private void Dispatch()
    {
        lock (sync)
        {
            if (!initialized || !accepting || disposed || Runtime.IsAdmissionPaused) return;
            foreach (var pair in deferred.ToArray()) { deferred.Remove(pair.Key); var epoch = lifetime; Track(() => ProcessCurrentAsync(pair.Key, pair.Value, epoch)); }
            foreach (var g in groups.Values.Where(g => g.Active is null && g.Pending is { Saved: true, AcceptUnknown: false, Cancelled: false } && !g.Starting).ToArray())
            { g.Starting = true; Track(async () => { await control.WaitAsync().ConfigureAwait(false); try { await AcceptPendingAsync(g).ConfigureAwait(false); } catch { failure.Report(); } finally { control.Release(); lock (sync) g.Starting = false; } }); }
            foreach (var job in jobs.Where(j => Active(j.Record.Status) && !j.Busy && !j.Stopped && !j.Dirty && j.Completion is null).ToArray())
            { job.Busy = true; Track(() => ExecuteAsync(job)); }
        }
    }
    private static bool Active(GenerationStatus s) => s is GenerationStatus.Queued or GenerationStatus.Running;
    private static bool Success(SummaryOutcomeKind k) => k is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange;
    private static void Stop(Job job)
    {
        job.Stopped = true; job.Demand?.Dispose(); job.Cancellation.Cancel();
        if (Active(job.Record.Status) && job.Completion is null) { job.Record = job.Record with { Status = GenerationStatus.Stopped, ErrorCode = "Stopped" }; job.Dirty = true; }
    }
    public void ResumeAccepting() { lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); accepting = true; } Dispatch(); Notify(); }
    private void RuntimeChanged() { Dispatch(); Notify(); }
    private void Notify() { if (Changed is { } handlers) foreach (Action h in handlers.GetInvocationList()) try { h(); } catch {/* Observers cannot break durability. */} }
    private sealed class Group { public Job? Active; public Pending? Pending; public bool FollowupUsed, Starting; }
    private sealed class Pending(NodeNamingIntent intent) { public NodeNamingIntent Intent = intent; public Guid? ReplacedOperation; public bool Saved, AcceptUnknown, Cancelled; }
    private sealed class Job(NodeNamingJob record, Group group)
    {
        public NodeNamingJob Record = record; public Group Group = group; public CancellationTokenSource Cancellation = new(); public AiRequestScheduler.LogicalWork? Demand;
        public PreparedNodeNamingRequest? Prepared; public NodeNamingCompletion? Completion; public bool Busy, Stopped, Dirty, PreparationFailed;
    }
}
