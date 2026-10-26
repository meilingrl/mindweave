using System.Text;
using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

/// <summary>One application lifetime; page navigation only changes who observes snapshots.</summary>
public sealed partial class GenerationCoordinator : IAsyncDisposable
{
    private readonly IChatStore store;
    private readonly IChatTransport transport;
    private readonly ICredentialVault vault;
    private readonly object sync = new();
    private readonly SemaphoreSlim acceptance = new(1, 1);
    private readonly SemaphoreSlim persistence = new(1, 1);
    private readonly SemaphoreSlim repairs = new(1, 1);
    private readonly List<Job> jobs = [];
    private readonly HashSet<Guid> pendingTitles = [];
    private readonly CancellationTokenSource lifetime = new();
    private Task? checkpoints;
    private bool initialized, accepting, persistenceFailure, externalPersistenceFailure, disposed;
    // Read only while holding sync. External failures pause new work, not healthy checkpoints.
    private bool PersistencePaused => persistenceFailure || externalPersistenceFailure;
    private int slots;

    public GenerationCoordinator(IChatStore store, IChatTransport transport, ICredentialVault vault)
    { this.store = store; this.transport = transport; this.vault = vault; }

    public event Action? Changed;
    public IReadOnlyList<GenerationSnapshot> Snapshots
    { get { lock (sync) return jobs.Select(Snapshot).ToArray(); } }
    public bool HasActiveWork
    { get { lock (sync) return slots > 0 || jobs.Any(job => Active(job.Status)); } }
    public bool HasPersistenceFailure
    { get { lock (sync) return PersistencePaused; } }

