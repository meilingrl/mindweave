using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

public sealed class GenerationOptions
{
    /// <summary>Global limit on concurrent AI requests (architecture §6).</summary>
    public int Slots { get; init; } = 2;

    /// <summary>Minimum gap between partial-answer writes; only the newest text is written.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often terminal writes that failed are retried while storage is unavailable.</summary>
    public TimeSpan SaveRetryInterval { get; init; } = TimeSpan.FromSeconds(5);
}

public enum GenerationPriority
{
    User,
    Background,
}

/// <summary>Work accepted for generation; the request (model snapshot and turns) is already frozen.</summary>
public abstract record GenerationWork(GenerationTaskRecord Task, ChatRequest Request, GenerationPriority Priority);

public sealed record AnswerWork(GenerationTaskRecord Task, ChatRequest Request, LiveAnswer Live)
    : GenerationWork(Task, Request, GenerationPriority.User);

public sealed record TitleWork(GenerationTaskRecord Task, ChatRequest Request)
    : GenerationWork(Task, Request, GenerationPriority.Background);

public sealed record AnswerOutcome(ConversationId ConversationId, MessageId MessageId, MessageState State, string Body);

/// <summary>
/// Owns every AI request for the lifetime of the application, independent of any view: a two-slot
/// scheduler with user work ahead of background work, per-answer checkpoint loops, and guarded terminal
/// writes. A late chunk or checkpoint can never overwrite a terminal answer, and nothing is ever re-sent
/// automatically.
/// </summary>
public sealed partial class GenerationCoordinator : IAsyncDisposable
{
    private readonly IConversationStore _store;
    private readonly IChatClient _client;
    private readonly ICredentialStore _credentials;
    private readonly IClock _clock;
    private readonly GenerationOptions _options;
    private readonly ILogger<GenerationCoordinator> _logger;

    private readonly Lock _gate = new();
    private readonly LinkedList<GenerationWork> _userQueue = new();
    private readonly LinkedList<GenerationWork> _backgroundQueue = new();
    private readonly Dictionary<GenerationTaskId, Running> _running = [];
    private readonly ConcurrentDictionary<MessageId, LiveAnswer> _live = new();
    private readonly ConcurrentQueue<Func<Task<bool>>> _unsaved = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _retryLoop = Task.CompletedTask;
    private bool _closed;
    private int _storageBlocked;

