using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Storage;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;

namespace Mindweave.Application.Conversations;

public sealed class GenerationOptions
{
    /// <summary>Global limit on concurrent AI requests (architecture §6).</summary>
    public int Slots { get; init; } = 2;

    /// <summary>Minimum gap between partial-answer writes; only the newest text is written.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public enum GenerationPriority
{
    User,
    Background,
}

/// <summary>Work accepted for generation; the request (model snapshot and turns) is already frozen.</summary>
public abstract record GenerationWork(GenerationTaskRecord Task, ChatRequest Request, GenerationPriority Priority);

/// <param name="Plan">
/// When the plan summarizes older context, the summaries are produced in this task's slot before the answer
/// request, and <paramref name="Request"/>'s turns are replaced by the prepared ones.
/// </param>
public sealed record AnswerWork(GenerationTaskRecord Task, ChatRequest Request, LiveAnswer Live, ContextPlan? Plan = null, ContextBudget? Budget = null)
    : GenerationWork(Task, Request, GenerationPriority.User);

public sealed record TitleWork(GenerationTaskRecord Task, ChatRequest Request)
    : GenerationWork(Task, Request, GenerationPriority.Background);

public sealed record AnswerOutcome(ConversationId ConversationId, MessageId MessageId, MessageState State, string Text);

/// <summary>User-started work that has not finished (running or queued): what the exit prompt names, by kind.</summary>
public readonly record struct UnfinishedUserWork(int Answers, int Summaries)
{
    public bool Any => Answers + Summaries > 0;
}

/// <summary>What became of a usable summary output handed to <see cref="SummaryWork.Save"/>.</summary>
public enum SummarySaveResult
{
    /// <summary>Stored together with the task's completion.</summary>
    Saved,

    /// <summary>The output had no usable title and body; nothing was stored.</summary>
    Unusable,

    /// <summary>The task was no longer running (stopped meanwhile); nothing was stored.</summary>
    NotRunning,
}

/// <summary>
/// A summary of a conversation (S4). Like an answer it may first summarize older context in its own slot. The
/// output stays in memory until <paramref name="Save"/> stores it with the task's completion; a failed, stopped or
/// interrupted task leaves no version (MW-C06).
/// </summary>
public sealed record SummaryWork(GenerationTaskRecord Task, ChatRequest Request, ContextPlan? Plan, ContextBudget? Budget, Func<string, Task<SummarySaveResult>> Save)
    : GenerationWork(Task, Request, GenerationPriority.User);

/// <summary>Background naming of same-name summary nodes; <paramref name="Apply"/> receives the complete output.</summary>
public sealed record NamingWork(GenerationTaskRecord Task, ChatRequest Request, Func<string, Task> Apply)
    : GenerationWork(Task, Request, GenerationPriority.Background);

/// <summary>
/// Owns every AI request for the lifetime of the application, independent of any view: a two-slot
/// scheduler with user work ahead of background work, per-answer checkpoint loops, and guarded terminal
/// writes. A late chunk or checkpoint can never overwrite a terminal answer, and nothing is ever re-sent
/// automatically.
/// </summary>
public sealed partial class GenerationCoordinator : IGenerationExit, IAsyncDisposable
{
    private readonly IConversationStore _store;
    private readonly IChatClient _client;
    private readonly ICredentialStore _credentials;
    private readonly IAiRequestStore _requests;
    private readonly IClock _clock;
    private readonly GenerationOptions _options;
    private readonly ILogger<GenerationCoordinator> _logger;
    private readonly ContextCompressor? _compressor;

    private readonly Lock _gate = new();
    private readonly LinkedList<GenerationWork> _userQueue = new();
    private readonly LinkedList<GenerationWork> _backgroundQueue = new();
    private readonly Dictionary<GenerationTaskId, Running> _running = [];
    private readonly ConcurrentDictionary<MessageId, LiveAnswer> _live = new();
    private readonly StorageHealth _health;

    // Terminal writes that storage refused, each with its obligation: storage does not count as recovered until they are saved.
    private readonly ConcurrentQueue<KeptWrite> _unsaved = new();

    // Terminal writes that failed for a reason other than storage (a defect): kept so the exit reports them unsaved, but
    // they neither pause requests nor hold up recovery, which only storage itself can bring about.
    private readonly ConcurrentQueue<KeptWrite> _unsavedDefects = new();

    // One pass over the failed terminal writes at a time: the recovery retry and an exit's final save must not
    // overlap, or the exit could see an empty queue while a dequeued write is still failing.
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private bool _closed;
    private int _shutDown;

