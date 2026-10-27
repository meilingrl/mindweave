namespace Mindweave.Application.Generation;

/// <summary>Owns shared admission, source-scoped failure epochs, and aggregate shutdown; never owns participant stores.</summary>
public sealed class AppGenerationRuntime : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim acceptance = new(1, 1), lifecycle = new(1, 1);
    private readonly List<IGenerationRuntimeParticipant> participants = [];
    private readonly List<FailureSource> failures = [];
    private bool accepting = true, disposed, repairing, shuttingDown;
    private int pendingAcceptance;
    public AiRequestScheduler Scheduler { get; } = new();
    public event Action? Changed;
    public bool HasPersistenceFailure { get { lock (sync) return failures.Any(f => f.Failed); } }
    public bool IsAdmissionPaused { get { lock (sync) return !accepting || disposed || repairing || failures.Any(f => f.Failed); } }
    public bool HasActiveOrPendingWork
    {
        get
        {
            IGenerationRuntimeParticipant[] snapshot;
            lock (sync) { if (pendingAcceptance > 0) return true; snapshot = participants.ToArray(); }
            return Scheduler.ActivePhaseCount > 0 || snapshot.Any(p => p.HasActiveOrPendingWork);
        }
    }
    public IDisposable RegisterParticipant(IGenerationRuntimeParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!accepting || participants.Contains(participant)) throw new InvalidOperationException("Cannot register participant.");
            participants.Add(participant);
        }
        return new Release(() => { lock (sync) participants.Remove(participant); });
    }
    public FailureSource CreateFailureSource()
    {
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); var source = new FailureSource(this); failures.Add(source); return source; }
    }
    public async Task<IDisposable> EnterAcceptanceAsync(CancellationToken cancellationToken = default)
    {
        lock (sync) { RequireAdmission(); pendingAcceptance++; }
        Notify();
        bool entered = false;
        try
        {
            await acceptance.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
            lock (sync) RequireAdmission();
            return new Release(() => { lock (sync) pendingAcceptance--; acceptance.Release(); Notify(); });
        }
        catch { lock (sync) pendingAcceptance--; if (entered) acceptance.Release(); Notify(); throw; }
    }
    public void RequireAdmission() { lock (sync) if (IsAdmissionPaused) throw new InvalidOperationException("当前不能接受新请求，请完成保存或返回工作台。"); }

    public async Task RetryPersistenceAsync(Func<Task>? externalRepair = null)
    {
        // Preserve caller context for UI-owned draft repair.
        await lifecycle.WaitAsync();
        try
        {
            lock (sync) { repairing = true; UpdateScheduler(); }
            Notify();
            if (externalRepair is not null) await externalRepair();
            IGenerationRuntimeParticipant[] snapshot; lock (sync) snapshot = participants.ToArray();
            List<Exception> errors = [];
            foreach (var participant in snapshot)
                try { await participant.RepairPersistenceAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1) throw new AggregateException(errors);
            if (HasPersistenceFailure) throw new InvalidOperationException("仍有未修复的保存失败。");
        }
        finally { lock (sync) { repairing = false; UpdateScheduler(); } lifecycle.Release(); Notify(); }
    }
    public async Task ShutdownAsync(Func<Task>? externalRepair = null)
    {
        await lifecycle.WaitAsync();
        try
        {
            lock (sync) { shuttingDown = true; accepting = false; UpdateScheduler(); }
            Notify();
            await acceptance.WaitAsync();
            IGenerationRuntimeParticipant[] snapshot;
            try { lock (sync) snapshot = participants.ToArray(); } finally { acceptance.Release(); }
            await Task.WhenAll(snapshot.Select(async p => await p.ShutdownAsync().ConfigureAwait(false)));
            if (externalRepair is not null) await externalRepair();
            if (HasPersistenceFailure) throw new InvalidOperationException("仍有未保存内容。");
        }
        finally { lock (sync) shuttingDown = false; lifecycle.Release(); Notify(); }
    }
    public void ResumeAccepting()
    {
        IGenerationRuntimeParticipant[] snapshot;
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); if (shuttingDown) throw new InvalidOperationException("Shutdown is still draining."); accepting = true; snapshot = participants.ToArray(); UpdateScheduler(); }
        foreach (var participant in snapshot) participant.ResumeAccepting();
        Notify();
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync) if (disposed) return;
        await ShutdownAsync().ConfigureAwait(false);
        lock (sync) { disposed = true; UpdateScheduler(); }
    }
    private void UpdateScheduler() => Scheduler.SetPaused(!accepting || disposed || repairing || failures.Any(f => f.Failed));
    private void Notify()
    {
        if (Changed is { } handlers) foreach (Action handler in handlers.GetInvocationList()) try { handler(); } catch { }
    }
    public sealed class FailureSource
    {
        private readonly AppGenerationRuntime owner;
        private long epoch;
        internal bool Failed;
        internal FailureSource(AppGenerationRuntime owner) => this.owner = owner;
        public long Epoch { get { lock (owner.sync) return epoch; } }
        public bool HasFailure { get { lock (owner.sync) return Failed; } }
        public long Report()
        {
            long result; bool changed;
            lock (owner.sync) { result = ++epoch; changed = !Failed; Failed = true; owner.UpdateScheduler(); }
            if (changed) owner.Notify();
            return result;
        }
        public bool TryClear(long repairedEpoch)
        {
            lock (owner.sync)
            {
                if (epoch != repairedEpoch) return false;
                Failed = false; owner.UpdateScheduler();
            }
            owner.Notify(); return true;
        }
    }
    private sealed class Release(Action action) : IDisposable
    {
        private Action? release = action;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
