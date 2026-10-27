using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

public sealed partial class GenerationCoordinator
{
    private int checkpointRequested;
    private Task? growthCheckpoint;

    private async Task CheckpointLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                await FlushAsync(throwOnFailure: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void RequestCheckpoint()
    {
        // Bound background work: at most one growth-triggered flush awaits the serial persistence gate.
        if (Interlocked.Exchange(ref checkpointRequested, 1) != 0) return;
        growthCheckpoint = Task.Run(async () =>
        {
            try { await FlushAsync(throwOnFailure: false).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref checkpointRequested, 0); }
        });
    }

    private async Task FlushAsync(bool throwOnFailure, bool explicitRetry = false)
    {
        await persistence.WaitAsync().ConfigureAwait(false);
        var repairEpoch = localFailure.Epoch;
        try
        {
            lock (sync)
            {
                if (persistenceFailure && !explicitRetry)
                {
                    if (throwOnFailure) throw new InvalidOperationException("保存失败，请重试保存。");
                    return;
                }
            }
            // Capture each dirty revision, then save outside the memory lock. Chunks can continue to arrive.
            Job[] dirty;
            lock (sync) dirty = jobs.Where(job => job.Dirty || job.CompressionDirty || job.UnresolvedChunkKey is not null).ToArray();
            foreach (var job in dirty)
            {
                GenerationSnapshot snapshot;
                string? model;
                string title;
                // Stop may change the child while an older running checkpoint is in flight.
                // Capture the parent only when that exact memory state has no unsaved child.
                while (true)
                {
                    await FlushCompressionAsync(job).ConfigureAwait(false);
                    lock (sync)
                    {
                        if (job.CompressionDirty) continue;
                        snapshot = Snapshot(job); model = job.ActualModel; title = job.Body.ToString();
                        break;
                    }
                }
                lock (sync) if (!job.Dirty) continue;
                GenerationRecord? titleResult = null;
                if (snapshot.Kind == GenerationKind.Title && snapshot.Status == GenerationStatus.Completed)
                {
                    var current = (await store.LoadGenerationAsync(snapshot.TaskId).ConfigureAwait(false))!;
                    if (Active(current.Status))
                    {
                        // CompleteTitle owns its final revision increment. Bring its persisted running
                        // revision up to final-1 first so observers never see revision rollback.
                        if (current.Revision < snapshot.Revision - 1)
                            await store.WriteGenerationAsync(snapshot.TaskId, snapshot.Revision - 1, "", GenerationStatus.Running, actualModelId: model).ConfigureAwait(false);
                        await store.CompleteTitleAsync(snapshot.TaskId, title).ConfigureAwait(false);
                    }
                    titleResult = (await store.LoadGenerationAsync(snapshot.TaskId).ConfigureAwait(false))!;
                }
                else
                {
                    var saved = await store.WriteGenerationAsync(snapshot.TaskId, snapshot.Revision, snapshot.Body,
                        snapshot.Status, snapshot.Error, model).ConfigureAwait(false);
                    if (!saved)
                    {
                        var current = await store.LoadGenerationAsync(snapshot.TaskId).ConfigureAwait(false);
                        if (current is null || current.Status != snapshot.Status || current.Revision != snapshot.Revision)
                            throw new InvalidOperationException("生成任务保存冲突。");
                    }
                }
                lock (sync)
                {
                    job.PersistedLength = snapshot.Body.Length;
                    if (job.Revision == snapshot.Revision)
                    {
                        job.Dirty = false;
                        if (titleResult is not null)
                        { job.Status = titleResult.Status; job.Error = titleResult.ErrorCode; job.Revision = titleResult.Revision; }
                        if (snapshot.Kind == GenerationKind.Reply && snapshot.Status == GenerationStatus.Completed && accepting)
                            pendingTitles.Add(snapshot.ConversationId);
                    }
                }
            }
            if (explicitRetry)
            {
                localFailure.TryClear(repairEpoch);
                lock (sync) persistenceFailure = localFailure.HasFailure;
            }
        }
        catch
        {
            MarkPersistenceFailure();
            if (throwOnFailure) throw;
        }
        finally { persistence.Release(); Notify(); }
    }
}
