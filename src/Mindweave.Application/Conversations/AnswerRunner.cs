using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

/// <summary>
/// Runs one answer in its slot: marks it running, reads the key, summarizes older context when the plan says so,
/// streams with periodic checkpoints, and makes the terminal write. Holds no scheduler state.
/// </summary>
internal sealed partial class AnswerRunner(
    IConversationStore store,
    GenerationRequests requests,
    TerminalWrites writes,
    StorageHealth health,
    IClock clock,
    GenerationOptions options,
    IGenerationEvents events,
    ILogger logger)
{
    private readonly IConversationStore _store = store;
    private readonly GenerationRequests _requests = requests;
    private readonly TerminalWrites _writes = writes;
    private readonly StorageHealth _health = health;
    private readonly IClock _clock = clock;
    private readonly GenerationOptions _options = options;
    private readonly IGenerationEvents _events = events;
    private readonly ILogger _logger = logger;

    /// <summary>Where a run gives its slot up while it waits for approval; without it, a run waits in its slot.</summary>
    internal IRunSlots? Slots { get; init; }

    public async Task RunAsync(AnswerWork work, CancellationToken stop)
    {
        try
        {
            await RunCoreAsync(work, stop).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Health check 2026-11-12: whatever ends a run, its answer ends too; one left streaming locks the conversation until restart.
        catch (Exception ex) when (work.Live.Snapshot().State is MessageState.Pending or MessageState.Streaming)
#pragma warning restore CA1031
        {
            var stopped = ex is OperationCanceledException && stop.IsCancellationRequested;
            if (!stopped)
            {
                LogUnexpectedFailure(_logger, ex, work.Task.Id);
            }

            await FinishAsync(work, stopped ? MessageState.Stopped : MessageState.Failed, stopped ? null : GenerationErrorKind.InvalidResponse, stopped ? null : UnexpectedDetail(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>What the answer says when its run failed in a way nobody foresaw.</summary>
    internal static string UnexpectedDetail(Exception ex) => $"Unexpected failure: {ex.Message}";

    private async Task RunCoreAsync(AnswerWork work, CancellationToken stop)
    {
        var live = work.Live;
        var task = work.Task;

        var marked = await _writes.SaveAsync(() => _store.MarkRunningAsync(task.Id, live.MessageId, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false);
        if (marked == SaveResult.NotApplied)
        {
            // Cancelled between dequeue and start; the cancellation already wrote the terminal state.
            return;
        }

        if (marked == SaveResult.Failed)
        {
            // Without a durable "running" row there is nowhere safe to put the answer: do not call the AI.
            await FinishAsync(work, MessageState.Failed, GenerationErrorKind.StorageUnavailable, null).ConfigureAwait(false);
            return;
        }

        live.MarkStreaming();
        _events.Changed();

        var key = await _requests.ReadKeyAsync(task).ConfigureAwait(false);
        if (key.Error is { } keyError)
        {
            await FinishAsync(work, MessageState.Failed, keyError, key.Detail).ConfigureAwait(false);
            return;
        }

        var request = work.Request;
        if (work.Plan is { } plan && (plan.IsCompressed || plan.Resources.Count > 0))
        {
            // Summaries (of older context, of attachments) are a prerequisite run in this slot, so the answer never waits for
            // a slot of its own making.
            if (plan.IsCompressed || plan.Resources.Any(r => r.NeedsSummary))
            {
                live.SetPhase(AnswerPhase.Compressing);
            }

            try
            {
                var (prepared, sent) = await _requests.PrepareContextAsync(task, plan, work.Budget, work.Inputs, key.Secret!, stop).ConfigureAwait(false);
                request = request with { Turns = prepared.Turns };
                live.SetPhase(AnswerPhase.Waiting);
                if (sent.IsCompressed)
                {
                    await _requests.RecordCompressedInputAsync(task.Id, prepared.SummaryIds).ConfigureAwait(false);
                }

                await _requests.RecordResourceInputAsync(task.Id, sent).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                await FinishAsync(work, MessageState.Stopped, null, null).ConfigureAwait(false);
                return;
            }
            catch (ContextCompressionException ex)
            {
                await FinishAsync(work, MessageState.Failed, GenerationErrorKind.CompressionFailed, ex.Message).ConfigureAwait(false);
                return;
            }
            catch (StorageAdmissionRefusedException ex)
            {
                await FinishAsync(work, MessageState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message).ConfigureAwait(false);
                return;
            }
        }

        if (!_health.TryAdmit())
        {
            // Storage failed while this task waited for its key or its summaries: the answer is not asked for (D2).
            await FinishAsync(work, MessageState.Failed, GenerationErrorKind.StorageUnavailable, StorageAdmissionRefusedException.Detail).ConfigureAwait(false);
            return;
        }

        using var checkpointStop = new CancellationTokenSource();
        var checkpoints = CheckpointLoopAsync(live, checkpointStop.Token);

        if (work.Run is { } run && _requests.Runs is { } services)
        {
            var ending = await RunAsync(work, run, services, request, key.Secret!, stop).ConfigureAwait(false);
            if (ending.State is MessageState.Complete or MessageState.Stopped && run.Scope.Sources.All.Count > 0)
            {
                // 出处 (requirements §10.4.5, L11): the numbers the text cites that name a source of this run, web or local, located in it.
                live.SetCitations(run.Scope.Sources.CitationsFor(live.Snapshot().Text));
            }

            await checkpointStop.CancelAsync().ConfigureAwait(false);
            await checkpoints.ConfigureAwait(false);
            await FinishAsync(work, ending.State, ending.Error, ending.Detail).ConfigureAwait(false);
            return;
        }

        MessageState final;
        GenerationErrorKind? error = null;
        string? detail = null;
        var trace = new RequestTrace(task.Id, _clock, request, Diagnostics.RequestPurpose.Answer);
        try
        {
            ChatStreamEvent.Completed? end = null;
            await foreach (var item in _requests.StreamAsync(request, key.Secret!, stop).ConfigureAwait(false))
            {
                trace.Observe(item);
                switch (item)
                {
                    case ChatStreamEvent.TextDelta delta:
                        live.Append(delta.Text);
                        break;
                    case ChatStreamEvent.ReasoningDelta:
                        // Only says the model is working; the reasoning is not shown or stored.
                        live.MarkThinking();
                        break;
                    case ChatStreamEvent.Completed done:
                        end = done;
                        break;
                }
            }

            var empty = string.IsNullOrWhiteSpace(live.Snapshot().Text);
            if (end is { Truncated: true })
            {
                // Cut off by the service (health check 2026-11-12): what came is kept with an incomplete mark and the
                // reason, as a stopped answer is; nothing at all is a failure to retry.
                final = empty ? MessageState.Failed : MessageState.Stopped;
                error = end.Filtered ? GenerationErrorKind.ContentFiltered : GenerationErrorKind.OutputLimit;
            }
            else if (end is not null && empty)
            {
                final = MessageState.Failed;
                error = GenerationErrorKind.InvalidResponse;
                detail = "The service finished without any text.";
            }
            else if (end is not null)
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
#pragma warning disable CA1031 // Anything else still ends the answer, stops its checkpoints and records the request (health check 2026-11-12).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnexpectedFailure(_logger, ex, task.Id);
            final = MessageState.Failed;
            error = GenerationErrorKind.InvalidResponse;
            detail = UnexpectedDetail(ex);
        }

        await checkpointStop.CancelAsync().ConfigureAwait(false);
        await checkpoints.ConfigureAwait(false);
        await FinishAsync(work, final, error, detail).ConfigureAwait(false);
        // A cut-off answer with text is recorded as truncated, not as stopped by the user or as failed.
        var userStopped = final == MessageState.Stopped && error is null;
        await _requests.RecordAsync(trace.Finish(userStopped, final == MessageState.Failed ? error : null, detail)).ConfigureAwait(false);
    }

    private async Task FinishAsync(AnswerWork work, MessageState state, GenerationErrorKind? error, string? detail)
    {
        var live = work.Live;
        var revision = live.Finish(state, error, detail);
        var snapshot = live.Snapshot();
        _events.AnswerEnded(new AnswerOutcome(live.ConversationId, live.MessageId, state, snapshot.Text));
        await _writes.SaveAsync(() => _store.FinishAnswerAsync(work.Task.Id, live.MessageId, snapshot.Blocks, revision, state, error, detail, _clock.UtcNow, CancellationToken.None), retryOnFailure: true)
            .ConfigureAwait(false);
        LogAnswerFinished(_logger, work.Task.Id, state, error);
        _events.AnswerFinished(new AnswerOutcome(live.ConversationId, live.MessageId, state, snapshot.Text));
    }

    public async Task CancelQueuedAsync(AnswerWork work)
    {
        var revision = work.Live.Finish(MessageState.Cancelled, null, null);
        await _writes.SaveAsync(
                () => _store.FinishAnswerAsync(work.Task.Id, work.Live.MessageId, ContentBlocks.FromText(string.Empty), revision, MessageState.Cancelled, null, null, _clock.UtcNow, CancellationToken.None),
                retryOnFailure: true)
            .ConfigureAwait(false);
        _events.AnswerFinished(new AnswerOutcome(work.Live.ConversationId, work.Live.MessageId, MessageState.Cancelled, string.Empty));
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
                if (await _writes.SaveAsync(() => _store.CheckpointAsync(live.MessageId, snapshot.Blocks, snapshot.Revision, CancellationToken.None)).ConfigureAwait(false) != SaveResult.Failed)
                {
                    written = snapshot.Revision;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Answer task {TaskId} failed unexpectedly")]
    private static partial void LogUnexpectedFailure(ILogger logger, Exception exception, GenerationTaskId taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Answer task {TaskId} finished as {State} ({Error})")]
    private static partial void LogAnswerFinished(ILogger logger, GenerationTaskId taskId, MessageState state, GenerationErrorKind? error);
}