    public async Task InitializeAsync()
    {
        await acceptance.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); if (initialized) return; }
            var interrupted = await store.ListActiveGenerationsAsync().ConfigureAwait(false);
            await store.RecoverInterruptedAsync().ConfigureAwait(false);
            var restored = new List<Job>();
            foreach (var old in interrupted)
            {
                var recovered = (await store.LoadGenerationAsync(old.Id).ConfigureAwait(false))!;
                var messages = await store.ReadMessagesAsync(old.ConversationId).ConfigureAwait(false);
                var context = await store.ReadContextAsync(old.ConversationId).ConfigureAwait(false);
                // Previously accepted title/reply manifests can still name an older own attempt.
                // Ancestors are admitted only through this conversation's frozen context.
                var byId = context.Messages.Where(message => message.ConversationId != old.ConversationId)
                    .Concat(messages).ToDictionary(message => message.Id);
                var input = recovered.InputMessageIds.Select(id => byId.TryGetValue(id, out var message)
                    ? message : throw new InvalidDataException("生成输入不属于本会话或冻结祖先。")).ToArray();
                var work = new GenerationWork(recovered, input, recovered.Kind == GenerationKind.Reply ? context.SourceQuote : null);
                var job = new Job(work) { Status = recovered.Status, Error = recovered.ErrorCode };
                job.Body.Append(messages.FirstOrDefault(m => m.Id == recovered.AnswerMessageId)?.Body ?? "");
                restored.Add(job);
            }
            lock (sync)
            {
                jobs.AddRange(restored);
                persistenceFailure = false;
                initialized = accepting = true;
                checkpoints = Task.Run(CheckpointLoopAsync);
            }
        }
        catch { lock (sync) persistenceFailure = true; throw; }
        finally { acceptance.Release(); Notify(); }
    }

    public Task<Guid> SendAsync(Guid conversationId, string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) throw new ArgumentException("请输入问题。", nameof(userText));
        return AcceptAsync(conversationId, userText);
    }
    public Task<Guid> RetryAsync(Guid conversationId) => AcceptAsync(conversationId, null);

    private async Task<Guid> AcceptAsync(Guid conversationId, string? userText)
    {
        await acceptance.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                RequireAccepting();
                if (jobs.Any(job => job.Work.Task.ConversationId == conversationId && job.Work.Task.Kind == GenerationKind.Reply && Active(job.Status)))
                    throw new InvalidOperationException("此会话已有未结束回答。");
            }
            // A terminal in-memory answer can precede its final disk write. Flush before
            // reading context so budget preflight and acceptance see its complete, fixed body.
            await FlushAsync(throwOnFailure: true).ConfigureAwait(false);
            var configuration = await store.LoadActiveConfigurationAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("请先保存 AI 配置。");
            var context = ChatRequestBudget.PrepareReplyContext(await store.ReadContextAsync(conversationId).ConfigureAwait(false), conversationId, userText is null);
            var requestInput = ChatRequestBudget.Inputs(context.Messages, context.SourceQuote).ToList();
            if (userText is not null) requestInput.Add(new("user", userText));
            if (!ChatRequestBudget.Fits(configuration, requestInput))
                throw new InvalidOperationException("输入超出配置的上下文预算，请缩短输入或调整预算。");
            GenerationWork work;
            try
            {
                Task<GenerationWork> accepted;
                lock (sync)
                {
                    RequireAccepting();
                    accepted = store.AcceptReplyAsync(conversationId, userText, configuration);
                }
                work = await accepted.ConfigureAwait(false);
            }
            catch (Exception error) when (error is not (ArgumentException or InvalidOperationException))
            { MarkPersistenceFailure(); throw; }
            lock (sync) jobs.Add(new(work));
            return work.Task.Id;
        }
        finally { acceptance.Release(); Dispatch(); Notify(); }
    }

    public async Task StopAsync(Guid conversationId)
    {
        Job[] stopped;
        lock (sync)
        {
            stopped = jobs.Where(j => j.Work.Task.ConversationId == conversationId && Active(j.Status)).ToArray();
            foreach (var job in stopped) Terminate(job, GenerationStatus.Stopped, "stopped");
        }
        // Delta gates are already closed before invoking cancellation callbacks or waiting for storage.
        foreach (var job in stopped) job.Cancellation.Cancel();
        Notify();
        await FlushAsync(throwOnFailure: true).ConfigureAwait(false);
        Dispatch();
    }

    /// <summary>Report a core save failure discovered outside the coordinator. Running streams
    /// keep their memory/checkpoints; acceptance, queued dispatch and new titles pause.</summary>
    public void ReportPersistenceFailure()
    {
        bool changed;
        lock (sync)
        {
            if (disposed || externalPersistenceFailure) return;
            changed = !persistenceFailure;
            externalPersistenceFailure = true;
        }
        // Repeated reports are idempotent and cannot create a Changed/refresh/report loop.
        if (changed) Notify();
    }

    public Task RetryPersistenceAsync() => RetryPersistenceCoreAsync(null);

    /// <summary>Repair all external core state while the gate remains closed, then flush
    /// coordinator state and release the gate. A failed repair leaves the pause intact.</summary>
    public Task RetryPersistenceAsync(Func<Task> repairExternalPersistence)
    {
        ArgumentNullException.ThrowIfNull(repairExternalPersistence);
        return RetryPersistenceCoreAsync(repairExternalPersistence);
    }

    private async Task RetryPersistenceCoreAsync(Func<Task>? repairExternalPersistence)
    {
        // The supplied repair may project caller-owned UI state. Preserve its caller
        // context across contention; only coordinator-only awaits below leave it.
        await repairs.WaitAsync();
        try
        {
            lock (sync)
                if (externalPersistenceFailure && repairExternalPersistence is null)
                    throw new InvalidOperationException("工作台仍有未保存内容，请从工作台重试保存。");
            if (repairExternalPersistence is not null)
            {
                try { await repairExternalPersistence().ConfigureAwait(false); }
                catch { ReportPersistenceFailure(); throw; }
            }
            await FlushAsync(throwOnFailure: true, explicitRetry: true).ConfigureAwait(false);
            lock (sync) externalPersistenceFailure = false;
            Notify();
            await AcceptTitlesAsync().ConfigureAwait(false);
            Dispatch();
        }
        finally { repairs.Release(); }
    }

    public async Task ShutdownAsync()
    {
        await acceptance.WaitAsync().ConfigureAwait(false);
        Task[] executions;
        try
        {
            Job[] stopped;
            lock (sync)
            {
                accepting = false;
                pendingTitles.Clear();
                stopped = jobs.Where(j => Active(j.Status)).ToArray();
                foreach (var job in stopped) Terminate(job, GenerationStatus.Stopped, "stopped");
                executions = jobs.Where(j => j.Execution is not null).Select(j => j.Execution!).ToArray();
            }
            foreach (var job in stopped) job.Cancellation.Cancel();
            Notify();
        }
        finally { acceptance.Release(); }
        // A failed flush propagates while this instance and its latest memory remain alive for Retry/Return.
        await FlushAsync(throwOnFailure: true, explicitRetry: true).ConfigureAwait(false);
        await Task.WhenAll(executions).ConfigureAwait(false);
        await FlushAsync(throwOnFailure: true).ConfigureAwait(false);
    }

    public void ResumeAccepting()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!initialized) throw new InvalidOperationException("协调器尚未初始化。");
            accepting = true;
        }
        Dispatch(); Notify();
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (disposed) return; }
        await ShutdownAsync().ConfigureAwait(false);
        lock (sync) disposed = true;
        lifetime.Cancel();
        if (checkpoints is not null) await checkpoints.ConfigureAwait(false);
        if (growthCheckpoint is not null) await growthCheckpoint.ConfigureAwait(false);
        foreach (var job in jobs) job.Cancellation.Dispose();
        lifetime.Dispose();
    }

    private void RequireAccepting()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!initialized || !accepting) throw new InvalidOperationException("当前不能接受新请求。");
        if (PersistencePaused) throw new InvalidOperationException("保存失败，请先重试保存。");
    }
    private static bool Active(GenerationStatus status) => status is GenerationStatus.Queued or GenerationStatus.Running;
    private static GenerationSnapshot Snapshot(Job job) => new(job.Work.Task.Id, job.Work.Task.ConversationId,
        job.Work.Task.AnswerMessageId, job.Work.Task.Kind, job.Status,
        job.Work.Task.Kind == GenerationKind.Title ? "" : job.Body.ToString(), job.Revision, job.Error, job.Dirty);
    private static void Terminate(Job job, GenerationStatus status, string? error)
    { job.Status = status; job.Error = error; job.Revision = checked(job.Revision + 1); job.Dirty = true; }
    private void MarkPersistenceFailure() { lock (sync) persistenceFailure = true; Notify(); }
    private void Notify()
    {
        // Observers cannot break scheduling/persistence; Desktop marshals this notification onto its dispatcher.
        if (Changed is { } handlers)
            foreach (Action handler in handlers.GetInvocationList()) { try { handler(); } catch { /* Observer isolation. */ } }
    }

    private sealed class Job(GenerationWork work)
    {
        public GenerationWork Work { get; } = work;
        public StringBuilder Body { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public GenerationStatus Status = work.Task.Status;
        public long Revision = work.Task.Revision;
        public string? Error = work.Task.ErrorCode, ActualModel = work.Task.ActualModelId;
        public bool Dirty, HasSlot, StreamEnded;
        public int PersistedLength;
        public Task? Execution;
    }
}
