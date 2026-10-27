using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
namespace Mindweave.Application.Summaries;

public sealed partial class GroupNamingCoordinator
{
    public Task RetryPersistenceAsync() => Runtime.RetryPersistenceAsync();
    public Task RepairPersistenceAsync() => FlushAsync(true);
    private async Task FlushAsync(bool repair)
    {
        await control.WaitAsync().ConfigureAwait(false); var epoch = failure.Epoch;
        try
        {
            if (failure.HasFailure && !repair) throw new InvalidOperationException("命名保存失败，请重试保存。");
            if (repair)
            {
                if (configurationReadFailed) { _ = await chat.LoadActiveConfigurationAsync().ConfigureAwait(false); configurationReadFailed = false; }
                Group[] pendingGroups; lock (sync) pendingGroups = groups.Values.Where(g => g.Pending is not null).ToArray();
                foreach (var group in pendingGroups)
                {
                    var pending = group.Pending!;
                    if (pending.AcceptUnknown)
                    {
                        var record = await store.FindNodeNamingOperationAsync(pending.Intent.OperationId).ConfigureAwait(false);
                        if (record is not null)
                        {
                            if (record.Configuration != pending.Intent.Configuration || record.Snapshot.SnapshotHash != pending.Intent.Snapshot.SnapshotHash) throw new InvalidDataException("命名操作身份不一致。");
                            lock (sync) { Adopt(group, record, pending.Cancelled || !accepting); group.Pending = null; }
                        }
                        else
                        {
                            // Acceptance uncertainty is reconciled, never accepted a second time.
                            pending.Cancelled = true; pending.AcceptUnknown = false;
                        }
                    }
                    if (group.Pending is null) continue;
                    if (!pending.Saved)
                    {
                        var durable = (await store.ListNodeNamingIntentsAsync().ConfigureAwait(false)).SingleOrDefault(i => i.OperationId == pending.Intent.OperationId);
                        if (durable is null)
                        {
                            if (pending.ReplacedOperation is { } replaced) await store.ClearNodeNamingIntentAsync(pending.Intent.Snapshot.WorkspaceId, pending.Intent.Snapshot.GroupKey, replaced).ConfigureAwait(false);
                            lock (sync) group.Pending = null; continue;
                        }
                        if (durable.Snapshot.SnapshotHash != pending.Intent.Snapshot.SnapshotHash || durable.Configuration != pending.Intent.Configuration) throw new InvalidDataException("命名待处理身份不一致。");
                        pending.Saved = true;
                    }
                    if (pending.Cancelled || !accepting)
                    {
                        await store.ClearNodeNamingIntentAsync(pending.Intent.Snapshot.WorkspaceId, pending.Intent.Snapshot.GroupKey, pending.Intent.OperationId).ConfigureAwait(false);
                        lock (sync) group.Pending = null;
                    }
                }
            }
            Job[] snapshot; lock (sync) snapshot = jobs.ToArray();
            foreach (var job in snapshot)
            {
                if (repair && job.PreparationFailed && !job.Stopped && Active(job.Record.Status)) await PrepareAsync(job).ConfigureAwait(false);
                if (job.Completion is { } completion)
                {
                    var result = await store.CompleteNodeNamingAsync(completion).ConfigureAwait(false);
                    if (Success(result.Kind))
                    {
                        lock (sync) { job.Record = job.Record with { Status = GenerationStatus.Completed, Revision = checked(completion.ExpectedRevision + 1), ErrorCode = null }; job.Completion = null; job.Dirty = false; job.Prepared = null; job.Demand?.Dispose(); }
                    }
                    else await ReconcileAsync(job, result.Code ?? "NamingCompletionRejected").ConfigureAwait(false);
                }
                if (job.Dirty) await PersistTerminalAsync(job).ConfigureAwait(false);
            }
            if (repair) failure.TryClear(epoch);
        }
        catch { failure.Report(); throw; }
        finally { control.Release(); Notify(); }
        Job[] finished; lock (sync) finished = jobs.Where(j => !Active(j.Record.Status) && !j.Busy).ToArray();
        foreach (var job in finished) await FinishGroupAsync(job).ConfigureAwait(false);
        Dispatch();
    }
    private async Task PersistTerminalAsync(Job job)
    {
        var status = job.Record.Status; var error = job.Record.ErrorCode;
        var result = await store.UpdateNodeNamingMetadataAsync(new(job.Record.Id, job.Record.Revision, status, error)).ConfigureAwait(false);
        if (Success(result.Kind) && result.Value is { } saved) { lock (sync) { job.Record = saved; job.Dirty = false; job.Prepared = null; } return; }
        var current = await store.LoadNodeNamingJobAsync(job.Record.Id).ConfigureAwait(false);
        if (current is not null && Active(current.Status))
        {
            result = await store.UpdateNodeNamingMetadataAsync(new(current.Id, current.Revision, status, error)).ConfigureAwait(false);
            if (!Success(result.Kind) || result.Value is null) throw new InvalidDataException("命名终态无法核实。"); current = result.Value;
        }
        lock (sync) { job.Record = current ?? job.Record with { Status = GenerationStatus.Failed, ErrorCode = "NamingTaskMissing" }; job.Dirty = false; job.Completion = null; job.Prepared = null; }
    }
    private async Task ReconcileAsync(Job job, string code)
    {
        var current = await store.LoadNodeNamingJobAsync(job.Record.Id).ConfigureAwait(false);
        lock (sync)
        {
            job.Completion = null; job.Prepared = null; job.Demand?.Dispose();
            if (current is null) { job.Record = job.Record with { Status = GenerationStatus.Failed, ErrorCode = "NamingTaskMissing" }; job.Dirty = false; }
            else { job.Record = Active(current.Status) ? current with { Status = job.Stopped ? GenerationStatus.Stopped : GenerationStatus.Failed, ErrorCode = job.Stopped ? "Stopped" : code } : current; job.Dirty = Active(current.Status); }
        }
        if (job.Dirty) await PersistTerminalAsync(job).ConfigureAwait(false);
    }
    public async Task ShutdownAsync()
    {
        // Stamp cancellation before IO or waiting for notification/preparation tails. Return never clears these intents.
        lock (sync)
        {
            accepting = false; lifetime++; deferred.Clear();
            foreach (var g in groups.Values) if (g.Pending is { } p) p.Cancelled = true;
            foreach (var j in jobs) Stop(j);
        }
        await FlushAsync(true).ConfigureAwait(false);
        Task[] pending; lock (sync) pending = tails.ToArray(); await Task.WhenAll(pending.Select(IgnoreObservedFailure)).ConfigureAwait(false);
        await FlushAsync(true).ConfigureAwait(false);
    }
    private static async Task IgnoreObservedFailure(Task task) { try { await task.ConfigureAwait(false); } catch {/* The failure source and retained state remain authoritative. */} }
    public async ValueTask DisposeAsync()
    {
        lock (sync) if (disposed) return;
        await ShutdownAsync().ConfigureAwait(false); lock (sync) disposed = true; Runtime.Changed -= RuntimeChanged; registration.Dispose();
        foreach (var job in jobs) { job.Demand?.Dispose(); job.Cancellation.Dispose(); }
    }
}
