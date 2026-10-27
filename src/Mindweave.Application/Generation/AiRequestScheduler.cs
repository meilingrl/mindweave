namespace Mindweave.Application.Generation;

/// <summary>One physical arbiter per app. A phase includes enumerator disposal, never its durable tail.</summary>
public sealed class AiRequestScheduler
{
    private readonly object sync = new();
    private readonly List<LogicalWork> work = [];
    private long sequence;
    private int running;
    private bool paused;
    public int ActivePhaseCount { get { lock (sync) return running; } }

    public LogicalWork RegisterLogicalWork(GenerationPriority priority)
    {
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        lock (sync) { var item = new LogicalWork(this, priority); work.Add(item); return item; }
    }

    public void SetPaused(bool value) { lock (sync) { paused = value; Dispatch(); } }

    private void Dispatch()
    {
        while (!paused && running < 2)
        {
            var next = work.Where(w => w.Phase is { Running: false }).OrderBy(w => w.Priority)
                .ThenBy(w => w.Phase!.Sequence).FirstOrDefault();
            if (next is null || work.Any(w => w.Pending && w.Priority < next.Priority)) return;
            var phase = next.Phase!;
            if (phase.Cancellation.IsCancellationRequested) { Cancel(next, phase); continue; }
            next.Pending = false; phase.Running = true; running++;
            _ = Task.Run(async () =>
            {
                Exception? failure = null;
                try { phase.Cancellation.ThrowIfCancellationRequested(); await phase.Execute().ConfigureAwait(false); }
                catch (Exception error) { failure = error; }
                lock (sync)
                {
                    // Publish future demand before releasing this phase: no low-priority transition gap.
                    next.Phase = null; next.Pending = !next.Disposed;
                    running--;
                    if (next.Disposed) work.Remove(next);
                    if (failure is OperationCanceledException) phase.Completion.TrySetCanceled();
                    else if (failure is not null) phase.Completion.TrySetException(failure);
                    else phase.Completion.TrySetResult();
                    Dispatch();
                }
                phase.Registration.Unregister();
            });
        }
    }

    private void Cancel(LogicalWork item, Phase phase)
    {
        lock (sync)
        {
            if (item.Phase != phase || phase.Running) return;
            item.Phase = null;
            phase.Completion.TrySetCanceled();
            phase.Registration.Unregister();
            Dispatch();
        }
    }

    public sealed class LogicalWork : IDisposable
    {
        private readonly AiRequestScheduler owner;
        internal readonly GenerationPriority Priority;
        internal bool Pending = true, Disposed;
        internal Phase? Phase;
        internal LogicalWork(AiRequestScheduler owner, GenerationPriority priority) { this.owner = owner; Priority = priority; }

        /// <summary>Offer only after readiness persistence and credentials are resolved. Await before any next phase.</summary>
        public Task RunPhaseAsync(Func<Task> executePhysicalAsync, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(executePhysicalAsync);
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                if (Phase is not null) throw new InvalidOperationException("A logical job already has a ready or running phase.");
                var phase = new Phase(++owner.sequence, executePhysicalAsync, cancellationToken);
                Phase = phase;
                phase.Registration = cancellationToken.Register(() => owner.Cancel(this, phase));
                owner.Dispatch();
                return phase.Completion.Task;
            }
        }
        public void Dispose()
        {
            lock (owner.sync)
            {
                if (Disposed) return;
                Disposed = true; Pending = false;
                if (Phase is { Running: false } phase) owner.Cancel(this, phase);
                if (Phase is null) owner.work.Remove(this);
                owner.Dispatch();
            }
        }
    }

    internal sealed class Phase(long sequence, Func<Task> execute, CancellationToken cancellation)
    {
        internal readonly long Sequence = sequence;
        internal readonly Func<Task> Execute = execute;
        internal readonly CancellationToken Cancellation = cancellation;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        internal bool Running;
    }
}
