using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

/// <summary>
/// Owns every AI request for the lifetime of the application, independent of any view: a two-slot
/// scheduler with user work ahead of background work, per-answer checkpoint loops, and guarded terminal
/// writes. A late chunk or checkpoint can never overwrite a terminal answer, and nothing is ever re-sent
/// automatically.
/// </summary>
public sealed partial class GenerationCoordinator : IGenerationExit, IGenerationEvents, IAsyncDisposable
{
    private readonly GenerationRequests _requests;
    private readonly GenerationOptions _options;
    private readonly ILogger<GenerationCoordinator> _logger;

    private readonly Lock _gate = new();
    private readonly LinkedList<GenerationWork> _userQueue = new();
    private readonly LinkedList<GenerationWork> _backgroundQueue = new();
    private readonly Dictionary<GenerationTaskId, Running> _running = [];
    private readonly ConcurrentDictionary<MessageId, LiveAnswer> _live = new();
    private readonly StorageHealth _health;
    private readonly TerminalWrites _writes;
    private readonly AnswerRunner _answers;
    private readonly TaskRunners _tasks;
    private readonly IClock _clock;

    private bool _closed;
    private int _shutDown;

    public GenerationCoordinator(
        IConversationStore store,
        GenerationRequests requests,
        StorageHealth health,
        IClock clock,
        GenerationOptions options,
        ILogger<GenerationCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(health);
        _requests = requests;
        _health = health;
        _options = options;
        _logger = logger;
        _clock = clock;
        _writes = new TerminalWrites(health, logger);
        _answers = new AnswerRunner(store, requests, _writes, health, clock, options, this, logger);
        _tasks = new TaskRunners(store, requests, _writes, health, clock, this, logger);
        health.Changed += (_, _) =>
        {
            // Accepted work never sent waits while storage is blocked (D2) and starts once it recovers.
            Pump();
            RaiseChanged();
        };
    }

    /// <summary>Any queue, task or storage-health change. Raised on worker threads.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// An answer reached its terminal state in memory, the moment the reader sees it end: raised before the terminal write,
    /// so whatever depends on what was on screen then (the "new answer" mark) is not decided after a slow disk sync, when the
    /// reader may already have moved on (2026-11-06). Raised on a worker thread.
    /// </summary>
    public event EventHandler<AnswerOutcome>? AnswerEnded;

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

    /// <summary>
    /// Completes once every answer of the conversation that has ended in memory has also been through its terminal write
    /// (and its run has finished). An answer ends on screen before that write lands; a follow-up sent in between would be
    /// refused by the store (the stored answer is still streaming) and would plan from the stored, possibly partial, text
    /// (2026-11-06, slow disk syncs). Answers still running are not waited for.
    /// </summary>
    public Task AnswerWritesSettledAsync(ConversationId conversation)
    {
        Task[] ending;
        lock (_gate)
        {
            ending = [.. _running.Values
                .Where(r => r.Work is AnswerWork a && a.Task.ConversationId == conversation && !IsLive(a))
                .Select(r => r.Completion)];
        }

        return Task.WhenAll(ending);
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

    /// <summary>
    /// Runs one request whose output is only shown, never stored (<see cref="TransientWork"/>), in a slot like any other
    /// work the user started, and returns its complete text. Cancelling <paramref name="cancellationToken"/> takes it off
    /// the queue or stops it, and the returned task is then cancelled. Throws <see cref="InvalidOperationException"/> when
    /// new work is refused (an exit is under way), <see cref="ChatClientException"/> when the service failed or its output
    /// was cut off or empty, and <see cref="StorageAdmissionRefusedException"/> when storage paused before it was sent.
    /// </summary>
    internal async Task<string> RequestTransientAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var task = new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.NameSuggestion, default, null, request.Model, [], _clock.UtcNow);
        var work = new TransientWork(task, request);
        Enqueue(work);

