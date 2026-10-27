using Mindweave.Application.Chat;
using Mindweave.Application.Generation;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

public sealed record KnowledgeSummarySnapshot(Guid TaskId, Guid OperationId, Guid WorkspaceId, Guid ConversationId,
    GenerationStatus Status, long Revision, string? Error, bool HasUnsavedChanges, SummaryContent? RetainedContent,
    SummaryGenerationResult? Result);
public sealed record KnowledgeSummaryCommitted(Guid WorkspaceId, Guid ConversationId, SummaryGenerationResult Result);

/// <summary>App-owned knowledge generation. Pages observe snapshots; they never own its cancellation or resources.</summary>
public sealed partial class KnowledgeSummaryCoordinator : IAsyncDisposable, IGenerationRuntimeParticipant
{
    private readonly IKnowledgeSummaryStore store;
    private readonly IChatStore chatStore;
    private readonly GenerationCoordinator chat;
    private readonly IChatTransport transport;
    private readonly ICredentialVault vault;
    private readonly IDisposable registration;
    private readonly AppGenerationRuntime.FailureSource failure;
    private readonly object sync = new();
    private readonly SemaphoreSlim acceptance = new(1, 1), persistence = new(1, 1);
    private readonly List<Job> jobs = [];
    private readonly List<Task> executions = [];
    private readonly Dictionary<Guid, AcceptanceIntent> unknownAcceptances = [];
    private int acceptingTails;
    private bool initialized, accepting, disposed, configurationReadFailed;
    public AppGenerationRuntime Runtime { get; }

    public KnowledgeSummaryCoordinator(IKnowledgeSummaryStore store, IChatStore chatStore, GenerationCoordinator chat,
        IChatTransport transport, ICredentialVault vault, AppGenerationRuntime runtime)
    {
        if (!ReferenceEquals(chat.Runtime, runtime)) throw new ArgumentException("Summary and Chat must share the application runtime.", nameof(runtime));
        this.store = store; this.chatStore = chatStore; this.chat = chat; this.transport = transport; this.vault = vault;
        Runtime = runtime; failure = runtime.CreateFailureSource(); registration = runtime.RegisterParticipant(this);
        runtime.Changed += RuntimeChanged;
    }

    public event Action? Changed;
    /// <summary>Only after atomic durable completion; candidate creation is not a current-version change.</summary>
    public event Action<KnowledgeSummaryCommitted>? Committed;
    public bool HasPersistenceFailure => Runtime.HasPersistenceFailure;
    public bool HasActiveOrPendingWork
    {
        get { lock (sync) return acceptingTails != 0 || unknownAcceptances.Count != 0 || jobs.Any(j => Active(j.Status) || j.Dirty || j.Completion is not null) || executions.Any(t => !t.IsCompleted); }
    }
    public IReadOnlyList<KnowledgeSummarySnapshot> Snapshots
    {
        get { lock (sync) return jobs.Select(j => new KnowledgeSummarySnapshot(j.Record.Id, j.Record.OperationId, j.Record.WorkspaceId,
            j.Record.ConversationId, j.Status, j.Record.Revision, j.Error, j.Dirty || j.Completion is not null,
            j.Completion?.Content, j.Result)).ToArray(); }
    }
    public IReadOnlyList<Guid> PendingAcceptanceOperations { get { lock (sync) return unknownAcceptances.Keys.ToArray(); } }