    public GenerationCoordinator(
        IConversationStore store,
        IChatClient client,
        ICredentialStore credentials,
        IClock clock,
        GenerationOptions options,
        ILogger<GenerationCoordinator> logger)
    {
        _store = store;
        _client = client;
        _credentials = credentials;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <summary>Any queue, task or storage-health change. Raised on worker threads.</summary>
    public event EventHandler? Changed;

    /// <summary>An answer reached a terminal state (after its terminal write was attempted).</summary>
    public event EventHandler<AnswerOutcome>? AnswerFinished;

    /// <summary>True while a core write has failed and not yet succeeded on retry; new requests must be refused.</summary>
    public bool IsStorageBlocked => Volatile.Read(ref _storageBlocked) != 0;

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
                return _userQueue.Count + _running.Values.Count(r => r.Work is AnswerWork);
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
            return _userQueue.Any(w => w.Task.ConversationId == conversation)
                || _running.Values.Any(r => r.Work is AnswerWork a
                    && a.Task.ConversationId == conversation
                    && a.Live.Snapshot().State is MessageState.Pending or MessageState.Streaming);
        }
    }

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

    /// <summary>
    /// Exit sequence steps 3–4 (architecture §12.3): close the entry, cancel queued work, stop running work
    /// keeping received text, and wait until terminal states are written (one last retry if storage failed).
    /// </summary>
    public async Task ShutdownAsync()
    {
        List<GenerationWork> queued;
        List<Running> running;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            queued = [.. _userQueue, .. _backgroundQueue];
            _userQueue.Clear();
            _backgroundQueue.Clear();
            running = [.. _running.Values];
        }

        foreach (var work in queued)
        {
            if (work is AnswerWork answer)
            {
                await CancelQueuedAsync(answer).ConfigureAwait(false);
            }
            else
            {
                await SaveAsync(() => FinishTask(work.Task.Id, GenerationTaskState.Cancelled, null, null)).ConfigureAwait(false);
            }
        }

        foreach (var r in running)
        {
            await r.Cancellation.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(running.Select(r => r.Completion)).ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _retryLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await FlushUnsavedAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _lifetime.Dispose();
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
            while (!_closed && _running.Count < _options.Slots)
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

        using var checkpointStop = new CancellationTokenSource();
        var checkpoints = CheckpointLoopAsync(live, checkpointStop.Token);

        MessageState final;
        GenerationErrorKind? error = null;
        string? detail = null;
        try
        {
            var completed = false;
            await foreach (var item in _client.StreamAsync(work.Request, key.Secret!, stop).ConfigureAwait(false))
            {
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
        }

        await checkpointStop.CancelAsync().ConfigureAwait(false);
        await checkpoints.ConfigureAwait(false);
        await FinishAnswerAsync(work, final, error, detail).ConfigureAwait(false);
    }

    private async Task FinishAnswerAsync(AnswerWork work, MessageState state, GenerationErrorKind? error, string? detail)
    {
        var live = work.Live;
        var revision = live.Finish(state, error, detail);
        var body = live.Snapshot().Text;
        await SaveAsync(() => _store.FinishAnswerAsync(work.Task.Id, live.MessageId, body, revision, state, error, detail, _clock.UtcNow, CancellationToken.None), retryOnFailure: true)
            .ConfigureAwait(false);
        LogAnswerFinished(_logger, work.Task.Id, state, error);
        AnswerFinished?.Invoke(this, new AnswerOutcome(live.ConversationId, live.MessageId, state, body));
    }

    private async Task CancelQueuedAsync(AnswerWork work)
    {
        var revision = work.Live.Finish(MessageState.Cancelled, null, null);
        await SaveAsync(
                () => _store.FinishAnswerAsync(work.Task.Id, work.Live.MessageId, string.Empty, revision, MessageState.Cancelled, null, null, _clock.UtcNow, CancellationToken.None),
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
                if (await SaveAsync(() => _store.CheckpointAsync(live.MessageId, snapshot.Text, snapshot.Revision, CancellationToken.None)).ConfigureAwait(false) != SaveResult.Failed)
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

        var text = new System.Text.StringBuilder();
        try
        {
            var completed = false;
            await foreach (var item in _client.StreamAsync(work.Request, key.Secret!, stop).ConfigureAwait(false))
            {
                if (item is ChatStreamEvent.TextDelta delta)
                {
                    text.Append(delta.Text);
                }
                else if (item is ChatStreamEvent.Completed)
                {
                    completed = true;
                }
            }

            var title = completed ? ConversationRules.CleanGeneratedTitle(text.ToString()) : null;
            if (title is not null)
            {
                await SaveAsync(() => _store.SetAutoTitleAsync(task.ConversationId, title, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false);
            }

            await SaveAsync(() => FinishTask(task.Id, title is null ? GenerationTaskState.Failed : GenerationTaskState.Completed, title is null ? GenerationErrorKind.InvalidResponse : null, null))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Stopped, null, null)).ConfigureAwait(false);
        }
        catch (ChatClientException ex)
        {
            // Titles are best effort: keep the current name, never retry automatically.
            await SaveAsync(() => FinishTask(task.Id, GenerationTaskState.Failed, ex.Kind, ex.Message)).ConfigureAwait(false);
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
    /// Runs one core write. A failure blocks new requests until some write succeeds again; terminal writes
    /// are kept and retried in the background so a finished answer is not lost while the app runs.
    /// </summary>
    private async Task<SaveResult> SaveAsync(Func<Task<bool>> write, bool retryOnFailure = false)
    {
        try
        {
            var applied = await write().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _storageBlocked, 0) != 0 && _unsaved.IsEmpty)
            {
                RaiseChanged();
            }

            return applied ? SaveResult.Applied : SaveResult.NotApplied;
        }
#pragma warning disable CA1031 // Storage failures are a reported, recoverable state, not a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSaveFailed(_logger, ex);
            Interlocked.Exchange(ref _storageBlocked, 1);
            if (retryOnFailure)
            {
                _unsaved.Enqueue(write);
                EnsureRetryLoop();
            }

            RaiseChanged();
            return SaveResult.Failed;
        }
    }

    private void EnsureRetryLoop()
    {
        lock (_gate)
        {
            if (_retryLoop.IsCompleted && !_lifetime.IsCancellationRequested)
            {
                _retryLoop = Task.Run(async () =>
                {
                    while (!_unsaved.IsEmpty)
                    {
                        await Task.Delay(_options.SaveRetryInterval, _lifetime.Token).ConfigureAwait(false);
                        await FlushUnsavedAsync().ConfigureAwait(false);
                    }
                });
            }
        }
    }

    private async Task FlushUnsavedAsync()
    {
        var count = _unsaved.Count;
        for (var i = 0; i < count && _unsaved.TryDequeue(out var write); i++)
        {
            try
            {
                await write().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Still failing: keep it for the next attempt.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSaveFailed(_logger, ex);
                _unsaved.Enqueue(write);
            }
        }

        if (_unsaved.IsEmpty && Interlocked.Exchange(ref _storageBlocked, 0) != 0)
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Running(GenerationWork work, CancellationTokenSource cancellation)
    {
        public GenerationWork Work { get; } = work;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Completion { get; set; } = Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Answer task {TaskId} finished as {State} ({Error})")]
    private static partial void LogAnswerFinished(ILogger logger, GenerationTaskId taskId, MessageState state, GenerationErrorKind? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Core write failed; new AI requests are paused until storage recovers")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Generation task {TaskId} crashed")]
    private static partial void LogRunCrashed(ILogger logger, Exception exception, GenerationTaskId taskId);
}