        // Registered after the work is queued, so a stop always finds it (queued or running); a token already cancelled
        // runs the callback at once.
        using (cancellationToken.Register(() => _ = StopTaskAsync(task.Id)))
        {
            return await work.Output.Task.ConfigureAwait(false);
        }
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
            await _answers.CancelQueuedAsync(cancelled).ConfigureAwait(false);
            RaiseChanged();
        }
        else if (running is not null)
        {
            await running.Cancellation.CancelAsync().ConfigureAwait(false);
            await running.Completion.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops a summary, naming or transient task: a queued one becomes cancelled, a running one stops and stores nothing.
    /// </summary>
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
            await CancelQueuedTaskAsync(queued).ConfigureAwait(false);
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
            await CancelQueuedTaskAsync(work).ConfigureAwait(false);
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
                    await _answers.CancelQueuedAsync(answer).ConfigureAwait(false);
                    break;
                default:
                    await CancelQueuedTaskAsync(work).ConfigureAwait(false);
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
    public Task<bool> SaveUnsavedAsync() => _writes.FlushAsync(includeDefects: true);

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
        await _writes.FlushAsync(includeDefects: true).ConfigureAwait(false);
        RaiseChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _writes.Dispose();
    }

    /// <summary>
    /// Cancels a title, summary or naming task taken off a queue before it started: its row becomes cancelled, then a
    /// title is reported abandoned (asked for again after the next answer) and anything else reported finished.
    /// </summary>
    private async Task CancelQueuedTaskAsync(GenerationWork work)
    {
        if (work is TransientWork transient)
        {
            // No row to finish: only its caller is told.
            transient.Output.TrySetCanceled();
            return;
        }

        await _writes.SaveAsync(() => _requests.FinishTaskAsync(work.Task.Id, GenerationTaskState.Cancelled, null, null)).ConfigureAwait(false);
        if (work is TitleWork)
        {
            TitleAbandoned?.Invoke(this, work.Task.ConversationId);
        }
        else
        {
            SummaryTaskFinished?.Invoke(this, work.Task);
        }
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
                    await _answers.RunAsync(answer, running.Cancellation.Token).ConfigureAwait(false);
                    break;
                case TitleWork title:
                    await _tasks.RunTitleAsync(title, running.Cancellation.Token).ConfigureAwait(false);
                    break;
                case SummaryWork summary:
                    await _tasks.RunSummaryAsync(summary, running.Cancellation.Token).ConfigureAwait(false);
                    break;
                case NamingWork naming:
                    await _tasks.RunNamingAsync(naming, running.Cancellation.Token).ConfigureAwait(false);
                    break;
                case TransientWork transient:
                    await _tasks.RunTransientAsync(transient, running.Cancellation.Token).ConfigureAwait(false);
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
            // Never leaves a caller waiting, whatever ended the run (no-op when the runner already answered).
            (running.Work as TransientWork)?.Output.TrySetCanceled();
            lock (_gate)
            {
                _running.Remove(running.Work.Task.Id);
            }

            // The token source is not disposed: a concurrent StopAsync may still cancel it after the run ends.
            Pump();
            RaiseChanged();
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    void IGenerationEvents.Changed() => RaiseChanged();

    void IGenerationEvents.AnswerEnded(AnswerOutcome outcome) => AnswerEnded?.Invoke(this, outcome);

    void IGenerationEvents.AnswerFinished(AnswerOutcome outcome) => AnswerFinished?.Invoke(this, outcome);

    void IGenerationEvents.TitleSaved(ConversationId conversation) => TitleSaved?.Invoke(this, conversation);

    void IGenerationEvents.TitleAbandoned(ConversationId conversation) => TitleAbandoned?.Invoke(this, conversation);

    void IGenerationEvents.SummaryTaskFinished(GenerationTaskRecord task) => SummaryTaskFinished?.Invoke(this, task);

    private sealed class Running(GenerationWork work, CancellationTokenSource cancellation)
    {
        public GenerationWork Work { get; } = work;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Completion { get; set; } = Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} stopped tasks did not finish within the exit wait; their last checkpoints stand")]
    private static partial void LogStopTimedOut(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Generation task {TaskId} crashed")]
    private static partial void LogRunCrashed(ILogger logger, Exception exception, GenerationTaskId taskId);
}