    public async Task InitializeAsync()
    {
        await acceptance.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); if (initialized) return; }
            var previous = await store.ListActiveKnowledgeSummaryJobsAsync().ConfigureAwait(false);
            await store.RecoverInterruptedKnowledgeSummariesAsync().ConfigureAwait(false);
            var recovered = new List<Job>();
            foreach (var old in previous)
            {
                var current = await store.LoadKnowledgeSummaryJobAsync(old.Id).ConfigureAwait(false)
                    ?? throw new InvalidDataException("总结恢复任务丢失。");
                recovered.Add(new(current));
            }
            lock (sync) { jobs.AddRange(recovered); initialized = accepting = true; }
        }
        catch { failure.Report(); throw; }
        finally { acceptance.Release(); Notify(); }
    }

    /// <summary>Caller may supply a stable operation ID. Unknown durable outcomes remain repairable by that identity.</summary>
    public async Task<SummaryOutcome<KnowledgeSummaryJob>> AcceptAsync(Guid conversationId, Guid? operationId = null)
    {
        lock (sync) { RequireAccepting(); acceptingTails++; }
        try
        {
            using var admission = await Runtime.EnterAcceptanceAsync().ConfigureAwait(false);
            using var preparing = Runtime.Scheduler.RegisterLogicalWork(GenerationPriority.Summary);
            await acceptance.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (sync) RequireAccepting();
                var op = operationId ?? Guid.NewGuid();
                // Replay a known operation with its original configuration, never the mutable active configuration.
                lock (sync)
                {
                    var known = jobs.FirstOrDefault(j => j.Record.OperationId == op);
                    if (known is not null) return known.Record.ConversationId == conversationId
                        ? new(SummaryOutcomeKind.NoChange, known.Record) : new(SummaryOutcomeKind.Conflict, Code: "SummaryOperationReused");
                }
                await chat.FlushPendingAsync().ConfigureAwait(false);
                if (chat.Snapshots.Any(s => s.ConversationId == conversationId && s.Kind == GenerationKind.Reply && Active(s.Status)))
                    return new(SummaryOutcomeKind.Blocked, Code: "KnowledgeSummaryActiveReply");
                AiConfiguration? configuration;
                try { configuration = await chatStore.LoadActiveConfigurationAsync().ConfigureAwait(false); }
                catch { lock (sync) configurationReadFailed = true; failure.Report(); throw; }
                if (configuration is null) return new(SummaryOutcomeKind.Rejected, Code: "MissingAiConfiguration");
                lock (sync) RequireAccepting();
                Runtime.RequireAdmission();
                var intent = new AcceptanceIntent(op, conversationId, configuration);
                SummaryOutcome<KnowledgeSummaryJob> outcome;
                try { outcome = await store.AcceptKnowledgeSummaryAsync(op, conversationId, configuration).ConfigureAwait(false); }
                catch { lock (sync) unknownAcceptances[op] = intent; failure.Report(); throw; }
                lock (sync)
                {
                    if (Success(outcome.Kind) && outcome.Value is { } accepted) AddJob(accepted);
                }
                return outcome;
            }
            finally { acceptance.Release(); }
        }
        finally { lock (sync) acceptingTails--; Dispatch(); Notify(); }
    }

    /// <summary>Freezes deltas/cancels immediately. A completion already being committed must reconcile its durable winner.</summary>
    public async Task StopAsync(Guid taskId)
    {
        Job? job;
        lock (sync)
        {
            job = jobs.SingleOrDefault(j => j.Record.Id == taskId);
            if (job is null) return;
            Stop(job);
        }
        Cancel(job); Notify();
        await FlushAsync(explicitRepair: false).ConfigureAwait(false);
    }

    public Task RetryPersistenceAsync() => Runtime.RetryPersistenceAsync();
    public void ResumeAccepting()
    {
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); if (!initialized) throw new InvalidOperationException("总结协调器尚未初始化。"); accepting = true; }
        Dispatch(); Notify();
    }
    private void RequireAccepting()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!initialized || !accepting) throw new InvalidOperationException("当前不能接受知识总结。");
        Runtime.RequireAdmission();
    }
    private Job AddJob(KnowledgeSummaryJob record)
    {
        var existing = jobs.FirstOrDefault(j => j.Record.Id == record.Id);
        if (existing is not null) return existing;
        var job = new Job(record);
        if (Active(record.Status)) job.Demand = Runtime.Scheduler.RegisterLogicalWork(GenerationPriority.Summary);
        jobs.Add(job); return job;
    }
    private static bool Active(GenerationStatus status) => status is GenerationStatus.Queued or GenerationStatus.Running;
    private static bool Success(SummaryOutcomeKind kind) => kind is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange;
    private static void Stop(Job job)
    {
        job.StopRequested = true;
        if (Active(job.Status) && job.Completion is null) { job.Status = GenerationStatus.Stopped; job.Error = "Stopped"; job.Dirty = true; }
    }
    private static void Cancel(Job job) { job.Demand?.Dispose(); job.Cancellation.Cancel(); }
    private void RuntimeChanged() { Dispatch(); Notify(); }
    private void Notify()
    {
        if (Changed is { } handlers) foreach (Action handler in handlers.GetInvocationList()) try { handler(); } catch { /* Observers cannot break durability. */ }
    }
    private void Publish(KnowledgeSummaryCommitted notification)
    {
        if (Committed is { } handlers) foreach (Action<KnowledgeSummaryCommitted> handler in handlers.GetInvocationList()) try { handler(notification); } catch { /* Observers own their errors. */ }
    }
    private sealed record AcceptanceIntent(Guid OperationId, Guid ConversationId, AiConfiguration Configuration);
    private sealed class Job(KnowledgeSummaryJob record)
    {
        public KnowledgeSummaryJob Record = record;
        public GenerationStatus Status = record.Status;
        public string? Error = record.ErrorCode;
        public CancellationTokenSource Cancellation { get; } = new();
        public AiRequestScheduler.LogicalWork? Demand;
        public PreparedKnowledgeSummaryRequest? Prepared;
        public SummaryGenerationCompletion? Completion;
        public SummaryGenerationResult? Result;
        public bool Busy, StopRequested, Dirty, Published, NeedsPreparationRepair;
    }
}