    public GenerationCoordinator(
        IConversationStore store,
        IChatClient client,
        ICredentialStore credentials,
        IAiRequestStore requests,
        StorageHealth health,
        IClock clock,
        GenerationOptions options,
        ILogger<GenerationCoordinator> logger,
        ContextCompressor? compressor = null)
    {
        ArgumentNullException.ThrowIfNull(health);
        _store = store;
        _client = client;
        _credentials = credentials;
        _requests = requests;
        _health = health;
        _clock = clock;
        _options = options;
        _logger = logger;
        _compressor = compressor;
        health.RegisterRetry(_ => FlushUnsavedAsync());
        health.Changed += (_, _) =>
        {
            // Accepted work never sent waits while storage is blocked (D2) and starts once it recovers.
            Pump();
            RaiseChanged();
        };
    }

    /// <summary>Any queue, task or storage-health change. Raised on worker threads.</summary>
    public event EventHandler? Changed;

    /// <summary>An answer reached a terminal state (after its terminal write was attempted).</summary>
    public event EventHandler<AnswerOutcome>? AnswerFinished;

    /// <summary>An automatic title was stored for this conversation.</summary>
    public event EventHandler<ConversationId>? TitleSaved;

    /// <summary>An automatic title task was stopped or cancelled before it produced a title (for example on exit).</summary>
    public event EventHandler<ConversationId>? TitleAbandoned;

    /// <summary>A summary or naming task reached a terminal state (after its terminal write was attempted).</summary>
    public event EventHandler<GenerationTaskRecord>? SummaryTaskFinished;

    /// <summary>Queued plus running tasks of every kind — what the exit prompt counts.</summary>
    public int UnfinishedCount
    {
        get
        {
            lock (_gate)
            {
                return _userQueue.Count + _backgroundQueue.Count + _running.Count;
            }
        }
    }

    /// <summary>Unfinished answers only (titles and other background work excluded).</summary>
    public int UnfinishedAnswerCount
    {
        get
        {
            lock (_gate)
            {
                return _userQueue.Count(w => w is AnswerWork) + _running.Values.Count(r => r.Work is AnswerWork);
            }
        }
    }

    /// <summary>
    /// Answers and summaries the user started, running or queued, counted separately. Titles and naming are
    /// background work the exit prompt does not ask about; they are cancelled silently.
    /// </summary>
    public UnfinishedUserWork UnfinishedUserWork
    {
        get
        {
            lock (_gate)
            {
                // Like HasUnfinishedAnswer: a running answer whose live state is already terminal is finished, even
                // though its slot is released only after the terminal write and its listeners.
                var pending = _userQueue.Concat(_backgroundQueue)
                    .Concat(_running.Values.Select(r => r.Work).Where(w => w is not AnswerWork a || IsLive(a)))
                    .ToList();
                return new UnfinishedUserWork(pending.Count(w => w is AnswerWork), pending.Count(w => w is SummaryWork));
            }
        }
    }

    /// <summary>True while new work is refused: between an exit's stop step and <see cref="Reopen"/>, or after shutdown.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    public LiveAnswer? Observe(MessageId answer) => _live.GetValueOrDefault(answer);

    public bool HasUnfinishedAnswer(ConversationId conversation)
    {
        lock (_gate)
        {
            // A running answer counts until its live state is terminal — not until its slot is released,
            // which happens a moment after the terminal write a retry may already be reacting to.
            return _userQueue.Any(w => w is AnswerWork && w.Task.ConversationId == conversation)
                || _running.Values.Any(r => r.Work is AnswerWork a && a.Task.ConversationId == conversation && IsLive(a));
        }
    }

    /// <summary>A summary of the conversation is queued or being generated (naming and titles do not count).</summary>
    public bool HasUnfinishedSummary(ConversationId conversation)
    {
        lock (_gate)
        {
            return _userQueue.Concat(_backgroundQueue).Any(w => w is SummaryWork && w.Task.ConversationId == conversation)
                || _running.Values.Any(r => r.Work is SummaryWork && r.Work.Task.ConversationId == conversation);
        }
    }

    private static bool IsLive(AnswerWork answer) => answer.Live.Snapshot().State is MessageState.Pending or MessageState.Streaming;

    /// <summary>Queues work whose task row is already stored. Throws once shutdown has begun.</summary>
    public void Enqueue(GenerationWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException("Generation is shutting down.");
            }

            if (work is AnswerWork answer)
            {
                _live[answer.Live.MessageId] = answer.Live;
            }

            (work.Priority == GenerationPriority.User ? _userQueue : _backgroundQueue).AddLast(work);
        }

