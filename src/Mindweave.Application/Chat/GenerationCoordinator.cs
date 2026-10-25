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
    private readonly List<Job> jobs = [];
    private readonly Dictionary<Guid, AiConfiguration> pendingTitles = [];
    private readonly CancellationTokenSource lifetime = new();
    private Task? checkpoints;
    private bool initialized, accepting, persistenceFailure, disposed;
    private int slots;

    public GenerationCoordinator(IChatStore store, IChatTransport transport, ICredentialVault vault)
    { this.store = store; this.transport = transport; this.vault = vault; }

    public event Action? Changed;
    public IReadOnlyList<GenerationSnapshot> Snapshots
    { get { lock (sync) return jobs.Select(Snapshot).ToArray(); } }
    public bool HasActiveWork
    { get { lock (sync) return slots > 0 || jobs.Any(job => Active(job.Status)); } }
    public bool HasPersistenceFailure
    { get { lock (sync) return persistenceFailure; } }

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
                var work = new GenerationWork(recovered, messages.Where(m => recovered.InputMessageIds.Contains(m.Id)).ToArray());
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
            var messages = await store.ReadMessagesAsync(conversationId).ConfigureAwait(false);
            var input = ChatHistory.LatestInput(messages).ToList();
            if (userText is null)
            {
                if (messages.Count == 0 || messages[^1].Role != MessageRole.Assistant)
                    throw new InvalidOperationException("只能重试会话最后一条回答。");
                input.RemoveAll(m => m.Role == MessageRole.Assistant && m.ReplyToUserId == messages[^1].ReplyToUserId);
            }
            var requestInput = ChatRequestBudget.Inputs(input).ToList();
            if (userText is not null) requestInput.Add(new("user", userText));
            if (!ChatRequestBudget.Fits(configuration, requestInput))
                throw new InvalidOperationException("输入超出配置的上下文预算，请缩短输入或调整预算。");
            lock (sync) RequireAccepting();
            GenerationWork work;
            try { work = await store.AcceptReplyAsync(conversationId, userText, configuration).ConfigureAwait(false); }
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

    public async Task RetryPersistenceAsync()
    {
        await FlushAsync(throwOnFailure: true, explicitRetry: true).ConfigureAwait(false);
        await AcceptTitlesAsync().ConfigureAwait(false);
        Dispatch();
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
        if (persistenceFailure) throw new InvalidOperationException("保存失败，请先重试保存。");
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
