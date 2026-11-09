using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

/// <summary>
/// Runs the non-answer tasks in their slots: automatic titles, conversation summaries and summary-node naming. Holds no
/// scheduler state.
/// </summary>
internal sealed partial class TaskRunners(
    IConversationStore store,
    GenerationRequests requests,
    TerminalWrites writes,
    StorageHealth health,
    IClock clock,
    IGenerationEvents events,
    ILogger logger)
{
    private readonly IConversationStore _store = store;
    private readonly GenerationRequests _requests = requests;
    private readonly TerminalWrites _writes = writes;
    private readonly StorageHealth _health = health;
    private readonly IClock _clock = clock;
    private readonly IGenerationEvents _events = events;
    private readonly ILogger _logger = logger;

    public async Task RunTitleAsync(TitleWork work, CancellationToken stop)
    {
        var task = work.Task;
        if (await _writes.SaveAsync(() => _store.MarkRunningAsync(task.Id, null, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false) != SaveResult.Applied)
        {
            return;
        }

        var key = await _requests.ReadKeyAsync(task).ConfigureAwait(false);
        if (key.Error is { } keyError)
        {
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, keyError, key.Detail)).ConfigureAwait(false);
            return;
        }

        try
        {
            // Like summaries and naming, a title is used only from a normal, non-empty finish; a cut-off one is not.
            var (output, problem) = await _requests.RequestCompleteTextAsync(task, work.Request, key.Secret!, stop).ConfigureAwait(false);
            var title = output is null ? null : ConversationRules.CleanGeneratedTitle(output);
            if (title is not null)
            {
                if (await _writes.SaveAsync(() => _store.SetAutoTitleAsync(task.ConversationId, title, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false) == SaveResult.Applied)
                {
                    _events.TitleSaved(task.ConversationId);
                }
            }

            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, title is null ? GenerationTaskState.Failed : GenerationTaskState.Completed, title is null ? GenerationErrorKind.InvalidResponse : null, title is null ? problem : null))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Stopped, null, null)).ConfigureAwait(false);
            _events.TitleAbandoned(task.ConversationId);
        }
        catch (ChatClientException ex)
        {
            // Titles are best effort: keep the current name, never retry automatically.
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, ex.Kind, ex.Message)).ConfigureAwait(false);
        }
        catch (StorageAdmissionRefusedException ex)
        {
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message)).ConfigureAwait(false);
        }
    }

    public async Task RunSummaryAsync(SummaryWork work, CancellationToken stop)
    {
        var task = work.Task;
        try
        {
            if (await _writes.SaveAsync(() => _store.MarkRunningAsync(task.Id, null, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false) != SaveResult.Applied)
            {
                return;
            }

            var key = await _requests.ReadKeyAsync(task).ConfigureAwait(false);
            if (key.Error is { } keyError)
            {
                await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, keyError, key.Detail)).ConfigureAwait(false);
                return;
            }

            var request = work.Request;
            if (work.Plan is { IsCompressed: true } plan)
            {
                var prepared = await _requests.PrepareCompressedAsync(task, plan, work.Budget, key.Secret!, stop).ConfigureAwait(false);
                request = request with { Turns = [.. request.Turns.TakeWhile(t => t.Role == ChatRole.System), .. prepared.Turns] };
                await _requests.RecordCompressedInputAsync(task.Id, prepared.SummaryIds).ConfigureAwait(false);
            }

            var (output, problem) = await _requests.RequestCompleteTextAsync(task, request, key.Secret!, stop).ConfigureAwait(false);
            if (output is null)
            {
                await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, GenerationErrorKind.InvalidResponse, problem)).ConfigureAwait(false);
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

                await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message), retryOnFailure: true).ConfigureAwait(false);
                return;
            }

            if (saved == SummarySaveResult.Unusable)
            {
                await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, GenerationErrorKind.InvalidResponse, "总结缺少标题或正文。")).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Stopped, null, null)).ConfigureAwait(false);
        }
        catch (ContextCompressionException ex)
        {
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, GenerationErrorKind.CompressionFailed, ex.Message)).ConfigureAwait(false);
        }
        catch (ChatClientException ex)
        {
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, ex.Kind, ex.Message)).ConfigureAwait(false);
        }
        catch (StorageAdmissionRefusedException ex)
        {
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            _events.SummaryTaskFinished(task);
        }
    }

    public async Task RunNamingAsync(NamingWork work, CancellationToken stop)
    {
        var task = work.Task;
        try
        {
            if (await _writes.SaveAsync(() => _store.MarkRunningAsync(task.Id, null, _clock.UtcNow, CancellationToken.None)).ConfigureAwait(false) != SaveResult.Applied)
            {
                return;
            }

            var key = await _requests.ReadKeyAsync(task).ConfigureAwait(false);
            if (key.Error is { } keyError)
            {
                await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, keyError, key.Detail)).ConfigureAwait(false);
                return;
            }

            // Names are best effort: a failure keeps the current names and is never retried automatically.
            var (output, problem) = await _requests.RequestCompleteTextAsync(task, work.Request, key.Secret!, stop).ConfigureAwait(false);
            if (output is not null)
            {
                await work.Apply(output).ConfigureAwait(false);
            }

            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, output is null ? GenerationTaskState.Failed : GenerationTaskState.Completed, output is null ? GenerationErrorKind.InvalidResponse : null, problem))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Stopped, null, null)).ConfigureAwait(false);
        }
        catch (ChatClientException ex)
        {
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, ex.Kind, ex.Message)).ConfigureAwait(false);
        }
        catch (StorageAdmissionRefusedException ex)
        {
            await _writes.SaveAsync(() => _requests.FinishTaskAsync(task.Id, GenerationTaskState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            _events.SummaryTaskFinished(task);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Summary of task {TaskId} could not be stored")]
    private static partial void LogSummaryNotSaved(ILogger logger, Exception exception, GenerationTaskId taskId);
}