        Pump();
        RaiseChanged();
    }

    /// <summary>Stops an answer: a queued one becomes cancelled, a running one stops and keeps what arrived.</summary>
    public async Task StopAsync(MessageId answer)
    {
        GenerationWork? queued = null;
        Running? running = null;
        lock (_gate)
        {
            queued = RemoveQueued(w => w is AnswerWork a && a.Live.MessageId == answer);
            if (queued is null)
            {
                running = _running.Values.FirstOrDefault(r => r.Work is AnswerWork a && a.Live.MessageId == answer);
            }
        }

        if (queued is AnswerWork cancelled)
        {
            await CancelQueuedAsync(cancelled).ConfigureAwait(false);
            RaiseChanged();
        }
        else if (running is not null)
        {
            await running.Cancellation.CancelAsync().ConfigureAwait(false);
            await running.Completion.ConfigureAwait(false);
        }
    }

    /// <summary>Stops a summary or naming task: a queued one becomes cancelled, a running one stops and stores nothing.</summary>
    public async Task StopTaskAsync(GenerationTaskId task)
    {
        GenerationWork? queued;
        Running? running = null;
        lock (_gate)
        {
            queued = RemoveQueued(w => w is not AnswerWork && w.Task.Id == task);
            if (queued is null)
            {
                running = _running.Values.FirstOrDefault(r => r.Work is not AnswerWork && r.Work.Task.Id == task);
            }
        }

        if (queued is not null)
        {
            await SaveAsync(() => FinishTask(task, GenerationTaskState.Cancelled, null, null)).ConfigureAwait(false);
            SummaryTaskFinished?.Invoke(this, queued.Task);
            RaiseChanged();
        }
        else if (running is not null)
        {
            await running.Cancellation.CancelAsync().ConfigureAwait(false);
            await running.Completion.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels the background work (titles and naming) of these conversations before they are archived or deleted
    /// (MW-C14): queued tasks become cancelled, running ones are stopped, and this waits until they have ended. A title
    /// stopped this way is asked for again after the next answer, like one stopped by an exit.
    /// </summary>
    public async Task CancelBackgroundAsync(IReadOnlySet<ConversationId> conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        var queued = new List<GenerationWork>();
        List<Running> running;
        lock (_gate)
        {
            while (RemoveQueued(w => w is TitleWork or NamingWork && conversations.Contains(w.Task.ConversationId)) is { } work)
            {
                queued.Add(work);
            }

            running = [.. _running.Values.Where(r => r.Work is TitleWork or NamingWork && conversations.Contains(r.Work.Task.ConversationId))];
        }

        foreach (var work in queued)
        {
            await SaveAsync(() => FinishTask(work.Task.Id, GenerationTaskState.Cancelled, null, null)).ConfigureAwait(false);
            if (work is TitleWork)
            {
                TitleAbandoned?.Invoke(this, work.Task.ConversationId);
            }
            else
            {
                SummaryTaskFinished?.Invoke(this, work.Task);
            }
        }

        foreach (var r in running)
        {
            await r.Cancellation.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(running.Select(r => r.Completion)).ConfigureAwait(false);
        if (queued.Count + running.Count > 0)
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// Exit sequence step 3 (architecture §12.3): close the entry, cancel queued work, stop running work keeping
    /// received text, and wait up to <paramref name="wait"/> for the stopped tasks to write their terminal states. An
    /// answer that does not acknowledge in time keeps its last checkpoint and is marked interrupted on the next start.
    /// Returns false when something was still running at the deadline. Unlike <see cref="ShutdownAsync"/>, failed
    /// terminal writes stay queued for retry and the entry can be opened again with <see cref="Reopen"/>.
    /// </summary>
    public async Task<bool> StopForExitAsync(TimeSpan wait)
    {
        List<GenerationWork> queued;
        List<Running> running;
        lock (_gate)
        {
            // Re-entrant: a second call finds nothing queued (the entry is closed) and only waits again.
            _closed = true;
            queued = [.. _userQueue, .. _backgroundQueue];
            _userQueue.Clear();
            _backgroundQueue.Clear();
            running = [.. _running.Values];
        }

        foreach (var work in queued)
        {
            switch (work)
            {
                case AnswerWork answer:
                    await CancelQueuedAsync(answer).ConfigureAwait(false);
                    break;
                default:
                    await SaveAsync(() => FinishTask(work.Task.Id, GenerationTaskState.Cancelled, null, null)).ConfigureAwait(false);
                    if (work is TitleWork)
                    {
                        TitleAbandoned?.Invoke(this, work.Task.ConversationId);
                    }
                    else
                    {
                        SummaryTaskFinished?.Invoke(this, work.Task);
                    }

                    break;
            }
        }

        foreach (var r in running)
        {
            await r.Cancellation.CancelAsync().ConfigureAwait(false);
        }

        var all = Task.WhenAll(running.Select(r => r.Completion));
        var acknowledged = await Task.WhenAny(all, Task.Delay(wait)).ConfigureAwait(false) == all;
        if (!acknowledged)
        {
            LogStopTimedOut(_logger, running.Count(r => !r.Completion.IsCompleted));
        }

        RaiseChanged();
        return acknowledged;
    }

    /// <summary>
    /// After an exit that could not finish (a save failed and the user went back): accept new work again. What the
    /// exit stopped or cancelled stays so; nothing is re-queued (architecture §12.3 step 5).
    /// </summary>
    public void Reopen()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _shutDown) != 0)
            {
                return;
            }

            _closed = false;
        }

        Pump();
        RaiseChanged();
    }

    /// <summary>Tries the terminal writes that failed earlier once more, now. True when none is left unsaved.</summary>
    public Task<bool> SaveUnsavedAsync() => FlushUnsavedAsync(includeDefects: true);

    /// <summary>
    /// Final stop when the process ends: <see cref="StopForExitAsync"/> (bounded by <paramref name="wait"/>, unbounded
    /// when null), then stop the retry loop and try failed terminal writes one last time. Idempotent.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan? wait = null)
    {
        if (Interlocked.Exchange(ref _shutDown, 1) != 0)
        {
            return;
        }

        await StopForExitAsync(wait ?? Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        await FlushUnsavedAsync(includeDefects: true).ConfigureAwait(false);
        RaiseChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _flushGate.Dispose();
    }

    private GenerationWork? RemoveQueued(Func<GenerationWork, bool> match)
    {
        foreach (var queue in new[] { _userQueue, _backgroundQueue })
        {
            for (var node = queue.First; node is not null; node = node.Next)
            {
                if (match(node.Value))
                {
                    queue.Remove(node);
                    return node.Value;
                }
            }
        }

        return null;
    }

    private void Pump()
    {
        lock (_gate)
        {
            while (!_closed && _running.Count < _options.Slots && _health.TryAdmit())
            {
                var queue = _userQueue.Count > 0 ? _userQueue : _backgroundQueue.Count > 0 ? _backgroundQueue : null;
                if (queue is null)
                {
                    return;
                }

                var work = queue.First!.Value;
                queue.RemoveFirst();
                var running = new Running(work, new CancellationTokenSource());
                _running[work.Task.Id] = running;

                // Assigned under the lock, so the run's own cleanup (which takes the lock) always sees it.
                running.Completion = Task.Run(() => RunAsync(running));
            }
        }
    }

    private async Task RunAsync(Running running)
    {
        try
        {
            switch (running.Work)
            {
                case AnswerWork answer:
                    await RunAnswerAsync(answer, running.Cancellation.Token).ConfigureAwait(false);
                    break;
                case TitleWork title:
                    await RunTitleAsync(title, running.Cancellation.Token).ConfigureAwait(false);
                    break;
                case SummaryWork summary:
                    await RunSummaryAsync(summary, running.Cancellation.Token).ConfigureAwait(false);
                    break;
                case NamingWork naming:
                    await RunNamingAsync(naming, running.Cancellation.Token).ConfigureAwait(false);
                    break;
            }
        }
#pragma warning disable CA1031 // A failed run must never take down the scheduler; it is logged and its slot freed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRunCrashed(_logger, ex, running.Work.Task.Id);
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(running.Work.Task.Id);
            }

            // The token source is not disposed: a concurrent StopAsync may still cancel it after the run ends.
            Pump();
            RaiseChanged();
        }
    }

    private async Task RunAnswerAsync(AnswerWork work, CancellationToken stop)
    {
        var live = work.Live;
        var task = work.Task;

        var marked = await SaveAsync(() => _store.MarkRunningAsync(task.Id, live.MessageId, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false);
        if (marked == SaveResult.NotApplied)
        {
            // Cancelled between dequeue and start; the cancellation already wrote the terminal state.
            return;
        }

        if (marked == SaveResult.Failed)
        {
            // Without a durable "running" row there is nowhere safe to put the answer: do not call the AI.
            await FinishAnswerAsync(work, MessageState.Failed, GenerationErrorKind.StorageUnavailable, null).ConfigureAwait(false);
            return;
        }

        live.MarkStreaming();
        RaiseChanged();

        var key = await ReadKeyAsync(task).ConfigureAwait(false);
        if (key.Error is { } keyError)
        {
            await FinishAnswerAsync(work, MessageState.Failed, keyError, key.Detail).ConfigureAwait(false);
            return;
        }

        var request = work.Request;
        if (work.Plan is { IsCompressed: true } plan)
        {
            // Summaries are a prerequisite run in this slot, so the answer never waits for a slot of its own making.
            live.SetPhase(AnswerPhase.Compressing);
            try
            {
                var compressor = _compressor ?? throw new ContextCompressionException("No context compressor is configured.");
                var prepared = await compressor.PrepareAsync(task, plan, work.Budget ?? await BudgetForAsync(task.Model, CancellationToken.None).ConfigureAwait(false), key.Secret!, RecordAsync, stop, _health.TryAdmit).ConfigureAwait(false);
                request = request with { Turns = prepared.Turns };
                live.SetPhase(AnswerPhase.Waiting);
                await compressor.RecordTaskInputAsync(task.Id, prepared.SummaryIds).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                await FinishAnswerAsync(work, MessageState.Stopped, null, null).ConfigureAwait(false);
                return;
            }
            catch (ContextCompressionException ex)
            {
                await FinishAnswerAsync(work, MessageState.Failed, GenerationErrorKind.CompressionFailed, ex.Message).ConfigureAwait(false);
                return;
            }
            catch (StorageAdmissionRefusedException ex)
            {
                await FinishAnswerAsync(work, MessageState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message).ConfigureAwait(false);
                return;
            }
        }

        if (!_health.TryAdmit())
        {
            // Storage failed while this task waited for its key or its summaries: the answer is not asked for (D2).
            await FinishAnswerAsync(work, MessageState.Failed, GenerationErrorKind.StorageUnavailable, StorageAdmissionRefusedException.Detail).ConfigureAwait(false);
            return;
        }

        using var checkpointStop = new CancellationTokenSource();
        var checkpoints = CheckpointLoopAsync(live, checkpointStop.Token);

        MessageState final;
        GenerationErrorKind? error = null;
        string? detail = null;
        var trace = new RequestTrace(task.Id, _clock, request);
        try
        {
            var completed = false;
            await foreach (var item in _client.StreamAsync(request, key.Secret!, stop).ConfigureAwait(false))
            {
                trace.Observe(item);
                switch (item)
                {
                    case ChatStreamEvent.TextDelta delta:
                        live.Append(delta.Text);
                        break;
                    case ChatStreamEvent.Completed:
                        completed = true;
                        break;
                }
            }

            if (completed)
            {
                final = MessageState.Complete;
            }
            else
            {
                final = MessageState.Failed;
                error = GenerationErrorKind.InvalidResponse;
                detail = "The service closed the stream without finishing the answer.";
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            final = MessageState.Stopped;
        }
        catch (ChatClientException ex)
        {
            final = MessageState.Failed;
            error = ex.Kind;
            detail = ex.Message;
            trace.Fail(ex);
        }

        await checkpointStop.CancelAsync().ConfigureAwait(false);
        await checkpoints.ConfigureAwait(false);
        await FinishAnswerAsync(work, final, error, detail).ConfigureAwait(false);
        await RecordAsync(trace.Finish(final == MessageState.Stopped, error, detail)).ConfigureAwait(false);
    }

    private async Task FinishAnswerAsync(AnswerWork work, MessageState state, GenerationErrorKind? error, string? detail)
    {
        var live = work.Live;
        var revision = live.Finish(state, error, detail);
        var snapshot = live.Snapshot();
        await SaveAsync(() => _store.FinishAnswerAsync(work.Task.Id, live.MessageId, snapshot.Blocks, revision, state, error, detail, _clock.UtcNow, CancellationToken.None), retryOnFailure: true)
            .ConfigureAwait(false);
        LogAnswerFinished(_logger, work.Task.Id, state, error);
        AnswerFinished?.Invoke(this, new AnswerOutcome(live.ConversationId, live.MessageId, state, snapshot.Text));
    }

    private async Task CancelQueuedAsync(AnswerWork work)
    {
        var revision = work.Live.Finish(MessageState.Cancelled, null, null);
        await SaveAsync(
                () => _store.FinishAnswerAsync(work.Task.Id, work.Live.MessageId, ContentBlocks.FromText(string.Empty), revision, MessageState.Cancelled, null, null, _clock.UtcNow, CancellationToken.None),
                retryOnFailure: true)
            .ConfigureAwait(false);
        AnswerFinished?.Invoke(this, new AnswerOutcome(work.Live.ConversationId, work.Live.MessageId, MessageState.Cancelled, string.Empty));
    }

    private async Task CheckpointLoopAsync(LiveAnswer live, CancellationToken cancellationToken)
    {
        long written = 0;
        try
        {
            while (true)
            {
                await Task.Delay(_options.CheckpointInterval, cancellationToken).ConfigureAwait(false);
                var snapshot = live.Snapshot();
                if (snapshot.State != MessageState.Streaming || snapshot.Revision <= written)
                {
                    continue;
                }

                // One write at a time per answer: a slow disk coalesces checkpoints instead of queuing them.
                if (await SaveAsync(() => _store.CheckpointAsync(live.MessageId, snapshot.Blocks, snapshot.Revision, CancellationToken.None)).ConfigureAwait(false) != SaveResult.Failed)
                {
                    written = snapshot.Revision;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunTitleAsync(TitleWork work, CancellationToken stop)
    {
        var task = work.Task;
        if (await SaveAsync(() => _store.MarkRunningAsync(task.Id, null, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false) != SaveResult.Applied)
        {
            return;
        }

        var key = await ReadKeyAsync(task).ConfigureAwait(false);
        if (key.Error is { } keyError)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, keyError, key.Detail)).ConfigureAwait(false);
            return;
        }

        try
        {
            // Like summaries and naming, a title is used only from a normal, non-empty finish; a cut-off one is not.
            var (output, problem) = await RequestCompleteTextAsync(task, work.Request, key.Secret!, stop).ConfigureAwait(false);
            var title = output is null ? null : ConversationRules.CleanGeneratedTitle(output);
            if (title is not null)
            {
                if (await SaveAsync(() => _store.SetAutoTitleAsync(task.ConversationId, title, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false) == SaveResult.Applied)
                {
                    TitleSaved?.Invoke(this, task.ConversationId);
                }
            }

            await SaveAsync(() => FinishTask(task.Id, title is null ? GenerationTaskState.Failed : GenerationTaskState.Completed, title is null ? GenerationErrorKind.InvalidResponse : null, title is null ? problem : null))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Stopped, null, null)).ConfigureAwait(false);
            TitleAbandoned?.Invoke(this, task.ConversationId);
        }
        catch (ChatClientException ex)
        {
            // Titles are best effort: keep the current name, never retry automatically.
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, ex.Kind, ex.Message)).ConfigureAwait(false);
        }
        catch (StorageAdmissionRefusedException ex)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message)).ConfigureAwait(false);
        }
    }

    private async Task RunSummaryAsync(SummaryWork work, CancellationToken stop)
    {
        var task = work.Task;
        try
        {
            if (await SaveAsync(() => _store.MarkRunningAsync(task.Id, null, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false) != SaveResult.Applied)
            {
                return;
            }

            var key = await ReadKeyAsync(task).ConfigureAwait(false);
            if (key.Error is { } keyError)
            {
                await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, keyError, key.Detail)).ConfigureAwait(false);
                return;
            }

            var request = work.Request;
            if (work.Plan is { IsCompressed: true } plan)
            {
                var compressor = _compressor ?? throw new ContextCompressionException("No context compressor is configured.");
                var prepared = await compressor.PrepareAsync(task, plan, work.Budget ?? await BudgetForAsync(task.Model, CancellationToken.None).ConfigureAwait(false), key.Secret!, RecordAsync, stop, _health.TryAdmit).ConfigureAwait(false);
                request = request with { Turns = [.. request.Turns.TakeWhile(t => t.Role == ChatRole.System), .. prepared.Turns] };
                await compressor.RecordTaskInputAsync(task.Id, prepared.SummaryIds).ConfigureAwait(false);
            }

            var (output, problem) = await RequestCompleteTextAsync(task, request, key.Secret!, stop).ConfigureAwait(false);
            if (output is null)
            {
                await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, GenerationErrorKind.InvalidResponse, problem)).ConfigureAwait(false);
                return;
            }

            SummarySaveResult saved;
            try
            {
                saved = await work.Save(output).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The version could not be stored: the task fails visibly and keeps nothing.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSummaryNotSaved(_logger, ex, task.Id);

                // Only storage refusing writes pauses requests; any other error is a defect that fails this task (the
                // version was not stored) without pausing everything else.
                if (ex is StorageWriteException storage)
                {
                    _health.Report(storage);
                }

                await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message), retryOnFailure: true).ConfigureAwait(false);
                return;
            }

            if (saved == SummarySaveResult.Unusable)
            {
                await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, GenerationErrorKind.InvalidResponse, "总结缺少标题或正文。")).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Stopped, null, null)).ConfigureAwait(false);
        }
        catch (ContextCompressionException ex)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, GenerationErrorKind.CompressionFailed, ex.Message)).ConfigureAwait(false);
        }
        catch (ChatClientException ex)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, ex.Kind, ex.Message)).ConfigureAwait(false);
        }
        catch (StorageAdmissionRefusedException ex)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            SummaryTaskFinished?.Invoke(this, task);
        }
    }

    private async Task RunNamingAsync(NamingWork work, CancellationToken stop)
    {
        var task = work.Task;
        try
        {
            if (await SaveAsync(() => _store.MarkRunningAsync(task.Id, null, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false) != SaveResult.Applied)
            {
                return;
            }

            var key = await ReadKeyAsync(task).ConfigureAwait(false);
            if (key.Error is { } keyError)
            {
                await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, keyError, key.Detail)).ConfigureAwait(false);
                return;
            }

            // Names are best effort: a failure keeps the current names and is never retried automatically.
            var (output, problem) = await RequestCompleteTextAsync(task, work.Request, key.Secret!, stop).ConfigureAwait(false);
            if (output is not null)
            {
                await work.Apply(output).ConfigureAwait(false);
            }

            await SaveAsync(() => FinishTask(task.Id, output is null ? GenerationTaskState.Failed : GenerationTaskState.Completed, output is null ? GenerationErrorKind.InvalidResponse : null, problem))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Stopped, null, null)).ConfigureAwait(false);
        }
        catch (ChatClientException ex)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, ex.Kind, ex.Message)).ConfigureAwait(false);
        }
        catch (StorageAdmissionRefusedException ex)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            SummaryTaskFinished?.Invoke(this, task);
        }
    }

    /// <summary>
    /// One request whose output the program uses directly: only a normal, non-empty finish counts (D-015); a cut-off
    /// output is never used. Returns the text, or null with the reason.
    /// </summary>
    private async Task<(string? Text, string? Problem)> RequestCompleteTextAsync(GenerationTaskRecord task, ChatRequest request, string apiKey, CancellationToken stop)
    {
        if (!_health.TryAdmit())
        {
            throw new StorageAdmissionRefusedException();
        }

        var trace = new RequestTrace(task.Id, _clock, request);
        var text = new System.Text.StringBuilder();
        ChatStreamEvent.Completed? completed = null;
        try
        {
            await foreach (var item in _client.StreamAsync(request, apiKey, stop).ConfigureAwait(false))
            {
                trace.Observe(item);
                if (item is ChatStreamEvent.TextDelta delta)
                {
                    text.Append(delta.Text);
                }
                else if (item is ChatStreamEvent.Completed done)
                {
                    completed = done;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await RecordAsync(trace.Finish(stopped: true, null, null)).ConfigureAwait(false);
            throw;
        }
        catch (ChatClientException ex)
        {
            trace.Fail(ex);
            await RecordAsync(trace.Finish(stopped: false, ex.Kind, ex.Message)).ConfigureAwait(false);
            throw;
        }

        var output = text.ToString().Trim();
        var problem = completed is null ? "回答流未正常结束。"
            : completed.Truncated ? "输出达到上限被截断，未保存。"
            : output.Length == 0 ? "服务返回了空内容。"
            : null;
        await RecordAsync(trace.Finish(stopped: false, problem is null ? null : GenerationErrorKind.InvalidResponse, problem)).ConfigureAwait(false);
        return problem is null ? (output, null) : (null, problem);
    }

    /// <summary>
    /// The model's context budget, with the calibration learned from what this address and model billed for recent
    /// requests (<see cref="TokenCalibration"/>). If the requests cannot be read, the plain estimate is used: requests
    /// are not held up for a diagnostic table, and the budget's margin still applies.
    /// </summary>
    public async Task<ContextBudget> BudgetForAsync(ModelSnapshot model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var calibration = TokenCalibration.None;
        try
        {
            var samples = await _requests.RecentTokenSamplesAsync(model.BaseUrl, model.Model, TokenCalibration.SampleSize, cancellationToken).ConfigureAwait(false);
            calibration = TokenCalibration.Fit([.. samples.Select(s => (s.Estimate, s.Reported))]);
        }
#pragma warning disable CA1031 // Learned counts only refine the budget; without them the plain estimate is used.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogCalibrationFailed(_logger, ex, model.Model);
        }

        return ContextBudget.For(model, calibration);
    }

    /// <summary>Diagnostics only: a failed trace write is logged and does not count as a storage failure.</summary>
    private async Task RecordAsync(AiRequestRecord record)
    {
        try
        {
            await _requests.AddAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A lost trace must not affect the answer or the storage-health state.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTraceFailed(_logger, ex, record.TaskId);
        }
    }

    private async Task<(string? Secret, GenerationErrorKind? Error, string? Detail)> ReadKeyAsync(GenerationTaskRecord task)
    {
        try
        {
            var secret = await _credentials.ReadAsync(task.Model.CredentialRef, CancellationToken.None).ConfigureAwait(false);
            return secret is null
                ? (null, GenerationErrorKind.NotConfigured, "The API key for this connection is missing.")
                : (secret, null, null);
        }
        catch (CredentialUnavailableException ex)
        {
            return (null, GenerationErrorKind.CredentialUnavailable, ex.Message);
        }
    }

    private async Task<bool> FinishTask(GenerationTaskId id, GenerationTaskState state, GenerationErrorKind? error, string? detail)
    {
        await _store.FinishTaskAsync(id, state, error, detail, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private enum SaveResult
    {
        Applied,
        NotApplied,
        Failed,
    }

    /// <summary>
    /// Runs one core write. A storage failure blocks new requests until storage recovers (see <see cref="IsStorageBlocked"/>);
    /// terminal writes are kept and retried in the background so a finished answer is not lost while the app runs. Any
    /// other failure is a defect: the write is kept for the exit to report, and requests are not paused.
    /// </summary>
    private async Task<SaveResult> SaveAsync(Func<Task<bool>> write, bool retryOnFailure = false)
    {
        try
        {
            var applied = await write().ConfigureAwait(false);
            return applied ? SaveResult.Applied : SaveResult.NotApplied;
        }
        catch (StorageWriteException ex)
        {
            if (retryOnFailure)
            {
                _unsaved.Enqueue(new KeptWrite(write, _health.Retain(ex)));
            }
            else
            {
                _health.Report(ex);
            }

            return SaveResult.Failed;
        }
#pragma warning disable CA1031 // A defect in one write must not take down the run; the content stays in memory and is reported.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWriteDefect(_logger, ex);
            if (retryOnFailure)
            {
                _unsavedDefects.Enqueue(new KeptWrite(write, null));
            }

            return SaveResult.Failed;
        }
    }

    /// <summary>
    /// One pass over the failed terminal writes kept for storage. True when none is left unsaved at the end of this pass.
    /// With <paramref name="includeDefects"/> (the exit's save) the writes that failed for other reasons are tried too
    /// and count as unsaved; recovery leaves them alone, as storage coming back does not fix them.
    /// </summary>
    private async Task<bool> FlushUnsavedAsync(bool includeDefects = false)
    {
        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RetryAsync(_unsaved).ConfigureAwait(false);
            if (includeDefects)
            {
                await RetryAsync(_unsavedDefects).ConfigureAwait(false);
            }

            // Read while no other pass can have a write out of the queue.
            return _unsaved.IsEmpty && (!includeDefects || _unsavedDefects.IsEmpty);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task RetryAsync(ConcurrentQueue<KeptWrite> queue)
    {
        var count = queue.Count;
        for (var i = 0; i < count && queue.TryDequeue(out var kept); i++)
        {
            // While it is out of the queue its obligation stays open, so recovery cannot be declared over it.
            try
            {
                await kept.Write().ConfigureAwait(false);
                kept.Obligation?.Resolve();
            }
            catch (StorageWriteException ex)
            {
                if (kept.Obligation is { IsResolved: false })
                {
                    _health.Report(ex);
                    _unsaved.Enqueue(kept);
                }
                else
                {
                    _unsaved.Enqueue(kept with { Obligation = _health.Retain(ex) });
                }
            }
#pragma warning disable CA1031 // Still failing: keep it for the next attempt.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Storage is no longer what stops it; the content stays kept, now as a defect the exit reports.
                LogWriteDefect(_logger, ex);
                kept.Obligation?.Resolve();
                _unsavedDefects.Enqueue(kept with { Obligation = null });
            }
        }
    }

    /// <summary>A terminal write kept after it failed, with the storage obligation it holds open (none for a defect).</summary>
    private sealed record KeptWrite(Func<Task<bool>> Write, StorageObligation? Obligation);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Running(GenerationWork work, CancellationTokenSource cancellation)
    {
        public GenerationWork Work { get; } = work;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Completion { get; set; } = Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Answer task {TaskId} finished as {State} ({Error})")]
    private static partial void LogAnswerFinished(ILogger logger, GenerationTaskId taskId, MessageState state, GenerationErrorKind? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "A core write failed for a reason other than storage; its content is kept in memory and reported at exit")]
    private static partial void LogWriteDefect(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Summary of task {TaskId} could not be stored")]
    private static partial void LogSummaryNotSaved(ILogger logger, Exception exception, GenerationTaskId taskId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request trace for task {TaskId} was not saved")]
    private static partial void LogTraceFailed(ILogger logger, Exception exception, GenerationTaskId taskId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token counts for model {Model} could not be read; the plain estimate is used")]
    private static partial void LogCalibrationFailed(ILogger logger, Exception exception, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} stopped tasks did not finish within the exit wait; their last checkpoints stand")]
    private static partial void LogStopTimedOut(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Generation task {TaskId} crashed")]
    private static partial void LogRunCrashed(ILogger logger, Exception exception, GenerationTaskId taskId);
}

/// <summary>
/// Storage became unavailable after a task started and before one of its requests was sent: the request is not sent and
/// the task fails visibly (architecture review D2). Requests already in flight finish.
/// </summary>
internal sealed class StorageAdmissionRefusedException : Exception
{
    public const string Detail = "本地数据暂时无法保存，请求未发送。";

    public StorageAdmissionRefusedException()
        : base(Detail)
    {
    }

    public StorageAdmissionRefusedException(string message)
        : base(message)
    {
    }

    public StorageAdmissionRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
